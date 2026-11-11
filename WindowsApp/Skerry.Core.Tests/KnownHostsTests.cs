using Skerry.Core;
using Xunit;

namespace Skerry.Core.Tests;

public sealed class KnownHostsTests
{
    [Fact]
    public void FailsClosedWhenKnownHostsParentCannotBeCreated()
    {
        var root = Path.Combine(Path.GetTempPath(), "skerry-known-hosts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var parentIsFile = Path.Combine(root, "not-a-directory");
            File.WriteAllText(parentIsFile, "block");
            var store = new KnownHosts(Path.Combine(parentIsFile, "known_hosts"));

            Assert.Equal(KnownHostVerdict.StorageFailure, store.Check("host", 22, "ssh-ed25519 AAAA").Verdict);
            Assert.Equal(KnownHostVerdict.StorageFailure, store.Check("host", 22, "ssh-ed25519 AAAA").Verdict);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsOversizedKnownHostsFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "skerry-known-hosts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "known_hosts");
            File.WriteAllBytes(path, new byte[4 * 1024 * 1024 + 1]);
            Assert.Throws<IOException>(() => new KnownHosts(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
