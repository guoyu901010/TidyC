using TidyC.Core.Clean;
using TidyC.Core.Tests.TestSupport;

namespace TidyC.Core.Tests;

public sealed class HandleGuardTests
{
    [Fact]
    public void CaptureReturnsStableIdentity()
    {
        using var tree = new TempTree("guard-capture");
        string file = tree.File("data.bin", 8192);

        GuardResult result = HandleGuard.Capture(file, out FileIdentity identity);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(8192, identity.LogicalBytes);
        Assert.False(identity.IsDirectory);
        Assert.False(identity.IsReparsePoint);
        Assert.StartsWith("0x", identity.FileIdHex);
    }

    [Fact]
    public void DeleteVerified_RemovesFile()
    {
        using var tree = new TempTree("guard-delete");
        string file = tree.File("trash.tmp", 4096);
        Assert.True(HandleGuard.Capture(file, out FileIdentity identity).IsSuccess);

        GuardResult result = HandleGuard.DeleteVerified(file, identity, enforceProtectedPaths: false);

        Assert.True(result.IsSuccess, result.Message);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void DeleteVerified_RejectsSwappedFile()
    {
        using var tree = new TempTree("guard-swap");
        string file = tree.File("target.bin", 4096);
        Assert.True(HandleGuard.Capture(file, out FileIdentity identity).IsSuccess);

        string backup = tree.At("backup.bin");
        File.Move(file, backup);
        tree.File("target.bin", 12288);

        GuardResult result = HandleGuard.DeleteVerified(file, identity, enforceProtectedPaths: false);

        Assert.False(result.IsSuccess);
        Assert.True(result.Status is GuardStatus.IdentityMismatch or GuardStatus.SizeMismatch);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void DeleteVerified_RejectsJunctionToSystemDirectory()
    {
        using var tree = new TempTree("guard-junction");
        string victimDirectory = tree.Dir("victim");
        string victimFile = tree.File(@"victim\important.txt", 1024);
        string link = tree.At("attack_link");
        if (!Junctions.TryCreate(victimDirectory, link))
        {
            return;
        }

        GuardResult result = HandleGuard.DeleteVerified(link, enforceProtectedPaths: false);

        Assert.False(result.IsSuccess);
        Assert.Equal(GuardStatus.ReparsePointRejected, result.Status);
        Assert.True(File.Exists(victimFile), "联接点目标绝不能被删除");
        Assert.True(Directory.Exists(victimDirectory));
    }

    [Fact]
    public void DeleteVerified_RefusesProtectedSystemPath()
    {
        GuardResult result = HandleGuard.DeleteVerified(@"C:\Windows");

        Assert.False(result.IsSuccess);
        Assert.True(Directory.Exists(@"C:\Windows"));
    }

    [Fact]
    public void DeleteVerified_MissingFileReturnsNotFound()
    {
        using var tree = new TempTree("guard-missing");

        GuardResult result = HandleGuard.DeleteVerified(tree.At("does-not-exist.tmp"), enforceProtectedPaths: false);

        Assert.False(result.IsSuccess);
        Assert.Equal(GuardStatus.NotFound, result.Status);
    }

    [Fact]
    public void DeleteVerified_LockedFileFailsGracefully()
    {
        using var tree = new TempTree("guard-lock");
        string file = tree.File("locked.bin", 4096);

        using (var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            GuardResult result = HandleGuard.DeleteVerified(file, enforceProtectedPaths: false);

            Assert.False(result.IsSuccess);
            Assert.True(result.Status is GuardStatus.LockedByAnotherProcess or GuardStatus.AccessDenied);
        }

        Assert.True(File.Exists(file));
    }

    [Fact]
    public void DeleteVerified_HandlesExtendedLongPaths()
    {
        using var tree = new TempTree("guard-long");
        string directory = tree.Root;
        while (directory.Length < 280)
        {
            directory = Path.Combine(directory, new string('d', 40));
        }

        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "long.txt");
        File.WriteAllText(file, "long path");
        Assert.True(file.Length > 260);

        GuardResult result = HandleGuard.DeleteVerified(file, enforceProtectedPaths: false);

        Assert.True(result.IsSuccess, result.Message);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void MoveVerified_RenamesInPlace()
    {
        using var tree = new TempTree("guard-move");
        string source = tree.File("source.bin", 16384);
        string destination = tree.At(@"nested\dest.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Assert.True(HandleGuard.Capture(source, out FileIdentity identity).IsSuccess);

        GuardResult result = HandleGuard.MoveVerified(source, destination, identity, enforceProtectedPaths: false);

        Assert.True(result.IsSuccess, result.Message);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(destination));
        Assert.Equal(16384, new FileInfo(destination).Length);
    }

    [Fact]
    public void MoveVerified_RejectsJunction()
    {
        using var tree = new TempTree("guard-move-junction");
        string victim = tree.Dir("victim2");
        tree.File(@"victim2\keep.txt", 512);
        string link = tree.At("junction_source");
        if (!Junctions.TryCreate(victim, link))
        {
            return;
        }

        GuardResult result = HandleGuard.MoveVerified(link, tree.At(@"dest\link"), enforceProtectedPaths: false);

        Assert.False(result.IsSuccess);
        Assert.Equal(GuardStatus.ReparsePointRejected, result.Status);
        Assert.True(File.Exists(tree.At(@"victim2\keep.txt")));
    }

    [Fact]
    public void DeleteVerified_RemovesReadOnlyFile()
    {
        using var tree = new TempTree("guard-readonly");
        string file = tree.File("readonly.txt", 2048);
        File.SetAttributes(file, FileAttributes.ReadOnly);

        GuardResult result = HandleGuard.DeleteVerified(file, enforceProtectedPaths: false);

        Assert.True(result.IsSuccess, result.Message);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void DeleteTreeVerified_RemovesTreeButKeepsJunctionTargets()
    {
        using var tree = new TempTree("guard-tree");
        tree.File(@"doomed\a\one.bin", 4096);
        tree.File(@"doomed\b\two.bin", 8192);
        string outside = tree.Dir("outside");
        string outsideFile = tree.File(@"outside\survivor.bin", 1024);
        Junctions.TryCreate(outside, tree.At(@"doomed\junction"));

        GuardResult result = HandleGuard.DeleteTreeVerified(tree.At("doomed"), enforceProtectedPaths: false);

        Assert.True(result.IsSuccess, result.Message);
        Assert.False(Directory.Exists(tree.At("doomed")));
        Assert.True(File.Exists(outsideFile), "目录树内的联接点不应把目标一起删除");
    }
}
