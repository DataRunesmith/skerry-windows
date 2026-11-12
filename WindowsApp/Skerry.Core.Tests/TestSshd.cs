using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;

namespace Skerry.Core.Tests;

/// <summary>
/// Standalone, minimal sshd instance on a random high port with its own
/// configuration file (does not read the system sshd_config). Used by SSH/SFTP integration tests.
/// </summary>
public sealed class TestSshd : IDisposable
{
    private static readonly TimeSpan KeygenTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);

    private readonly Process _process;
    private readonly DirectoryInfo _directory;

    /// <summary>Gets the local TCP port used by the test SSH server.</summary>
    public int Port { get; }

    /// <summary>Gets the path to the private key used by the test SSH client.</summary>
    public string ClientKeyPath { get; }

    /// <summary>Creates a fixture that owns the running SSH server and its temporary files.</summary>
    /// <param name="process">The running SSH daemon process.</param>
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
        // This fixture deliberately uses the Linux OpenSSH daemon and is only
        // applicable to the Linux integration-test runner.
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/sbin/sshd"))
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

            int port;
            using (var probe = new TcpListener(System.Net.IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            }

            var configPath = Path.Combine(dir, "sshd_config");
            File.WriteAllText(configPath,
                $"Port {port}\nListenAddress 127.0.0.1\nHostKey {QuoteConfigPath(hostKey)}\nAuthorizedKeysFile {QuoteConfigPath(authorizedKeys)}\n" +
                "PubkeyAuthentication yes\nPasswordAuthentication no\nUsePAM no\nStrictModes no\n" +
                $"Subsystem sftp internal-sftp\nPidFile {QuoteConfigPath(Path.Combine(dir, "pid"))}\n");

            var psi = new ProcessStartInfo("/usr/sbin/sshd")
            {
                UseShellExecute = false,
                // Do not redirect daemon output into unread pipes: a full pipe
                // could block sshd and make the integration tests hang.
            };
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(configPath);
            psi.ArgumentList.Add("-D");
            psi.ArgumentList.Add("-e");

            server = Process.Start(psi);
            if (server is null)
            {
                return null;
            }

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < StartupTimeout)
            {
                if (server.HasExited)
                {
                    return null;
                }

                try
                {
                    using var probeClient = new TcpClient();
                    probeClient.Connect("127.0.0.1", port);

                    var fixture = new TestSshd(server, directory, port, clientKey);
                    server = null;    // Ownership transfers to the fixture.
                    directory = null; // The fixture now owns the directory.
                    return fixture;
                }
                catch (SocketException)
                {
                    Thread.Sleep(100);
                }
            }

            return null;
        }
        catch (Win32Exception)
        {
            // OpenSSH/ssh-keygen may be missing or unavailable on a local host.
            return null;
        }
        finally
        {
            try
            {
                if (server is not null)
                {
                    StopProcess(server);
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
                    if (directory is not null)
                    {
                        TryDeleteDirectory(directory);
                    }
                }
            }
        }
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

    /// <summary>Requests termination of a process tree and verifies that the owned process exits.</summary>
    /// <param name="process">The process to stop.</param>
    /// <exception cref="InvalidOperationException">The process remains alive or tree termination was incomplete.</exception>
    /// <exception cref="Win32Exception">The process cannot be terminated.</exception>
    private static void StopProcess(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        Exception? treeTerminationFailure = null;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (AggregateException exception)
        {
            // Some descendants may resist tree termination; still try to stop the owned process.
            treeTerminationFailure = exception;
        }
        catch (Win32Exception exception)
        {
            // The tree operation may fail even when the owned process can still be stopped directly.
            treeTerminationFailure = exception;
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // The process exited between the initial state check and Kill.
            return;
        }

        if (!process.WaitForExit(2000))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process exited between the state check and the fallback Kill.
            }
            catch (Win32Exception) when (process.HasExited)
            {
                // The process exited while the operating system was reporting the failed Kill.
            }

            if (!process.WaitForExit(2000))
            {
                throw new InvalidOperationException(
                    $"Process {process.Id} did not exit after termination was requested.",
                    treeTerminationFailure);
            }
        }

        if (treeTerminationFailure is not null)
        {
            throw new InvalidOperationException(
                $"Process {process.Id} exited, but termination of its entire process tree was not confirmed.",
                treeTerminationFailure);
        }
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
        try
        {
            StopProcess(_process);
        }
        finally
        {
            try
            {
                _process.Dispose();
            }
            finally
            {
                TryDeleteDirectory(_directory);
            }
        }
    }
}
