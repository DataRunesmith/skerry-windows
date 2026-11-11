namespace Skerry.Core;

/// <summary>
/// Port av Sources/SSHCore/KnownHosts.swift (samma fil/format som
/// LinuxApp/src/known_hosts.rs): trust-on-first-use-lagring av
/// värdnycklar, `~/.skerry/known_hosts`, en rad per värd
/// (`host:port ssh-ed25519 AAAA...`).
/// </summary>
public enum KnownHostVerdict { Trusted, Learned, Changed, StorageFailure }

public sealed record KnownHostResult(KnownHostVerdict Verdict, string? StoredKey = null);

public sealed class KnownHosts
{
    private readonly string? _path;
    private readonly Dictionary<string, string> _entries;
    private readonly object _lock = new();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".skerry", "known_hosts");

    public KnownHosts(string? path)
    {
        _path = path;
        _entries = Load(path);
    }

    private const int MaxKnownHostsBytes = 4 * 1024 * 1024;
    private static FileAttributes? ExistingAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static Dictionary<string, string> Load(string? path)
    {
        var result = new Dictionary<string, string>();
        if (path is null) return result;
        var attributes = ExistingAttributes(path);
        if (attributes is null) return result;
        if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
            throw new IOException("known_hosts får inte vara en symlänk eller reparse point");
        if ((attributes.Value & FileAttributes.Directory) != 0)
            throw new IOException("known_hosts-sökvägen är en katalog");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxKnownHostsBytes)
            throw new IOException("known_hosts överskrider storleksgränsen på 4 MiB");
        using var memory = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = stream.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            if (memory.Length + count > MaxKnownHostsBytes)
                throw new IOException("known_hosts överskred storleksgränsen under läsning");
            memory.Write(buffer, 0, count);
        }
        var text = new System.Text.UTF8Encoding(false, true).GetString(memory.ToArray());
        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var spaceIndex = line.IndexOf(' ');
            if (spaceIndex <= 0) continue;
            result[line[..spaceIndex]] = line[(spaceIndex + 1)..];
        }
        return result;
    }

    public KnownHostResult Check(string host, int port, string keyString)
    {
        lock (_lock)
        {
            var id = $"{host}:{port}";
            if (_entries.TryGetValue(id, out var stored))
                return stored == keyString
                    ? new KnownHostResult(KnownHostVerdict.Trusted)
                    : new KnownHostResult(KnownHostVerdict.Changed, stored);

            try
            {
                Append(id, keyString);
                _entries[id] = keyString;
                return new KnownHostResult(KnownHostVerdict.Learned);
            }
            catch
            {
                return new KnownHostResult(KnownHostVerdict.StorageFailure);
            }
        }
    }

    private void Append(string id, string keyString)
    {
        if (_path is null) return;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            var directoryAttributes = ExistingAttributes(dir);
            if (directoryAttributes is null ||
                (directoryAttributes.Value & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory)
                throw new IOException("known_hosts-katalogen måste vara en riktig katalog");
        }
        var fileAttributes = ExistingAttributes(_path);
        if (fileAttributes is not null &&
            (fileAttributes.Value & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("known_hosts måste vara en vanlig fil, inte en reparse point");
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        var bytes = new System.Text.UTF8Encoding(false).GetBytes($"{id} {keyString}\n");
        if (stream.Length + bytes.Length > MaxKnownHostsBytes)
            throw new IOException("known_hosts överskrider storleksgränsen på 4 MiB");
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }
}
