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
            File.WriteAllBytes(path, new byte[] { 0x41, 0x20, 0xC3, 0x28 });
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

    [Fact]
    public void AppendStartsOnANewLineWhenTheFileDoesNotEndWithOne()
    {
        var root = Directory.CreateTempSubdirectory("skerry-known-hosts-").FullName;
        try
        {
            var path = Path.Combine(root, "known_hosts");
            File.WriteAllText(path, "old:22 ssh-ed25519 OLDKEY");
            var store = new KnownHosts(path);

            Assert.Equal(KnownHostVerdict.Learned, store.Check("new", 22, "ssh-ed25519 NEWKEY").Verdict);

            var reloaded = new KnownHosts(path);
            Assert.Equal(KnownHostVerdict.Trusted, reloaded.Check("old", 22, "ssh-ed25519 OLDKEY").Verdict);
            Assert.Equal(KnownHostVerdict.Trusted, reloaded.Check("new", 22, "ssh-ed25519 NEWKEY").Verdict);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RefusesToAppendToAUtf16KnownHostsFile()
    {
        var root = Directory.CreateTempSubdirectory("skerry-known-hosts-").FullName;
        try
        {
            var path = Path.Combine(root, "known_hosts");
            var content = new System.Text.UnicodeEncoding(false, true).GetPreamble()
                .Concat(System.Text.Encoding.Unicode.GetBytes("old:22 ssh-ed25519 OLDKEY\n")).ToArray();
            File.WriteAllBytes(path, content);
            var store = new KnownHosts(path);

            Assert.Equal(KnownHostVerdict.Trusted, store.Check("old", 22, "ssh-ed25519 OLDKEY").Verdict);
            Assert.Equal(KnownHostVerdict.StorageFailure, store.Check("new", 22, "ssh-ed25519 NEWKEY").Verdict);
            Assert.Equal(content, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
