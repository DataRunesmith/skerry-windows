using System.ComponentModel;
using System.Diagnostics;
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
    private const int PermissionDenied = 1;

    private static readonly TimeSpan KeygenTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProcessStopTimeout = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly DirectoryInfo _directory;

    /// <summary>Gets the local TCP port used by the test SSH server.</summary>
    public int Port { get; }

    /// <summary>Gets the path to the private key used by the test SSH client.</summary>
    public string ClientKeyPath { get; }

    /// <summary>Creates a fixture that owns the running SSH server and its temporary files.</summary>
    /// <param name="process">The running SSH daemon process-group leader.</param>
    /// <param name="directory">The temporary directory containing the daemon configuration and keys.</param>
    /// <param name="port">The local TCP port bound by the daemon.</param>
    /// <param name="clientKeyPath">The private key path used by test clients.</param>
    private TestSshd(Process process, DirectoryInfo directory, int port, string clientKeyPath)
    {
        _process = process;
        _directory = directory;
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
        // The fixture uses a non-privileged high port, disables PAM, and supplies
        // its own host key. OpenSSH can therefore run as the test user without sudo.
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

                if (WaitForServer(server, port, hostKey))
                {
                    var fixture = new TestSshd(server, directory, port, clientKey);
                    server = null;    // Ownership transfers to the fixture.
                    directory = null; // The fixture now owns the directory.
                    return fixture;
                }

                StopProcessGroup(server);
                server.Dispose();
                server = null;
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
                    StopProcessGroup(server);
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
        psi.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

    /// <summary>Signals every process in the isolated SSH daemon process group and verifies that the group exits.</summary>
    /// <param name="process">The daemon process started through <c>setsid</c>; its PID is the process-group ID.</param>
    /// <exception cref="InvalidOperationException">A descendant remains alive after termination is requested.</exception>
    /// <exception cref="Win32Exception">The process group cannot be inspected or signalled.</exception>
    private static void StopProcessGroup(Process process)
    {
        var processGroupId = process.Id;
        SignalProcessGroup(processGroupId, SigTerm);

        if (!WaitForProcessGroupExit(processGroupId, ProcessStopTimeout))
        {
            SignalProcessGroup(processGroupId, SigKill);
            if (!WaitForProcessGroupExit(processGroupId, ProcessStopTimeout))
            {
                throw new InvalidOperationException(
                    $"SSH daemon process group {processGroupId} still has live processes after SIGKILL.");
            }
        }

        if (!process.HasExited && !process.WaitForExit((int)ProcessStopTimeout.TotalMilliseconds))
        {
            throw new InvalidOperationException($"SSH daemon process {processGroupId} did not exit.");
        }
    }

    /// <summary>Sends a signal to a process group, treating an already-exited group as successfully stopped.</summary>
    /// <param name="processGroupId">The process-group ID.</param>
    /// <param name="signal">The POSIX signal number.</param>
    private static void SignalProcessGroup(int processGroupId, int signal)
    {
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

    /// <summary>Checks whether a process group still contains any process.</summary>
    /// <param name="processGroupId">The process-group ID.</param>
    /// <returns><see langword="true"/> when the group exists or access is denied; otherwise, <see langword="false"/>.</returns>
    private static bool IsProcessGroupRunning(int processGroupId)
    {
        if (NativeKill(-processGroupId, 0) == 0)
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == NoSuchProcess)
        {
            return false;
        }

        if (error == PermissionDenied)
        {
            return true;
        }

        throw new Win32Exception(error, $"Failed to inspect SSH daemon process group {processGroupId}.");
    }

    /// <summary>Waits for all processes in a process group to exit.</summary>
    /// <param name="processGroupId">The process-group ID.</param>
    /// <param name="timeout">The maximum wait duration.</param>
    /// <returns><see langword="true"/> when the group no longer exists before the timeout.</returns>
    private static bool WaitForProcessGroupExit(int processGroupId, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (!IsProcessGroupRunning(processGroupId))
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return !IsProcessGroupRunning(processGroupId);
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
            StopProcessGroup(_process);
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

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int NativeKill(int processId, int signal);
}
