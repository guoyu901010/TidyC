using TidyC.Core.Scan;

namespace TidyC.Core.Tests;

public sealed class ScanPipelineTests
{
    private static VolumeFacts Volume() => new()
    {
        Root = "C:\\",
        TotalBytes = 512L * 1024 * 1024 * 1024,
        FreeBytes = 300L * 1024 * 1024 * 1024,
        ClusterSizeBytes = 4096,
        SerialNumber = 0x8F94B2C0,
        IsNtfs = true,
        FileSystem = "NTFS",
    };

    private static MftFileEntry Directory(ulong frn, ulong parent, string name)
    {
        var link = new MftNameLink { ParentFileReferenceNumber = parent, Name = name, Namespace = 1 };
        var entry = new MftFileEntry
        {
            FileReferenceNumber = frn,
            IsDirectory = true,
            PrimaryLink = link,
        };
        entry.Links.Add(link);
        return entry;
    }

    private static MftFileEntry File(ulong frn, ulong parent, string name, long allocated, params (ulong Parent, string Name)[] extraLinks)
    {
        var primary = new MftNameLink { ParentFileReferenceNumber = parent, Name = name, Namespace = 1 };
        var entry = new MftFileEntry
        {
            FileReferenceNumber = frn,
            IsDirectory = false,
            AllocatedBytes = allocated,
            LogicalBytes = allocated,
            PrimaryLink = primary,
            LastWriteUtc = new DateTime(2025, 5, 20, 9, 41, 22, DateTimeKind.Utc),
        };
        entry.Links.Add(primary);
        foreach ((ulong linkParent, string linkName) in extraLinks)
        {
            entry.Links.Add(new MftNameLink { ParentFileReferenceNumber = linkParent, Name = linkName, Namespace = 1 });
        }

        return entry;
    }

    [Fact]
    public void MftScanBuilder_BuildsTreeAndDeduplicatesHardLinks()
    {
        var entries = new List<MftFileEntry>
        {
            Directory(5, 5, "C:\\"),
            Directory(10, 5, "Users"),
            Directory(11, 10, "Admin"),
            File(20, 11, "shared.bin", 8192, (11, "alias.bin")),
            File(21, 5, "pagefile-ish.sys", 4096),
        };

        var builder = new ScanResultBuilder("C:\\", ScanEngines.MftStreamReader, Volume(), new ScanOptions());
        MftScanBuilder.Feed(entries, builder);
        ScanResult result = builder.Build(1.25, entries.Count);

        Assert.Equal(2, result.TotalFilesCount);
        Assert.Equal(2, result.TotalDirectoriesCount);
        Assert.Equal(8192 + 4096, result.Reconciliation.EngineAttributedBytes);
        Assert.Equal(1, result.HardLinkDeduplicatedCount);
        Assert.Equal(8192, result.HardLinkSavedBytes);
        Assert.Equal(1.25, result.DurationSeconds);
        Assert.Equal("0x8F94B2C0", result.VolumeSerialNumber);

        SpaceNode users = result.Root.Children.Single(c => c.Name == "Users");
        SpaceNode admin = users.Children.Single(c => c.Name == "Admin");
        Assert.Equal(8192, admin.DirectAllocatedBytes);
        Assert.Equal(8192, users.AllocatedBytes);
        Assert.Equal(8192 + 4096, result.Root.AllocatedBytes);
        Assert.Contains(result.TopLargestFiles, f => f.Path.EndsWith("shared.bin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanResultBuilder_ClassifiesAndReconciles()
    {
        var builder = new ScanResultBuilder("C:\\", ScanEngines.MftStreamReader, Volume(), new ScanOptions { TopFolderCount = 5 });
        builder.Spectrum.AddForPath(@"C:\Windows\System32\a.dll", 1000);
        builder.Spectrum.AddForPath(@"C:\Users\Admin\AppData\Local\Cache\x.bin", 2000);
        builder.Spectrum.AddForPath(@"C:\Users\Admin\Documents\y.docx", 4000);
        builder.Spectrum.AddForPath(@"C:\Program Files\Vendor\z.exe", 8000);
        builder.Files = 4;
        builder.AttributedBytes = 15000;
        builder.Root.AddDirectFile(15000, 15000);

        ScanResult result = builder.Build(0.5, 4);

        Assert.Equal(1000, result.Categories.WindowsSystemBytes);
        Assert.Equal(2000, result.Categories.AppDataBytes);
        Assert.Equal(4000, result.Categories.UserProfilesBytes);
        Assert.Equal(8000, result.Categories.ProgramFilesBytes);
        Assert.Equal(15000, result.Categories.TotalBytes);
        Assert.Equal(15000, result.Reconciliation.EngineAttributedBytes);
        Assert.True(result.Reconciliation.PermissionBlindspotBytes >= 0);
        Assert.Equal(4, result.TotalFilesCount);
        Assert.True(result.IsElevated == TidyC.Core.Sys.ElevationHelper.IsElevated);
    }

    [Fact]
    public void ScanRecord_ReportsHonestBlindspot()
    {
        var builder = new ScanResultBuilder("C:\\", ScanEngines.DirectoryTreeWalker, Volume(), new ScanOptions());
        builder.AttributedBytes = 1000;
        builder.Files = 1;
        builder.Root.AddDirectFile(1000, 1000);
        builder.AccessDenied = 7;
        builder.ReparsePointsSkipped = 3;
        builder.Warnings.Add("test warning");

        ScanResult result = builder.Build(0.1, 1);

        Assert.Equal(7, result.AccessDeniedCount);
        Assert.Equal(3, result.ReparsePointsSkippedCount);
        Assert.Contains("test warning", result.Warnings);
        Assert.Equal(212L * 1024 * 1024 * 1024, result.Reconciliation.NtfsPhysicalUsedBytes);
        Assert.Equal(212L * 1024 * 1024 * 1024 - 1000, result.Reconciliation.PermissionBlindspotBytes);
    }
}
