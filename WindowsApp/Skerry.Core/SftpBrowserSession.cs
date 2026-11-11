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

    /// <summary>Katalogen listas alltid mapp-först, sedan alfabetiskt inom varje grupp (samma sortering som App/LinuxApp).</summary>
    public IReadOnlyList<SftpEntry> List(string path)
    {
        var entries = _client.ListDirectory(path)
            .Where(f => f.Name != "." && f.Name != "..")
            .Take(MaxDirectoryEntries + 1)
            .Select(f => new SftpEntry(f.Name, f.IsDirectory, f.Length))
            .ToList();
        if (entries.Count > MaxDirectoryEntries)
            throw new IOException($"Katalogen innehåller fler än {MaxDirectoryEntries} poster.");
        return entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public byte[] ReadFile(string path)
    {
        using var remote = _client.OpenRead(path);
        if (remote.Length > MaxReadWriteBytes)
            throw new IOException($"Filen överskrider läsgränsen på {MaxReadWriteBytes} byte.");
        using var stream = new MemoryStream((int)remote.Length);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = remote.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            if (stream.Length + count > MaxReadWriteBytes)
                throw new IOException($"Filen överskred läsgränsen på {MaxReadWriteBytes} byte under läsning.");
            stream.Write(buffer, 0, count);
        }
        return stream.ToArray();
    }

    /// <summary>Skapar eller ersätter en fil, med en övre gräns för att undvika oavsiktliga minnes- och nätverksbelastningar.</summary>
    public void WriteFile(string path, byte[] data)
    {
        if (data.Length > MaxReadWriteBytes)
            throw new IOException($"Filen överskrider skrivgränsen på {MaxReadWriteBytes} byte.");
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
