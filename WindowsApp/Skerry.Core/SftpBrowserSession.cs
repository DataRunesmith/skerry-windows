using System.Text;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace Skerry.Core;

/// <summary>
/// Port av App/SFTPBrowserModel.swifts kärnfunktioner (samma bläddringsmodell
/// som LinuxApp/src/sftp.rs) — här ovanpå SSH.NETs inbyggda <see cref="SftpClient"/>
/// istället för en egen SFTP-protokollimplementation (Rust/Swift saknar en
/// färdig SSH-klient med SFTP inbyggt; SSH.NET har det redan). En anslutning
/// hålls öppen och återanvänds för hela bläddringen — motsvarar Swiftsidans
/// <c>ensureClient()</c>-cache / Rusts en-bakgrundstråd-per-flik.
/// </summary>
public sealed record SftpEntry(string Name, bool IsDirectory, long Size);

public sealed class SftpBrowserSession : IDisposable
{
    private const int MaxReadWriteBytes = 64 * 1024 * 1024;
    private const int MaxDirectoryEntries = 10_000;
    private readonly SftpClient _client;

    private SftpBrowserSession(SftpClient client) => _client = client;

    public static SftpBrowserSession Connect(Host host, string? password, KnownHosts knownHosts)
    {
        using var agent = SshSession.ConnectAgentIfNeeded(host);
        var auth = SshSession.BuildAuthenticationMethod(host, password, agent);
        var connectionInfo = new ConnectionInfo(host.HostName, (int)host.Port, host.User, auth);
        var client = new SftpClient(connectionInfo);
        client.HostKeyReceived += SshSession.MakeHostKeyHandler(host, knownHosts, throwOnChange: true);
        try
        {
            client.Connect();
            return new SftpBrowserSession(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Lists a directory, folders first, then alphabetically within each group (same sort as App/LinuxApp).
    /// Enumeration stops after <see cref="MaxDirectoryEntries"/> entries so a huge directory cannot
    /// exhaust memory; the first entries are still returned, so the directory stays navigable.
    /// </summary>
    public IReadOnlyList<SftpEntry> List(string path) => List(path, out _);

    /// <summary>Same as <see cref="List(string)"/>; <paramref name="truncated"/> is true when entries beyond the limit were left out.</summary>
    public IReadOnlyList<SftpEntry> List(string path, out bool truncated)
    {
        var (entries, limitReached) = ListAsync(path).GetAwaiter().GetResult();
        truncated = limitReached;
        return entries;
    }

    private async Task<(IReadOnlyList<SftpEntry> Entries, bool Truncated)> ListAsync(string path)
    {
        var entries = new List<SftpEntry>();
        var limitReached = false;
        await foreach (var f in _client.ListDirectoryAsync(path, CancellationToken.None))
        {
            if (f.Name == "." || f.Name == "..") continue;
            if (entries.Count >= MaxDirectoryEntries)
            {
                limitReached = true;
                break;
            }
            entries.Add(new SftpEntry(f.Name, f.IsDirectory, f.Length));
        }
        IReadOnlyList<SftpEntry> sorted = entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (sorted, limitReached);
    }

    public byte[] ReadFile(string path)
    {
        using var remote = _client.OpenRead(path);
        // The server may not report a size (FSTAT failed or omitted it); then the stream is not
        // seekable and Length throws, so rely on the streaming limit below.
        long? knownLength = remote.CanSeek ? remote.Length : null;
        if (knownLength > MaxReadWriteBytes)
            throw new IOException($"The file exceeds the read limit of {MaxReadWriteBytes} bytes.");
        using var stream = knownLength is { } length ? new MemoryStream((int)length) : new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = remote.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            if (stream.Length + count > MaxReadWriteBytes)
                throw new IOException($"The file exceeded the read limit of {MaxReadWriteBytes} bytes while being read.");
            stream.Write(buffer, 0, count);
        }
        return stream.ToArray();
    }

    /// <summary>Creates or replaces a file, with an upper size limit to avoid accidental memory and network load.</summary>
    public void WriteFile(string path, byte[] data)
    {
        if (data.Length > MaxReadWriteBytes)
            throw new IOException($"The file exceeds the write limit of {MaxReadWriteBytes} bytes.");
        using var stream = new MemoryStream(data, writable: false);
        _client.UploadFile(stream, path, canOverride: true);
    }

    /// <summary>Försöker avkoda som UTF-8 STRIKT (kastar på ogiltiga byte-sekvenser) — samma säkerhetsmarginal som Swiftsidans <c>String(bytes:encoding:.utf8)</c>: binärt innehåll ska aldrig tyst tolkas som text och riskera att sparas över.</summary>
    public static bool TryDecodeUtf8(byte[] bytes, out string text)
    {
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = "";
            return false;
        }
    }

    public void CreateDirectory(string path) => _client.CreateDirectory(path);

    public void RemoveFile(string path) => _client.DeleteFile(path);

    public void RemoveDirectory(string path) => _client.DeleteDirectory(path);

    public void Rename(string from, string to) => _client.RenameFile(from, to);

    /// <summary>mode: oktalt heltal, t.ex. 0b111101101 för "755" — samma notation som chmod.</summary>
    public void SetPermissions(string path, short mode) => _client.ChangePermissions(path, mode);

    public void SetOwner(string path, int uid, int gid)
    {
        var attrs = _client.GetAttributes(path);
        attrs.UserId = uid;
        attrs.GroupId = gid;
        _client.SetAttributes(path, attrs);
    }

    public void Dispose() => _client.Dispose();
}
