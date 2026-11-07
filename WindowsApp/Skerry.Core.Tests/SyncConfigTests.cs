using Skerry.Core;

namespace Skerry.Core.Tests;

public class SyncConfigTests
{
    [Fact]
    public void RoundTripsThroughDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"skerry-cs-syncconfig-{Guid.NewGuid()}");
        var path = Path.Combine(dir, "sync-config.json");
        var config = new SyncConfig { FolderPath = @"D:\Syncthing\skerry" };
        config.Save(path);

        var reloaded = SyncConfig.Load(path);
        Assert.Equal(@"D:\Syncthing\skerry", reloaded.FolderPath);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void MissingFileLoadsAsEmptyConfig()
    {
        var config = SyncConfig.Load(Path.Combine(Path.GetTempPath(), $"skerry-nonexistent-{Guid.NewGuid()}.json"));
        Assert.Null(config.FolderPath);
    }
}
