using TidyC.Core.Rules;
using TidyC.Core.Tests.TestSupport;

namespace TidyC.Core.Tests;

public sealed class AppDataAnalyzerTests
{
    [Fact]
    public void ClassifiesGreenCache()
    {
        using var fixture = new AppDataFixture();
        fixture.Tree.File(@"profile\AppData\Local\App\Cache\a.bin", 64 * 1024);
        CleanRule rule = TestRules.Rule("Cache_01", RiskLevel.Green, new[] { @"%LOCALAPPDATA%\App\Cache" });

        AppDataAnalysisReport report = fixture.CreateAnalyzer(new[] { rule }).Analyze();

        AppDataItem item = Assert.Single(report.Applications.SelectMany(a => a.Items));
        Assert.Equal(RiskLevel.Green, item.RiskLevel);
        Assert.True(item.SizeBytes > 0);
        Assert.False(item.IsLocked);
        Assert.True(report.Summary.GreenSafeBytes >= item.SizeBytes);
    }

    [Fact]
    public void ClassifiesRedLocalStorage()
    {
        using var fixture = new AppDataFixture();
        fixture.Tree.File(@"profile\AppData\Local\App\Local Storage\leveldb\000003.log", 32 * 1024);
        CleanRule rule = TestRules.Rule("LocalStorage_01", RiskLevel.Red, new[] { @"%LOCALAPPDATA%\App\Local Storage" });

        AppDataAnalysisReport report = fixture.CreateAnalyzer(new[] { rule }).Analyze();

        AppDataItem item = Assert.Single(report.Applications.SelectMany(a => a.Items));
        Assert.Equal(RiskLevel.Red, item.RiskLevel);
        Assert.True(item.IsLocked);
        Assert.False(item.IsSelectable);
        Assert.Equal(0, report.Summary.GreenSafeBytes);
        Assert.True(report.Summary.RedPreserveBytes > 0);
    }

    [Fact]
    public void MatchesWildcardVersionFolder()
    {
        using var fixture = new AppDataFixture();
        fixture.Tree.File(@"profile\AppData\Local\Google\Chrome\User Data\Default\Cache\data_0", 128 * 1024);
        CleanRule rule = TestRules.Rule(
            "Chrome_Cache_Wildcard",
            RiskLevel.Green,
            new[] { @"%LOCALAPPDATA%\Google\Chrome\User Data\*\Cache" });

        AppDataAnalysisReport report = fixture.CreateAnalyzer(new[] { rule }).Analyze();

        AppDataItem item = Assert.Single(report.Applications.SelectMany(a => a.Items));
        Assert.Contains("Default", item.AbsolutePath);
        Assert.Equal(RiskLevel.Green, item.RiskLevel);
    }

    [Fact]
    public void MatchesCaseInsensitively()
    {
        using var fixture = new AppDataFixture();
        fixture.Tree.File(@"profile\AppData\Local\App\CACHE\x.bin", 8 * 1024);
        CleanRule rule = TestRules.Rule("Case_01", RiskLevel.Green, new[] { @"%LOCALAPPDATA%\App\Cache" });

        AppDataAnalysisReport report = fixture.CreateAnalyzer(new[] { rule }).Analyze();

        Assert.Single(report.Applications.SelectMany(a => a.Items));
    }

    [Fact]
    public void MeasuresDirectoryRuleRecursively()
    {
        using var fixture = new AppDataFixture();
        fixture.Tree.File(@"profile\AppData\Local\npm-cache\_cacache\content.bin", 200 * 1024);
        fixture.Tree.File(@"profile\AppData\Local\npm-cache\_logs\debug.log", 4 * 1024);
        CleanRule rule = TestRules.Rule("Npm_01", RiskLevel.Green, new[] { @"%LOCALAPPDATA%\npm-cache\*" }, appId: "npm");

        AppDataAnalysisReport report = fixture.CreateAnalyzer(new[] { rule }).Analyze();

        var items = report.Applications.SelectMany(a => a.Items).ToList();
        Assert.Equal(2, items.Count);
        Assert.True(items.Sum(i => i.SizeBytes) >= 204 * 1024);
    }

    [Fact]
    public void UnknownFolderBecomesGrayAndLocked()
    {
        using var fixture = new AppDataFixture();
        fixture.Tree.File(@"profile\AppData\Local\MysteryApp\payload.bin", 128 * 1024);
        CleanRule rule = TestRules.Rule("Known_01", RiskLevel.Green, new[] { @"%LOCALAPPDATA%\KnownApp\Cache" });

        AppDataAnalysisReport report = fixture.CreateAnalyzer(new[] { rule }).Analyze();

        AppDataItem gray = Assert.Single(report.Applications.SelectMany(a => a.Items));
        Assert.Equal(RiskLevel.Gray, gray.RiskLevel);
        Assert.True(gray.IsLocked);
        Assert.Contains("未识别", gray.Purpose);
        Assert.True(report.Summary.GrayUnrecognizedBytes > 0);
    }

    [Fact]
    public void DoesNotTraverseJunctions()
    {
        using var fixture = new AppDataFixture();
        string cache = fixture.Tree.Dir("profile", "AppData", "Local", "App", "Cache");
        fixture.Tree.File(@"profile\AppData\Local\App\Cache\local.bin", 32 * 1024);
        string outside = fixture.Tree.Dir("outside");
        fixture.Tree.File(@"outside\huge.bin", 2 * 1024 * 1024);
        string junction = Path.Combine(cache, "link");
        if (!Junctions.TryCreate(outside, junction))
        {
            // Junctions are unavailable in some restricted environments; the
            // parser-level reparse tests still cover the same invariant.
            return;
        }

        CleanRule rule = TestRules.Rule("Cache_Link", RiskLevel.Green, new[] { @"%LOCALAPPDATA%\App\Cache" });

        AppDataAnalysisReport report = fixture.CreateAnalyzer(new[] { rule }).Analyze();

        AppDataItem item = Assert.Single(report.Applications.SelectMany(a => a.Items));
        Assert.InRange(item.SizeBytes, 1, 256 * 1024);
    }

    [Fact]
    public void EveryItemHasCompleteNarrative()
    {
        using var fixture = new AppDataFixture();
        fixture.Tree.File(@"profile\AppData\Local\App\Cache\a.bin", 16 * 1024);
        fixture.Tree.File(@"profile\AppData\Local\App\Local Storage\b.bin", 16 * 1024);
        fixture.Tree.File(@"profile\AppData\Local\Unknown\c.bin", 16 * 1024);

        CleanRule[] rules =
        {
            TestRules.Rule("N1", RiskLevel.Green, new[] { @"%LOCALAPPDATA%\App\Cache" }),
            TestRules.Rule("N2", RiskLevel.Red, new[] { @"%LOCALAPPDATA%\App\Local Storage" }),
        };

        AppDataAnalysisReport report = fixture.CreateAnalyzer(rules).Analyze();

        Assert.NotEmpty(report.Applications);
        foreach (AppDataItem item in report.Applications.SelectMany(a => a.Items))
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Purpose));
            Assert.False(string.IsNullOrWhiteSpace(item.Evidence));
            Assert.False(string.IsNullOrWhiteSpace(item.Consequence));
            Assert.Contains("BuiltInRule", item.Evidence);
        }
    }

    [Fact]
    public void SummaryReconcilesWithItems()
    {
        using var fixture = new AppDataFixture();
        fixture.Tree.File(@"profile\AppData\Local\App\Cache\a.bin", 100 * 1024);
        fixture.Tree.File(@"profile\AppData\Local\App\Logs\b.log", 50 * 1024);
        fixture.Tree.File(@"profile\AppData\Local\Unknown\c.bin", 30 * 1024);

        CleanRule[] rules =
        {
            TestRules.Rule("S1", RiskLevel.Green, new[] { @"%LOCALAPPDATA%\App\Cache" }),
            TestRules.Rule("S2", RiskLevel.Yellow, new[] { @"%LOCALAPPDATA%\App\Logs" }),
        };

        AppDataAnalysisReport report = fixture.CreateAnalyzer(rules).Analyze();

        long itemSum = report.Applications.SelectMany(a => a.Items).Sum(i => i.SizeBytes);
        Assert.Equal(itemSum, report.Summary.TotalSizeBytes);
        Assert.Equal(
            report.Summary.GreenSafeBytes + report.Summary.YellowCautionBytes +
            report.Summary.RedPreserveBytes + report.Summary.GrayUnrecognizedBytes,
            report.Summary.TotalSizeBytes);
    }

    [Fact]
    public void SerializesContractSchema()
    {
        using var fixture = new AppDataFixture();
        fixture.Tree.File(@"profile\AppData\Local\App\Cache\a.bin", 64 * 1024);
        CleanRule rule = TestRules.Rule("Json_01", RiskLevel.Green, new[] { @"%LOCALAPPDATA%\App\Cache" });

        string json = AppDataJson.Serialize(fixture.CreateAnalyzer(new[] { rule }).Analyze());

        Assert.Contains("\"scan_id\"", json);
        Assert.Contains("\"ruleset_signature\"", json);
        Assert.Contains("\"target_scopes\"", json);
        Assert.Contains("\"green_safe_bytes\"", json);
        Assert.Contains("\"risk_level\": \"GREEN\"", json);
        Assert.Contains("\"is_locked\": false", json);
        Assert.Contains("\"evidence\"", json);
    }

    [Fact]
    public void RespectsRequestedScopes()
    {
        using var fixture = new AppDataFixture();
        fixture.Tree.File(@"profile\AppData\Local\App\Cache\local.bin", 32 * 1024);
        fixture.Tree.File(@"profile\AppData\Roaming\App\Cache\roaming.bin", 32 * 1024);
        CleanRule[] rules =
        {
            TestRules.Rule("Scope_Local", RiskLevel.Green, new[] { @"%LOCALAPPDATA%\App\Cache" }),
            TestRules.Rule("Scope_Roaming", RiskLevel.Green, new[] { @"%APPDATA%\App\Cache" }),
        };

        AppDataAnalysisReport report = fixture.CreateAnalyzer(rules).Analyze(
            new AppDataScanRequest { Scopes = new List<string> { "Local" }, IncludeGray = false });

        var items = report.Applications.SelectMany(a => a.Items).ToList();
        Assert.Single(items);
        Assert.StartsWith(@"Local\", items[0].RelativePath);
        Assert.Equal(new[] { "Local" }, report.TargetScopes);
    }

    [Fact]
    public void IgnoresMissingTargets()
    {
        using var fixture = new AppDataFixture();
        CleanRule rule = TestRules.Rule("Missing_01", RiskLevel.Green, new[] { @"%LOCALAPPDATA%\Ghost\Cache" });

        AppDataAnalysisReport report = fixture.CreateAnalyzer(new[] { rule }).Analyze();

        Assert.Empty(report.Applications);
        Assert.Equal(0, report.Summary.TotalSizeBytes);
    }

    [Fact]
    public void ProtectsUwpPackageSandbox()
    {
        using var fixture = new AppDataFixture();
        fixture.Tree.File(
            @"profile\AppData\Local\Packages\Microsoft.WindowsStore_8wekyb3d8bbwe\LocalState\state.dat",
            128 * 1024);
        CleanRule rule = TestRules.Rule("Uwp_01", RiskLevel.Red, new[] { @"%LOCALAPPDATA%\Packages\*" }, appId: "microsoft.uwp");

        AppDataAnalysisReport report = fixture.CreateAnalyzer(new[] { rule }).Analyze();

        AppDataItem item = Assert.Single(report.Applications.SelectMany(a => a.Items));
        Assert.Equal(RiskLevel.Red, item.RiskLevel);
        Assert.True(item.IsLocked);
        Assert.Contains("Packages", item.AbsolutePath);
    }
}
