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

    public int Port { get; }
    public string ClientKeyPath { get; }

    private TestSshd(Process process, DirectoryInfo directory, int port, string clientKeyPath)
    {
        _process = process;
        _directory = directory;
        Port = port;
        ClientKeyPath = clientKeyPath;
    }

    /// <summary>
    /// Returns null when the Linux SSH fixture is unavailable locally.
    /// Fixture failures fail CI rather than silently skipping integration tests.
    /// </summary>
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
                    directory = null!; // The fixture now owns the directory.
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

    private static string QuoteConfigPath(string path) =>
        "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

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

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (AggregateException)
                {
                    // Tree termination can fail partially while the daemon is still alive.
                    // Retry the daemon itself and still wait for its actual exit below.
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }

                process.WaitForExit(2000);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and Kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Best-effort cleanup; the owning test must still be able to finish.
        }
    }

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
