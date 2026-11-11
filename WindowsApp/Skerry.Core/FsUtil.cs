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
            throw new ArgumentException("sökvägen saknar en förälderkatalog", nameof(path));
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
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("lagringsfilen måste vara en vanlig fil, inte en reparse point");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maxBytes)
            throw new IOException($"filen överskrider storleksgränsen på {maxBytes} byte");
        using var memory = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = stream.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            if (memory.Length + count > maxBytes)
                throw new IOException($"filen överskred storleksgränsen på {maxBytes} byte under läsning");
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }

    public static void AtomicWriteText(string path, string text) =>
        AtomicWrite(path, System.Text.Encoding.UTF8.GetBytes(text));
}
