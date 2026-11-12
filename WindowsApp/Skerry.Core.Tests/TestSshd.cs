using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Skerry.Core.Tests;

/// <summary>
/// Standalone, minimal sshd instance on a random high port with its own
/// configuration file (does not read the system sshd_config). Used by SSH/SFTP integration tests.
/// </summary>
public sealed class TestSshd : IDisposable
{
    private const int StartupAttempts = 5;
    private const int SigTerm = 15;
    private const int SigKill = 9;
    private const int NoSuchProcess = 3;

    private static readonly TimeSpan KeygenTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProcessStopTimeout = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly DirectoryInfo _directory;
    private readonly ProcessGroupState _processGroupState;

    /// <summary>Gets the local TCP port used by the test SSH server.</summary>
    public int Port { get; }

    /// <summary>Gets the path to the private key used by the test SSH client.</summary>
    public string ClientKeyPath { get; }

    /// <summary>Creates a fixture that owns the running SSH server and its temporary files.</summary>
    /// <param name="process">The running SSH daemon process-group leader.</param>
    /// <param name="directory">The temporary directory containing the daemon configuration and keys.</param>
    /// <param name="port">The local TCP port bound by the daemon.</param>
    /// <param name="clientKeyPath">The private key path used by test clients.</param>
    private TestSshd(Process process, DirectoryInfo directory, int port, string clientKeyPath, ProcessGroupState processGroupState)
    {
        _process = process;
        _directory = directory;
        _processGroupState = processGroupState;
        Port = port;
        ClientKeyPath = clientKeyPath;
    }

    /// <summary>
    /// Starts the Linux SSH fixture when the required OpenSSH tools are available.
    /// The daemon runs unprivileged on a high port and authenticates the current user.
    /// Local environments may skip the fixture when setup is unavailable; CI fails instead.
    /// </summary>
    /// <returns>The running SSH fixture, or <see langword="null"/> when it is unavailable locally.</returns>
    /// <exception cref="InvalidOperationException">The fixture cannot be started in CI.</exception>
    public static TestSshd? Start()
    {
        var fixture = StartOptional();
        if (fixture is null && string.Equals(
                Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Failed to start the test SSH server in CI. Check that openssh-server and ssh-keygen are installed.");
        }

        return fixture;
    }

    /// <summary>Attempts to start the Linux-only SSH fixture without enforcing CI failure semantics.</summary>
    /// <returns>The running fixture, or <see langword="null"/> when OpenSSH is unavailable or startup fails.</returns>
    private static TestSshd? StartOptional()
    {
        // A non-privileged high port, an explicit host key, and disabled PAM let
        // OpenSSH run as the test user without granting the test process sudo access.
        if (!OperatingSystem.IsLinux() ||
            !File.Exists("/usr/sbin/sshd") ||
            !File.Exists("/usr/bin/setsid") ||
            !File.Exists("/usr/bin/ssh-keyscan"))
        {
            return null;
        }

        DirectoryInfo? directory = Directory.CreateTempSubdirectory("skerry-sshd-");
        var dir = directory.FullName;
        Process? server = null;
        ProcessGroupState? serverState = null;

        try
        {
            var hostKey = Path.Combine(dir, "hostkey");
            if (!RunKeygen(hostKey))
            {
                return null;
            }

            var clientKey = Path.Combine(dir, "client_key");
            if (!RunKeygen(clientKey))
            {
                return null;
            }

            var authorizedKeys = Path.Combine(dir, "authorized_keys");
            File.Copy(clientKey + ".pub", authorizedKeys);

            var configPath = Path.Combine(dir, "sshd_config");
            for (var attempt = 0; attempt < StartupAttempts; attempt++)
            {
                var port = FindAvailablePort();
                File.WriteAllText(configPath,
                    $"Port {port}\nListenAddress 127.0.0.1\nAuthorizedKeysFile {QuoteConfigPath(authorizedKeys)}\n" +
                    "PubkeyAuthentication yes\nPasswordAuthentication no\nKbdInteractiveAuthentication no\nUsePAM no\nStrictModes no\n" +
                    $"Subsystem sftp internal-sftp\nPidFile {QuoteConfigPath(Path.Combine(dir, "pid"))}\n");

                var psi = new ProcessStartInfo("/usr/bin/setsid")
                {
                    UseShellExecute = false,
                    // Do not redirect daemon output into unread pipes: a full pipe
                    // could block sshd and make the integration tests hang.
                };
                psi.ArgumentList.Add("/usr/sbin/sshd");
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add(configPath);
                psi.ArgumentList.Add("-h");
                psi.ArgumentList.Add(hostKey);
                psi.ArgumentList.Add("-D");
                psi.ArgumentList.Add("-e");

                server = Process.Start(psi);
                if (server is null)
                {
                    continue;
                }

                serverState = new ProcessGroupState(server.Id);
                if (WaitForServer(server, port, hostKey))
                {
                    var processSnapshot = ReadCapturedProcessSnapshot(server.Id);
                    if (processSnapshot is not null && processSnapshot.Value.ProcessGroupId == server.Id)
                    {
                        serverState.StartTime = processSnapshot.Value.StartTime;
                        var fixture = new TestSshd(server, directory, port, clientKey, serverState);
                        server = null;       // Ownership transfers to the fixture.
                        serverState = null;
                        directory = null;    // The fixture now owns the directory.
                        return fixture;
                    }
                }

                StopProcessGroup(server, serverState);
                server.Dispose();
                server = null;
                serverState = null;
            }

            return null;
        }
        catch (Win32Exception)
        {
            // OpenSSH tools may be missing or unavailable on a local host.
            return null;
        }
        finally
        {
            var serverStopped = server is null;
            try
            {
                if (server is not null)
                {
                    serverState ??= new ProcessGroupState(server.Id);
                    StopProcessGroup(server, serverState);
                    serverStopped = true;
                }
            }
            finally
            {
                try
                {
                    server?.Dispose();
                }
                finally
                {
                    // Keep the configuration and keys if any daemon process could
                    // still be alive and using them.
                    if (directory is not null && serverStopped)
                    {
                        TryDeleteDirectory(directory);
                    }
                }
            }
        }
    }

    /// <summary>Finds an unused loopback TCP port for one SSH daemon startup attempt.</summary>
    /// <returns>A currently available ephemeral port; the daemon bind is still retried if it races.</returns>
    private static int FindAvailablePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    /// <summary>Waits until the daemon serves the generated host key, not merely any listener on the port.</summary>
    /// <param name="server">The daemon process.</param>
    /// <param name="port">The candidate port.</param>
    /// <param name="hostKeyPath">The private host-key path whose public key must be served.</param>
    /// <returns><see langword="true"/> only when the expected SSH host key is observed.</returns>
    private static bool WaitForServer(Process server, int port, string hostKeyPath)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < StartupTimeout)
        {
            if (server.HasExited)
            {
                return false;
            }

            if (HasExpectedHostKey(port, hostKeyPath) && !server.HasExited)
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    /// <summary>Scans the candidate port and compares its SSH host key with this fixture's generated key.</summary>
    /// <param name="port">The candidate port.</param>
    /// <param name="hostKeyPath">The private host-key path.</param>
    /// <returns><see langword="true"/> when the listener presents the fixture's Ed25519 key.</returns>
    private static bool HasExpectedHostKey(int port, string hostKeyPath)
    {
        var psi = new ProcessStartInfo("/usr/bin/ssh-keyscan")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-4");
        psi.ArgumentList.Add("-T");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add("ed25519");
        psi.ArgumentList.Add("127.0.0.1");

        using var scan = Process.Start(psi);
        if (scan is null)
        {
            return false;
        }

        if (!scan.WaitForExit(1500))
        {
            StopProcess(scan);
            return false;
        }

        var output = scan.StandardOutput.ReadToEnd();
        _ = scan.StandardError.ReadToEnd();
        if (scan.ExitCode != 0)
        {
            return false;
        }

        var expected = File.ReadAllText(hostKeyPath + ".pub")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (expected.Length < 2)
        {
            return false;
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var actual = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (actual.Length >= 3 &&
                string.Equals(actual[1], expected[0], StringComparison.Ordinal) &&
                string.Equals(actual[2], expected[1], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Quotes and escapes a filesystem path for use as one OpenSSH configuration argument.</summary>
    /// <param name="path">The path to encode.</param>
    /// <returns>The path enclosed in OpenSSH-compatible double quotes.</returns>
    private static string QuoteConfigPath(string path) =>
        "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Generates an unencrypted Ed25519 key pair for the test fixture.</summary>
    /// <param name="path">The destination path for the private key.</param>
    /// <returns><see langword="true"/> when key generation succeeds before the timeout; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="Win32Exception">The key-generation process cannot be started.</exception>
    private static bool RunKeygen(string path)
    {
        var psi = new ProcessStartInfo("ssh-keygen")
        {
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add("-N");
        psi.ArgumentList.Add("");
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add("ed25519");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(path);

        using var process = Process.Start(psi);
        if (process is null)
        {
            return false;
        }

        if (!process.WaitForExit((int)KeygenTimeout.TotalMilliseconds))
        {
            StopProcess(process);
            return false;
        }

        return process.ExitCode == 0;
    }

    /// <summary>Stops a short-lived child process such as ssh-keygen or ssh-keyscan.</summary>
    /// <param name="process">The child process to stop.</param>
    /// <exception cref="InvalidOperationException">The process does not exit after termination is requested.</exception>
    private static void StopProcess(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            return;
        }

        if (!process.WaitForExit(2000))
        {
            throw new InvalidOperationException($"Process {process.Id} did not exit after termination was requested.");
        }
    }

    /// <summary>Stops the daemon and all captured descendants, including children in separate sessions.</summary>
    /// <param name="process">The daemon process started through <c>setsid</c>; its PID is the process-group ID.</param>
    /// <exception cref="InvalidOperationException">A descendant remains alive after termination is requested.</exception>
    /// <exception cref="Win32Exception">The process tree cannot be inspected or signalled.</exception>
    private static void StopProcessGroup(Process process, ProcessGroupState state)
    {
        var processGroupId = state.ProcessGroupId;
        var root = ReadCapturedProcessSnapshot(processGroupId);

        if (state.StartTime is null)
        {
            if (process.HasExited)
            {
                return;
            }

            if (root is null || root.Value.ProcessGroupId != processGroupId)
            {
                // setsid has not established an isolated group yet. Never signal
                // the caller's process group while startup is still in progress.
                StopProcess(process);
                return;
            }

            state.StartTime = root.Value.StartTime;
        }

        var processGroupStartTime = state.StartTime.Value;
        var groupIdentityMatches = root is null || root.Value.StartTime == processGroupStartTime;
        if (groupIdentityMatches && root is not null)
        {
            if (root.Value.ProcessGroupId != processGroupId)
            {
                throw new InvalidOperationException(
                    $"SSH daemon process {processGroupId} no longer belongs to its isolated process group.");
            }

            // Capture descendants before signaling the listener. OpenSSH may move
            // connection children into separate process groups during teardown.
            state.AddDescendants(GetProcessTree(processGroupId));
        }

        // Keep the captured identities in state across retries. If the listener
        // exits during a failed stop, a later finally-path retry must still
        // terminate and verify descendants that have moved to other groups.
        if (groupIdentityMatches)
        {
            SignalProcessGroup(processGroupId, processGroupStartTime, SigTerm);
        }

        SignalProcesses(state.Descendants, SigTerm);

        if (!WaitForShutdown(process, processGroupId, processGroupStartTime, state.Descendants, ProcessStopTimeout))
        {
            if (groupIdentityMatches)
            {
                SignalProcessGroup(processGroupId, processGroupStartTime, SigKill);
            }

            SignalProcesses(state.Descendants, SigKill);
            if (!WaitForShutdown(process, processGroupId, processGroupStartTime, state.Descendants, ProcessStopTimeout))
            {
                throw new InvalidOperationException(
                    $"SSH daemon process group {processGroupId} or one of its descendants is still running after SIGKILL.");
            }
        }
    }

    /// <summary>Captures the daemon's current descendants with process start times to avoid PID-reuse mistakes.</summary>
    /// <param name="rootProcessId">The daemon process ID.</param>
    /// <returns>A snapshot of all descendants visible in /proc.</returns>
    private static List<ProcessSnapshot> GetProcessTree(int rootProcessId)
    {
        var processes = new Dictionary<int, ProcessSnapshot>();
        foreach (var processDirectory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(processDirectory), NumberStyles.None, CultureInfo.InvariantCulture, out var processId))
            {
                continue;
            }

            var snapshot = TryReadProcessSnapshot(processId);
            if (snapshot is not null)
            {
                processes[processId] = snapshot.Value;
            }
        }

        var childrenByParent = processes.Values
            .GroupBy(snapshot => snapshot.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.ToArray());

        var descendants = new List<ProcessSnapshot>();
        var visited = new HashSet<int> { rootProcessId };
        var pending = new Stack<int>();
        pending.Push(rootProcessId);

        while (pending.Count > 0)
        {
            var parentProcessId = pending.Pop();
            if (!childrenByParent.TryGetValue(parentProcessId, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (visited.Add(child.ProcessId))
                {
                    descendants.Add(child);
                    pending.Push(child.ProcessId);
                }
            }
        }

        return descendants;
    }

    /// <summary>Reads a process identity, returning null only when the process disappears during inspection.</summary>
    /// <param name="processId">The process ID.</param>
    /// <returns>The process identity, or <see langword="null"/> when the process no longer exists.</returns>
    /// <exception cref="InvalidOperationException">The process stat data is malformed.</exception>
    private static ProcessSnapshot? ReadProcessSnapshot(int processId)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{processId}/stat");
            var closingParenthesis = stat.LastIndexOf(')');
            if (closingParenthesis < 0)
            {
                throw new InvalidOperationException($"Process {processId} has malformed /proc stat data.");
            }

            var fields = stat[(closingParenthesis + 2)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length <= 19 ||
                !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentProcessId) ||
                !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var processGroupId) ||
                !long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var startTime))
            {
                throw new InvalidOperationException($"Process {processId} has malformed /proc stat fields.");
            }

            return new ProcessSnapshot(processId, parentProcessId, processGroupId, fields[0][0], startTime);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Reads a captured process identity and fails if the process cannot be inspected.</summary>
    /// <param name="processId">The captured process ID.</param>
    /// <returns>The process identity, or <see langword="null"/> when the process has exited.</returns>
    /// <exception cref="InvalidOperationException">The process cannot be inspected reliably.</exception>
    private static ProcessSnapshot? ReadCapturedProcessSnapshot(int processId)
    {
        try
        {
            return ReadProcessSnapshot(processId);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException($"Unable to inspect captured process {processId}.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException($"Access denied while inspecting captured process {processId}.", exception);
        }
    }

    /// <summary>Best-effort process inspection for unrelated entries discovered while enumerating /proc.</summary>
    /// <param name="processId">The process ID.</param>
    /// <returns>The process identity, or <see langword="null"/> when it disappears or cannot be inspected.</returns>
    private static ProcessSnapshot? TryReadProcessSnapshot(int processId)
    {
        try
        {
            return ReadProcessSnapshot(processId);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Sends a signal to every captured descendant that still has the same process identity.</summary>
    /// <param name="processes">The process snapshots to signal.</param>
    /// <param name="signal">The POSIX signal number.</param>
    private static void SignalProcesses(IEnumerable<ProcessSnapshot> processes, int signal)
    {
        foreach (var process in processes)
        {
            SignalProcess(process, signal);
        }
    }

    /// <summary>Sends a signal only when the PID still refers to the captured process, not a reused PID.</summary>
    /// <param name="process">The captured process identity.</param>
    /// <param name="signal">The POSIX signal number.</param>
    private static void SignalProcess(ProcessSnapshot process, int signal)
    {
        if (!IsSameLiveProcess(process))
        {
            return;
        }

        if (NativeKill(process.ProcessId, signal) == 0)
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error != NoSuchProcess)
        {
            throw new Win32Exception(error, $"Failed to signal SSH fixture descendant {process.ProcessId}.");
        }
    }

    /// <summary>Signals a process group, treating an already-exited group as successfully stopped.</summary>
    /// <param name="processGroupId">The process-group ID.</param>
    /// <param name="signal">The POSIX signal number.</param>
    private static void SignalProcessGroup(int processGroupId, long processGroupStartTime, int signal)
    {
        var leader = ReadCapturedProcessSnapshot(processGroupId);
        if (leader is not null && leader.Value.StartTime != processGroupStartTime)
        {
            // The original group has exited and its numeric ID has been reused.
            return;
        }

        if (NativeKill(-processGroupId, signal) == 0)
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error != NoSuchProcess)
        {
            throw new Win32Exception(error, $"Failed to signal SSH daemon process group {processGroupId}.");
        }
    }

    /// <summary>Checks whether the captured process still exists with the same start time and is not a zombie.</summary>
    /// <param name="process">The captured process identity.</param>
    /// <returns><see langword="true"/> when the same process is still live.</returns>
    private static bool IsSameLiveProcess(ProcessSnapshot process)
    {
        var current = ReadCapturedProcessSnapshot(process.ProcessId);
        return current is not null &&
               current.Value.StartTime == process.StartTime &&
               current.Value.State is not ('Z' or 'X');
    }

    /// <summary>Checks whether the daemon's isolated process group still has a live member.</summary>
    /// <param name="processGroupId">The process-group ID.</param>
    /// <returns><see langword="true"/> when a non-zombie process remains in the group.</returns>
    private static bool IsProcessGroupRunning(int processGroupId, long processGroupStartTime)
    {
        var leader = ReadCapturedProcessSnapshot(processGroupId);
        if (leader is not null && leader.Value.StartTime != processGroupStartTime)
        {
            // The original group has exited and its numeric ID has been reused.
            return false;
        }

        foreach (var processDirectory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(processDirectory), NumberStyles.None, CultureInfo.InvariantCulture, out var processId))
            {
                continue;
            }

            var snapshot = TryReadProcessSnapshot(processId);
            if (snapshot is not null &&
                snapshot.Value.ProcessGroupId == processGroupId &&
                snapshot.Value.State is not ('Z' or 'X'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Waits until the daemon, its process group, and every captured descendant have exited.</summary>
    /// <param name="process">The daemon process handle.</param>
    /// <param name="processGroupId">The isolated process-group ID.</param>
    /// <param name="processGroupStartTime">The start time identifying the original process group.</param>
    /// <param name="descendants">The captured descendant identities.</param>
    /// <param name="timeout">The maximum wait duration.</param>
    /// <returns><see langword="true"/> when no live daemon process or captured descendant remains.</returns>
    private static bool WaitForShutdown(
        Process process,
        int processGroupId,
        long processGroupStartTime,
        IReadOnlyCollection<ProcessSnapshot> descendants,
        TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (IsShutdownComplete(process, processGroupId, processGroupStartTime, descendants))
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return IsShutdownComplete(process, processGroupId, processGroupStartTime, descendants);
    }

    /// <summary>Checks all known process identities before allowing fixture files to be deleted.</summary>
    /// <param name="process">The daemon process handle.</param>
    /// <param name="processGroupId">The isolated process-group ID.</param>
    /// <param name="processGroupStartTime">The start time identifying the original process group.</param>
    /// <param name="descendants">The captured descendant identities.</param>
    /// <returns><see langword="true"/> when all known daemon processes have exited.</returns>
    private static bool IsShutdownComplete(
        Process process,
        int processGroupId,
        long processGroupStartTime,
        IReadOnlyCollection<ProcessSnapshot> descendants)
    {
        if (!process.HasExited || IsProcessGroupRunning(processGroupId, processGroupStartTime))
        {
            return false;
        }

        return !descendants.Any(IsSameLiveProcess);
    }

    /// <summary>Attempts to remove the fixture's temporary directory without masking an earlier failure.</summary>
    /// <param name="directory">The temporary directory to delete.</param>
    private static void TryDeleteDirectory(DirectoryInfo directory)
    {
        try
        {
            if (directory.Exists)
            {
                directory.Delete(recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; do not hide the original fixture failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; do not hide the original fixture failure.
        }
    }

    /// <summary>Stops the SSH daemon and releases its process handle and temporary files.</summary>
    public void Dispose()
    {
        var stopped = false;
        try
        {
            StopProcessGroup(_process, _processGroupState);
            stopped = true;
        }
        finally
        {
            _process.Dispose();
            if (stopped)
            {
                TryDeleteDirectory(_directory);
            }
        }
    }

    /// <summary>Retains the identity and captured descendants of a daemon process group across stop attempts.</summary>
    private sealed class ProcessGroupState
    {
        public ProcessGroupState(int processGroupId) => ProcessGroupId = processGroupId;

        public int ProcessGroupId { get; }

        public long? StartTime { get; set; }

        public List<ProcessSnapshot> Descendants { get; } = new();

        public void AddDescendants(IEnumerable<ProcessSnapshot> descendants)
        {
            foreach (var descendant in descendants)
            {
                if (!Descendants.Any(existing =>
                        existing.ProcessId == descendant.ProcessId &&
                        existing.StartTime == descendant.StartTime))
                {
                    Descendants.Add(descendant);
                }
            }
        }
    }

    /// <summary>Identifies a process without relying on a PID that the operating system may reuse.</summary>
    /// <param name="ProcessId">The process ID.</param>
    /// <param name="ParentProcessId">The parent process ID.</param>
    /// <param name="ProcessGroupId">The process-group ID.</param>
    /// <param name="State">The process state from /proc.</param>
    /// <param name="StartTime">The process start time from /proc.</param>
    private readonly record struct ProcessSnapshot(
        int ProcessId,
        int ParentProcessId,
        int ProcessGroupId,
        char State,
        long StartTime);

    /// <summary>Invokes the POSIX kill system call for a process or process group.</summary>
    /// <param name="processId">A positive PID or a negative process-group ID.</param>
    /// <param name="signal">The POSIX signal number.</param>
    /// <returns>Zero on success; otherwise, -1 and a native error code.</returns>
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int NativeKill(int processId, int signal);
}
