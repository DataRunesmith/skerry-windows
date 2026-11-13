namespace Skerry.Core;

/// <summary>
/// Port of Sources/SSHCore/KnownHosts.swift (same file/format as
/// LinuxApp/src/known_hosts.rs): trust-on-first-use storage of
/// host keys, `~/.skerry/known_hosts`, one line per host
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

    // Set when the file exists but cannot be trusted or read (oversized, a directory, a reparse
    // point, invalid UTF-8). The store then fails closed: every Check returns StorageFailure and
    // nothing is appended, so the app still starts and the file is left untouched for repair.
    private readonly bool _loadFailed;

    public KnownHosts(string? path)
    {
        _path = path;
        try
        {
            _entries = Load(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            _entries = new Dictionary<string, string>();
            _loadFailed = true;
        }
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

        byte[] data;
        try
        {
            data = FsUtil.ReadAllBytesCapped(path, MaxKnownHostsBytes);
        }
        catch (FileNotFoundException)
        {
            return result;
        }
        catch (DirectoryNotFoundException)
        {
            return result;
        }
        var text = FsUtil.DecodeText(data, strict: true);
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
            if (_loadFailed) return new KnownHostResult(KnownHostVerdict.StorageFailure);
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
                throw new IOException("The known_hosts directory must be a real directory.");
        }
        var fileAttributes = ExistingAttributes(_path);
        if (fileAttributes is not null &&
            (fileAttributes.Value & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("known_hosts must be a regular file, not a directory or reparse point.");
        using var stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
        var originalLength = stream.Length;
        // A file that does not end in a newline (for example after an interrupted write) must not
        // swallow the new entry onto its last line.
        var needsLeadingNewline = false;
        if (originalLength > 0)
        {
            stream.Position = originalLength - 1;
            needsLeadingNewline = stream.ReadByte() != '\n';
        }
        var bytes = new System.Text.UTF8Encoding(false).GetBytes($"{(needsLeadingNewline ? "\n" : "")}{id} {keyString}\n");
        if (originalLength + bytes.Length > MaxKnownHostsBytes)
            throw new IOException("known_hosts exceeds the size limit of 4 MiB.");
        try
        {
            stream.Position = originalLength;
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            // Do not leave a partial entry behind.
            try { stream.SetLength(originalLength); } catch { /* best effort */ }
            throw;
        }
    }
}
