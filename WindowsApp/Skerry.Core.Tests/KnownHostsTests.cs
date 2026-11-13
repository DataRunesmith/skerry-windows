using Skerry.Core;
using Xunit;

namespace Skerry.Core.Tests;

public sealed class KnownHostsTests
{
    [Fact]
    public void FailsClosedWhenKnownHostsParentCannotBeCreated()
    {
        var root = Directory.CreateTempSubdirectory("skerry-known-hosts-").FullName;
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
    public void FailsClosedOnOversizedKnownHostsFileWithoutThrowing()
    {
        var root = Directory.CreateTempSubdirectory("skerry-known-hosts-").FullName;
        try
        {
            var path = Path.Combine(root, "known_hosts");
            File.WriteAllBytes(path, new byte[4 * 1024 * 1024 + 1]);
            var store = new KnownHosts(path);
            Assert.Equal(KnownHostVerdict.StorageFailure, store.Check("host", 22, "ssh-ed25519 AAAA").Verdict);
            Assert.Equal(4 * 1024 * 1024 + 1, new FileInfo(path).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FailsClosedWhenKnownHostsPathIsADirectory()
    {
        var root = Directory.CreateTempSubdirectory("skerry-known-hosts-").FullName;
        try
        {
            var store = new KnownHosts(root);
            Assert.Equal(KnownHostVerdict.StorageFailure, store.Check("host", 22, "ssh-ed25519 AAAA").Verdict);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FailsClosedOnInvalidUtf8KnownHostsFile()
    {
        var root = Directory.CreateTempSubdirectory("skerry-known-hosts-").FullName;
        try
        {
            var path = Path.Combine(root, "known_hosts");
            File.WriteAllBytes(path, new byte[] { 0xFF, 0xFE, 0x20, 0x41 });
            var store = new KnownHosts(path);
            Assert.Equal(KnownHostVerdict.StorageFailure, store.Check("host", 22, "ssh-ed25519 AAAA").Verdict);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsChangedKeyInKnownHostsFileWithLeadingBom()
    {
        var root = Directory.CreateTempSubdirectory("skerry-known-hosts-").FullName;
        try
        {
            var path = Path.Combine(root, "known_hosts");
            var line = System.Text.Encoding.UTF8.GetBytes("host:22 ssh-ed25519 AAAA\n");
            File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(line).ToArray());
            var store = new KnownHosts(path);
            Assert.Equal(KnownHostVerdict.Trusted, store.Check("host", 22, "ssh-ed25519 AAAA").Verdict);
            Assert.Equal(KnownHostVerdict.Changed, store.Check("host", 22, "ssh-ed25519 BBBB").Verdict);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
