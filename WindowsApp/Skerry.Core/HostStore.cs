using System.Text.Json;

namespace Skerry.Core;

/// <summary>
/// Port av HostStore.swift — persistent host-databas, `~/.skerry/hosts.json`.
/// Samma fil kan läsas/skrivas av App/, Android, LinuxApp och WindowsApp.
/// </summary>
public sealed class HostStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private SyncState _state;
    private readonly object _lock = new();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".skerry", "hosts.json");

    public HostStore(string path)
    {
        _path = path;
        _state = Load(path);
    }

    /// <summary>
    /// Skiljer "filen finns inte än" (tomt tillstånd är korrekt) från
    /// "filen finns men går inte att tolka i något av de två kända
    /// formaten" (kasta vidare) — innan denna fix kollapsade båda till ett
    /// tomt <see cref="SyncState"/>, så nästa <see cref="Upsert"/>/
    /// <see cref="Delete"/> skrev permanent över en bara TRUNKERAD (inte
    /// tom) fil med tomt innehåll, en tyst dataförlust utan varning.
    /// </summary>
    private static SyncState Load(string path)
    {
        byte[] data;
        try
        {
            data = FsUtil.ReadAllBytesCapped(path, FsUtil.MaxStateFileBytes);
        }
        catch (FileNotFoundException)
        {
            return new SyncState();
        }
        catch (DirectoryNotFoundException)
        {
            return new SyncState();
        }
        var json = FsUtil.WithoutUtf8Bom(data);
        try
        {
            return JsonSerializer.Deserialize<SyncState>(json, JsonOptions) ?? new SyncState();
        }
        catch (JsonException)
        {
            // Äldre format: en ren Host[]-array utan SyncState-omslag.
            return new SyncState { Hosts = JsonSerializer.Deserialize<List<Host>>(json, JsonOptions) ?? new() };
        }
    }

    public List<Host> All()
    {
        lock (_lock)
            return _state.Hosts.OrderBy(h => h.Alias, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Upsert(Host host)
    {
        lock (_lock)
        {
            host.ModifiedAt = ReferenceDate.Now();
            _state.Tombstones.Remove(host.Id);
            var index = _state.Hosts.FindIndex(h => h.Id == host.Id);
            if (index >= 0) _state.Hosts[index] = host;
            else _state.Hosts.Add(host);
            Persist();
        }
    }

    public void Delete(Guid id)
    {
        lock (_lock)
        {
            _state.Hosts.RemoveAll(h => h.Id == id);
            _state.Tombstones[id] = ReferenceDate.Now();
            Persist();
        }
    }

    public void Sync(ISyncProvider provider)
    {
        lock (_lock)
        {
            var remote = provider.Pull() ?? new SyncState();
            var merged = SyncEngine.Merge(_state, remote);
            // Check the size before the merged state replaces the in-memory one or reaches disk.
            var bytes = Serialize(merged);
            _state = merged;
            Persist(bytes);
            provider.Push(_state);
        }
    }

    private void Persist() => Persist(Serialize(_state));

    /// <summary>Serializes the state and rejects anything <see cref="Load"/> would refuse to read back.</summary>
    private static byte[] Serialize(SyncState state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        if (bytes.Length > FsUtil.MaxStateFileBytes)
            throw new InvalidOperationException(
                $"The host database would exceed the size limit of {FsUtil.MaxStateFileBytes} bytes.");
        return bytes;
    }

    private void Persist(byte[] bytes)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        FsUtil.AtomicWrite(_path, bytes);
    }
}
