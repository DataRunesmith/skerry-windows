namespace Skerry.Core;

/// <summary>
/// Delad hjälpare för atomär filpersistens. Konsoliderat hit för att undvika samma icke-atomära
/// <c>File.WriteAllText</c>/<c>File.WriteAllBytes</c>-mönster på flera
/// ställen (HostStore, SyncConfig, SyncCrypto, SyncProvider): ett krasch/
/// strömavbrott mitt i en direkt skrivning lämnar en trunkerad fil, vilket
/// <c>Load</c>-sidan annars inte kunde skilja från "filen finns inte än" —
/// nästa skrivning skulle då permanent skriva över den trunkerade filen
/// med ett tomt tillstånd.
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
    /// Skriver <paramref name="data"/> till <paramref name="path"/> atomärt:
    /// en temporär fil i SAMMA katalog (garanterar att <see cref="File.Move"/>
    /// nedan är en atomär filsystemsoperation, inte en cross-filesystem-
    /// kopiering) skrivs och flushas, byts sedan in över målet.
    /// </summary>
    public static void AtomicWrite(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
        {
            throw new ArgumentException("The path has no parent directory.", nameof(path));
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
            throw new ArgumentException("The path has no parent directory.", nameof(path));
        }
        // Same decomposition as AtomicWrite: a directory plus a bare file name.
        path = Path.Combine(directory, Path.GetFileName(path));
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
