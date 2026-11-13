namespace Skerry.Core;

/// <summary>
/// Shared helper for atomic file persistence. Consolidated here to avoid the same non-atomic
/// <c>File.WriteAllText</c>/<c>File.WriteAllBytes</c> pattern in several
/// places (HostStore, SyncConfig, SyncCrypto, SyncProvider): a crash or
/// power loss in the middle of a direct write leaves a truncated file, which
/// the <c>Load</c> side could not tell apart from "the file does not exist yet",
/// so the next write would permanently overwrite the truncated file
/// with an empty state.
/// </summary>
public static class FsUtil
{
    /// <summary>Largest state file (hosts, encrypted sync envelope) the app reads or writes.</summary>
    public const int MaxStateFileBytes = 64 * 1024 * 1024;

    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    /// <summary>Returns <paramref name="data"/> without a leading UTF-8 byte-order mark, if it has one.</summary>
    public static ReadOnlySpan<byte> WithoutUtf8Bom(byte[] data) =>
        data.AsSpan().StartsWith(Utf8Bom) ? data.AsSpan(Utf8Bom.Length) : data.AsSpan();

    /// <summary>
    /// Writes <paramref name="data"/> to <paramref name="path"/> atomically:
    /// a temporary file in the SAME directory (which guarantees that <see cref="File.Move"/>
    /// below is an atomic filesystem operation, not a cross-filesystem
    /// copy) is written and flushed, then swapped in over the target.
    /// </summary>
    public static void AtomicWrite(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
        {
            dir = "."; // a bare file name lives in the working directory
        }
        var tmpPath = Path.Combine(dir, $".{Path.GetFileName(path)}.tmp.{Guid.NewGuid():N}");
        try
        {
            using (var stream = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(data, 0, data.Length);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmpPath, path, overwrite: true);
        }
        catch
        {
            File.Delete(tmpPath);
            throw;
        }
    }

    public static byte[] ReadAllBytesCapped(string path, int maxBytes)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            directory = "."; // a bare file name lives in the working directory
        }
        // Same decomposition as AtomicWrite: a directory plus a bare file name.
        path = Path.Combine(directory, Path.GetFileName(path));
        // These paths come from the config file and the sync-folder setting; a ".." path component
        // (not a name that merely contains two dots, like "ann..lee") is never legitimate.
        if (path == ".." ||
            path.StartsWith("../", StringComparison.Ordinal) || path.StartsWith("..\\", StringComparison.Ordinal) ||
            path.EndsWith("/..", StringComparison.Ordinal) || path.EndsWith("\\..", StringComparison.Ordinal) ||
            path.Contains("/../", StringComparison.Ordinal) || path.Contains("/..\\", StringComparison.Ordinal) ||
            path.Contains("\\../", StringComparison.Ordinal) || path.Contains("\\..\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("The path must not contain traversal segments.", nameof(path));
        }
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("The storage file must be a regular file, not a directory or reparse point.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maxBytes)
            throw new IOException($"The file exceeds the size limit of {maxBytes} bytes.");
        using var memory = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = stream.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            if (memory.Length + count > maxBytes)
                throw new IOException($"The file exceeded the size limit of {maxBytes} bytes while being read.");
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }

    public static void AtomicWriteText(string path, string text) =>
        AtomicWrite(path, System.Text.Encoding.UTF8.GetBytes(text));
}
