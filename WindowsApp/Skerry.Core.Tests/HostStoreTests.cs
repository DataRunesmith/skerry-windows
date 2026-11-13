using Skerry.Core;

namespace Skerry.Core.Tests;

public class HostStoreTests
{
    private static string TempDir() => Directory.CreateTempSubdirectory("skerry-cs-test-").FullName;

    [Fact]
    public void LoadsHostsFileWithLeadingUtf8Bom()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "hosts.json");
        var host = Host.Create("bom-host", "192.168.1.51", "user");
        new HostStore(path).Upsert(host);
        File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(File.ReadAllBytes(path)).ToArray());

        var reopened = new HostStore(path);
        Assert.Equal(host.Id, Assert.Single(reopened.All()).Id);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void OversizedUpdateIsRejectedAndLeavesPreviousStateReadable()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "hosts.json");
        var store = new HostStore(path);
        var original = Host.Create("small", "192.168.1.52", "user");
        store.Upsert(original);

        var oversized = Host.Create(new string('a', FsUtil.MaxStateFileBytes + 1), "192.168.1.53", "user");
        Assert.Throws<InvalidOperationException>(() => store.Upsert(oversized));

        // Neither memory nor disk kept the rejected change, and later saves still work.
        Assert.Equal(original.Id, Assert.Single(store.All()).Id);
        Assert.Equal(original.Id, Assert.Single(new HostStore(path).All()).Id);
        store.Upsert(Host.Create("another", "192.168.1.54", "user"));
        Assert.Equal(2, new HostStore(path).All().Count);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void AcceptsDirectoryNamesThatContainTwoDots()
    {
        var dir = Path.Combine(TempDir(), "ann..lee");
        var path = Path.Combine(dir, "hosts.json");
        var host = Host.Create("dots", "192.168.1.55", "user");
        new HostStore(path).Upsert(host);

        Assert.Equal(host.Id, Assert.Single(new HostStore(path).All()).Id);
        Directory.Delete(Path.GetDirectoryName(dir)!, true);
    }

    [Fact]
    public void RejectsPathsWithATraversalComponent()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "sub", "..", "hosts.json");
        Assert.Throws<ArgumentException>(() => new HostStore(path));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void OversizedHostsFileDoesNotStopStartupAndIsNeverOverwritten()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "hosts.json");
        File.WriteAllBytes(path, new byte[FsUtil.MaxStateFileBytes + 1]);

        var store = new HostStore(path);

        Assert.Empty(store.All());
        Assert.Throws<InvalidOperationException>(() => store.Upsert(Host.Create("x", "192.168.1.56", "user")));
        Assert.Equal(FsUtil.MaxStateFileBytes + 1, new FileInfo(path).Length);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void LoadsUtf16EncodedHostsFile()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "hosts.json");
        var host = Host.Create("utf16-host", "192.168.1.57", "user");
        new HostStore(path).Upsert(host);
        var json = File.ReadAllText(path);
        File.WriteAllBytes(path, new System.Text.UnicodeEncoding(false, true).GetPreamble()
            .Concat(System.Text.Encoding.Unicode.GetBytes(json)).ToArray());

        Assert.Equal(host.Id, Assert.Single(new HostStore(path).All()).Id);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void LoadsLegacyHostArrayWithLeadingUtf8Bom()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "hosts.json");
        var host = Host.Create("legacy-bom", "192.168.1.58", "user");
        var legacy = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new List<Host> { host }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(legacy).ToArray());

        Assert.Equal(host.Id, Assert.Single(new HostStore(path).All()).Id);
        Directory.Delete(dir, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadsUtf32EncodedHostsFile(bool bigEndian)
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "hosts.json");
        var host = Host.Create("utf32-host", "192.168.1.59", "user");
        new HostStore(path).Upsert(host);
        var json = File.ReadAllText(path);
        var encoding = new System.Text.UTF32Encoding(bigEndian, byteOrderMark: true);
        File.WriteAllBytes(path, encoding.GetPreamble().Concat(encoding.GetBytes(json)).ToArray());

        Assert.Equal(host.Id, Assert.Single(new HostStore(path).All()).Id);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void HostStoreRoundTripsThroughDisk()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "hosts.json");
        var store = new HostStore(path);
        var host = Host.Create("host1", "192.168.1.50", "user");
        var id = host.Id;
        store.Upsert(host);

        var reopened = new HostStore(path);
        var all = reopened.All();
        Assert.Single(all);
        Assert.Equal(id, all[0].Id);
        Directory.Delete(dir, true);
    }

    /// <summary>
    /// Läser en riktig hosts.json genererad av en verklig `swift`-körning
    /// (fixturen kopieras in i testutdatakatalogen, se .csproj) — verifierar
    /// att C#-sidan verkligen kan avkoda Swifts faktiska wire-format, inte
    /// bara ett antaget sådant.
    /// </summary>
    [Fact]
    public void ReadsAHostsJsonActuallyProducedBySwift()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "csharp-hosts-fixture.json");
        Assert.True(File.Exists(fixturePath), $"Fixture saknas: {fixturePath}");

        var store = new HostStore(fixturePath);
        var hosts = store.All();
        Assert.Single(hosts);
        Assert.Equal("csharp-check", hosts[0].Alias);
        Assert.Equal("10.0.0.9", hosts[0].HostName);
        Assert.Equal(new HostAuth.KeyFile("/home/x/.ssh/id_ed25519"), hosts[0].Auth);
    }

    [Fact]
    public void ACorruptHostsJsonThrowsInsteadOfSilentlyBecomingEmpty()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "hosts.json");
        File.WriteAllText(path, "{ det här är inte giltig json");

        Assert.ThrowsAny<System.Text.Json.JsonException>(() => new HostStore(path));
        Directory.Delete(dir, true);
    }

    /// <summary>
    /// Ett äldre format (en ren <c>Host[]</c>-array utan SyncState-omslag)
    /// ska fortfarande gå att läsa — regressionstest för
    /// <see cref="SyncStateConverter"/>s omskrivning (den skulle annars
    /// kunna kasta ett otypat undantag på just den här formen och missa
    /// fallback-vägen helt).
    /// </summary>
    [Fact]
    public void ALegacyBareHostArrayStillLoadsCorrectly()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "hosts.json");
        File.WriteAllText(path, """[{"id":"00000000-0000-0000-0000-000000000001","alias":"legacy","hostName":"h","user":"u","modifiedAt":0}]""");

        var store = new HostStore(path);
        var hosts = store.All();
        Assert.Single(hosts);
        Assert.Equal("legacy", hosts[0].Alias);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void DeleteAddsATombstoneThatSurvivesReopen()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "hosts.json");
        var store = new HostStore(path);
        var host = Host.Create("t", "h", "u");
        store.Upsert(host);
        store.Delete(host.Id);

        Assert.Empty(store.All());
        var reopened = new HostStore(path);
        Assert.Empty(reopened.All());
        Directory.Delete(dir, true);
    }
}

public class SyncEngineTests
{
    private static Host HostAt(string alias, Guid id, double modifiedAt) => new()
    {
        Id = id,
        Alias = alias,
        HostName = "h",
        User = "u",
        ModifiedAt = new ReferenceDate(modifiedAt),
    };

    [Fact]
    public void NewerEditWinsOverOlderEdit()
    {
        var id = Guid.NewGuid();
        var a = new SyncState { Hosts = { HostAt("gammal", id, 10) } };
        var b = new SyncState { Hosts = { HostAt("ny", id, 20) } };
        var merged = SyncEngine.Merge(a, b);
        Assert.Single(merged.Hosts);
        Assert.Equal("ny", merged.Hosts[0].Alias);
    }

    [Fact]
    public void TombstoneWinsWhenNewerThanEdit()
    {
        var id = Guid.NewGuid();
        var a = new SyncState { Hosts = { HostAt("host", id, 10) } };
        var b = new SyncState { Tombstones = { [id] = new ReferenceDate(20) } };
        var merged = SyncEngine.Merge(a, b);
        Assert.Empty(merged.Hosts);
        Assert.Single(merged.Tombstones);
    }

    [Fact]
    public void NewerEditRevivesOverOlderTombstone()
    {
        var id = Guid.NewGuid();
        var a = new SyncState { Tombstones = { [id] = new ReferenceDate(10) } };
        var b = new SyncState { Hosts = { HostAt("återupplivad", id, 20) } };
        var merged = SyncEngine.Merge(a, b);
        Assert.Single(merged.Hosts);
        Assert.Equal("återupplivad", merged.Hosts[0].Alias);
        Assert.Empty(merged.Tombstones);
    }

    /// <summary>
    /// Regressionstest för en ORDNINGSBEROENDE tie-bugg:
    /// på en EXAKT tidsstämpel-krock vann tidigare alltid den sist besökta
    /// kopian i <c>Concat</c> — <c>Merge(a, b)</c> gav ett annat resultat
    /// än <c>Merge(b, a)</c>.
    /// </summary>
    [Fact]
    public void MergeIsCommutativeEvenOnAnExactModifiedAtTie()
    {
        var id = Guid.NewGuid();
        var a = new SyncState { Hosts = { HostAt("alpha", id, 42) } };
        var b = new SyncState { Hosts = { HostAt("bravo", id, 42) } };
        var ab = SyncEngine.Merge(a, b);
        var ba = SyncEngine.Merge(b, a);
        Assert.Single(ab.Hosts);
        Assert.Equal(ab.Hosts[0].Alias, ba.Hosts[0].Alias);
    }

    /// <summary>
    /// Riktig cross-instans-verifiering: två oberoende HostStores synkar via
    /// en delad FolderSyncProvider-fil och KONVERGERAR — samma test som
    /// LinuxApp/src/sync.rs, nu i C# för att bevisa att protokollet
    /// verkligen är klientoberoende (tredje språket).
    /// </summary>
    [Fact]
    public void TwoIndependentStoresConvergeThroughASharedFolderProvider()
    {
        var dir = Directory.CreateTempSubdirectory("skerry-cs-sync-test-").FullName;
        var storeAPath = Path.Combine(dir, "a", "hosts.json");
        var storeBPath = Path.Combine(dir, "b", "hosts.json");
        var sharedPath = Path.Combine(dir, "shared", "hosts.json");

        var storeA = new HostStore(storeAPath);
        var storeB = new HostStore(storeBPath);

        storeA.Upsert(Host.Create("från-a", "1.2.3.4", "u"));
        var provider = new FolderSyncProvider(sharedPath);
        storeA.Sync(provider);

        storeB.Upsert(Host.Create("från-b", "5.6.7.8", "u"));
        storeB.Sync(provider);

        storeA.Sync(provider);

        var aliasesA = storeA.All().Select(h => h.Alias).ToList();
        Assert.Contains("från-a", aliasesA);
        Assert.Contains("från-b", aliasesA);

        Directory.Delete(dir, true);
    }
}
