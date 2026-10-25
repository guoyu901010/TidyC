using TidyC.Core.Scan;

namespace TidyC.Core.Tests;

public sealed class SelfTestTests
{
    private static ScanResult Synthetic(string engine, long files, long directories, long bytes) => new()
    {
        EngineType = engine,
        TotalFilesCount = files,
        TotalDirectoriesCount = directories,
        Reconciliation = new VolumeReconciliation
        {
            EngineAttributedBytes = bytes,
            NtfsPhysicalUsedBytes = bytes + 1_000_000,
        },
    };

    [Fact]
    public void Compare_IdenticalEnginesPass()
    {
        ScanResult mft = Synthetic(ScanEngines.MftStreamReader, 123_456, 7_890, 1_000_000_000);
        ScanResult walker = Synthetic(ScanEngines.DirectoryTreeWalker, 123_456, 7_890, 1_000_000_000);

        SelfTestReport report = DualEngineSelfTest.Compare(mft, walker);

        Assert.True(report.Passed);
        Assert.Equal(1d, report.FileConsistency, 6);
        Assert.Equal(1d, report.ByteConsistency, 6);
        Assert.Contains("PASS", report.ToString());
    }

    [Fact]
    public void Compare_DetectsDivergence()
    {
        ScanResult mft = Synthetic(ScanEngines.MftStreamReader, 100_000, 5_000, 2_000_000_000);
        ScanResult walker = Synthetic(ScanEngines.DirectoryTreeWalker, 90_000, 5_000, 1_500_000_000);

        SelfTestReport report = DualEngineSelfTest.Compare(mft, walker);

        Assert.False(report.Passed);
        Assert.True(report.FileConsistency < 0.9999);
        Assert.True(report.ByteConsistency < 0.9999);
        Assert.NotEmpty(report.Notes);
        string json = DualEngineSelfTest.Serialize(report);
        Assert.Contains("\"passed\": false", json);
    }
}
