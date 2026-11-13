using System.Text;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace Skerry.Core;

/// <summary>
/// Port of the core features of App/SFTPBrowserModel.swift (same browsing model
/// as LinuxApp/src/sftp.rs), here on top of SSH.NET's built-in <see cref="SftpClient"/>
/// instead of a custom SFTP protocol implementation (Rust/Swift lack a
/// ready-made SSH client with SFTP built in; SSH.NET already has one). One connection
/// is kept open and reused for the whole browsing session, matching the Swift side's
/// <c>ensureClient()</c> cache / Rust's one-background-thread-per-tab.
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
    /// Lists the first page of a directory, folders first, then alphabetically within the page (same sort as
    /// App/LinuxApp). A page holds at most <see cref="MaxDirectoryEntries"/> entries and enumeration stops
    /// there, so a huge directory cannot exhaust memory; use <see cref="List(string, int, out bool)"/> for the rest.
    /// </summary>
    public IReadOnlyList<SftpEntry> List(string path) => List(path, 0, out _);

    /// <summary>
    /// Lists one page of a directory: the entries after the first <paramref name="skip"/> entries in server
    /// order, at most <see cref="MaxDirectoryEntries"/> of them. <paramref name="hasMore"/> is true when more
    /// entries follow the returned page.
    /// </summary>
    public IReadOnlyList<SftpEntry> List(string path, int skip, out bool hasMore)
    {
        var (entries, more) = ListAsync(path, skip).GetAwaiter().GetResult();
        hasMore = more;
        return entries;
    }

    /// <summary>Number of entries on one directory page.</summary>
    public static int DirectoryPageSize => MaxDirectoryEntries;

    private async Task<(IReadOnlyList<SftpEntry> Entries, bool HasMore)> ListAsync(string path, int skip)
    {
        if (skip < 0) throw new ArgumentOutOfRangeException(nameof(skip));
        var entries = new List<SftpEntry>();
        var seen = 0;
        var hasMore = false;
        await foreach (var f in _client.ListDirectoryAsync(path, CancellationToken.None))
        {
            if (f.Name == "." || f.Name == "..") continue;
            if (seen++ < skip) continue;
            if (entries.Count >= MaxDirectoryEntries)
            {
                hasMore = true;
                break;
            }
            entries.Add(new SftpEntry(f.Name, f.IsDirectory, f.Length));
        }
        IReadOnlyList<SftpEntry> sorted = entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (sorted, hasMore);
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

    /// <summary>Tries to decode as STRICT UTF-8 (throws on invalid byte sequences), the same safety margin as the Swift side's <c>String(bytes:encoding:.utf8)</c>: binary content must never be silently interpreted as text and risk being saved over.</summary>
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

    /// <summary>mode: octal integer, e.g. 0b111101101 for "755", the same notation as chmod.</summary>
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
