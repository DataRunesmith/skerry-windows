using System.Text.Json;

namespace Skerry.Core;

/// <summary>
/// Port av HostStore.swift — persistent host-databas, `~/.skerry/hosts.json`.
/// The same file can be read and written by App/, Android, LinuxApp and WindowsApp.
/// </summary>
public sealed class HostStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private SyncState _state;
    private readonly object _lock = new();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".skerry", "hosts.json");

    // Set when the file exists but cannot be read (oversized, a directory, a reparse point, no access).
    // The store then starts empty, so the app still opens, and refuses every change so the file is
    // never overwritten; an unparseable file still throws from the constructor, as before.
    private readonly string? _loadError;

    public HostStore(string path)
    {
        _path = path;
        try
        {
            _state = Load(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _state = new SyncState();
            _loadError = e.Message;
        }
    }

    private void ThrowIfLoadFailed()
    {
        if (_loadError is not null)
            throw new InvalidOperationException(
                $"The host database could not be read and was left untouched, so changes are refused: {_loadError}");
    }

    /// <summary>
    /// Tells "the file does not exist yet" (an empty state is correct) apart from
    /// "the file exists but cannot be parsed in either of the two known
    /// formats" (rethrow). Before this fix both collapsed into an empty
    /// <see cref="SyncState"/>, so the next <see cref="Upsert"/>/
    /// <see cref="Delete"/> permanently overwrote a merely TRUNCATED (not
    /// empty) file with empty content, a silent data loss without warning.
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
            // Older format: a plain Host[] array without the SyncState wrapper.
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
            ThrowIfLoadFailed();
            // Build and size-check the changed state first, so a failure leaves memory and disk unchanged.
            var candidate = CloneState(_state);
            host.ModifiedAt = ReferenceDate.Now();
            candidate.Tombstones.Remove(host.Id);
            var index = candidate.Hosts.FindIndex(h => h.Id == host.Id);
            if (index >= 0) candidate.Hosts[index] = host;
            else candidate.Hosts.Add(host);
            Commit(candidate);
        }
    }

    public void Delete(Guid id)
    {
        lock (_lock)
        {
            ThrowIfLoadFailed();
            var candidate = CloneState(_state);
            candidate.Hosts.RemoveAll(h => h.Id == id);
            candidate.Tombstones[id] = ReferenceDate.Now();
            Commit(candidate);
        }
    }

    public void Sync(ISyncProvider provider)
    {
        lock (_lock)
        {
            ThrowIfLoadFailed();
            var remote = provider.Pull() ?? new SyncState();
            Commit(SyncEngine.Merge(_state, remote));
            provider.Push(_state);
        }
    }

    /// <summary>Serializes and size-checks <paramref name="candidate"/>, writes it, and only then makes it the current state.</summary>
    private void Commit(SyncState candidate)
    {
        var bytes = Serialize(candidate);
        Persist(bytes);
        _state = candidate;
    }

    private static SyncState CloneState(SyncState state) =>
        JsonSerializer.Deserialize<SyncState>(JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions), JsonOptions)
        ?? new SyncState();

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
