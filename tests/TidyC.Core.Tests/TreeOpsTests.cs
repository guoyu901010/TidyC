using TidyC.Core.Scan;

namespace TidyC.Core.Tests;

public sealed class TreeOpsTests
{
    [Fact]
    public void Aggregate_RollsUpDirectAndChildBytes()
    {
        var root = new SpaceNode { Name = "C:\\", FullPath = "C:\\", Kind = NodeKind.Volume };
        var users = new SpaceNode { Name = "Users", FullPath = @"C:\Users", Kind = NodeKind.Directory };
        var child = new SpaceNode { Name = "Docs", FullPath = @"C:\Users\Docs", Kind = NodeKind.Directory };
        users.AddDirectFile(1_000, 1_000);
        child.AddDirectFile(4_000, 4_000);
        users.AddChild(child);
        root.AddChild(users);
        root.AddDirectFile(500, 500);

        root.Aggregate();

        Assert.Equal(5_500, root.AllocatedBytes);
        Assert.Equal(3, root.FileCount);
        Assert.Equal(2, root.DirectoryCount);
        Assert.Equal(5_000, users.AllocatedBytes);
        Assert.Equal(1, users.DirectFileCount);
    }

    [Fact]
    public void TopDirectories_SortsBySizeDescending()
    {
        var root = new SpaceNode { Name = "C:\\", FullPath = "C:\\", Kind = NodeKind.Volume };
        root.AddChild(new SpaceNode { Name = "Small", FullPath = @"C:\Small", Kind = NodeKind.Directory, AllocatedBytes = 10 });
        root.AddChild(new SpaceNode { Name = "Big", FullPath = @"C:\Big", Kind = NodeKind.Directory, AllocatedBytes = 900 });
        root.AddChild(new SpaceNode { Name = "Mid", FullPath = @"C:\Mid", Kind = NodeKind.Directory, AllocatedBytes = 100 });

        List<FolderFact> top = TreeOps.TopDirectories(root, 2);

        Assert.Equal(2, top.Count);
        Assert.Equal(@"C:\Big", top[0].Path);
        Assert.Equal(@"C:\Mid", top[1].Path);
    }

    [Fact]
    public void CategoryOfPath_ClassifiesSystemBuckets()
    {
        Assert.Equal(StorageCategories.WindowsSystem, TreeOps.CategoryOfPath(@"C:\Windows\System32\a.dll"));
        Assert.Equal(StorageCategories.AppData, TreeOps.CategoryOfPath(@"C:\Users\Admin\AppData\Local\Temp\x.tmp"));
        Assert.Equal(StorageCategories.UserProfiles, TreeOps.CategoryOfPath(@"C:\Users\Admin\Documents\a.docx"));
        Assert.Equal(StorageCategories.ProgramFiles, TreeOps.CategoryOfPath(@"C:\Program Files\Vendor\app.exe"));
        Assert.Equal(StorageCategories.ProgramFiles, TreeOps.CategoryOfPath(@"C:\Program Files (x86)\Vendor\app.exe"));
        Assert.Equal(StorageCategories.ProtectedSystem, TreeOps.CategoryOfPath(@"C:\System Volume Information\tracking.log"));
        Assert.Equal(StorageCategories.Quarantine, TreeOps.CategoryOfPath(@"C:\TidyC_Quarantine\BATCH_1\payload\a.bin"));
        Assert.Equal(StorageCategories.Other, TreeOps.CategoryOfPath(@"C:\Custom\data.bin"));
    }

    [Fact]
    public void Reconcile_ComputesNonNegativeBlindspot()
    {
        var volume = new VolumeFacts
        {
            TotalBytes = 100L * 1024 * 1024 * 1024,
            FreeBytes = 40L * 1024 * 1024 * 1024,
            ClusterSizeBytes = 4096,
        };

        VolumeReconciliation reconciled = TreeOps.Reconcile(volume, 50L * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024);

        Assert.Equal(60L * 1024 * 1024 * 1024, reconciled.NtfsPhysicalUsedBytes);
        Assert.Equal(10L * 1024 * 1024 * 1024, reconciled.PermissionBlindspotBytes);
        Assert.InRange(reconciled.AttributionRatio, 0.8, 0.84);
        Assert.True(reconciled.Reconciled);
    }
}
