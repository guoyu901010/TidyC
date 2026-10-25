using System.IO;
using System.Windows;
using TidyC.Core.Clean;
using TidyC.Core.Rules;
using TidyC.Core.Scan;
using TidyC.Core.Sys;

namespace TidyC.App.Services;

/// <summary>
/// Process-wide composition root. Holds the validated rule set, the scan engine,
/// the AppData analyzer and the single quarantine manager, so every view reads
/// the same facts instead of re-scanning behind the user's back.
/// </summary>
public sealed class AppServices
{
    private AppServices(bool demo)
    {
        IsDemo = demo;
        RuleReport = RuleValidator.Validate();
        RuleError = RuleReport.IsValid ? null : RuleReport.ToText();

        (QuarantineManager manager, string? warning) = CreateQuarantineManager(demo);
        Quarantine = manager;
        QuarantineWarning = warning;

        Scanner = new ScanService();
        AppData = new AppDataAnalyzer(BuiltInRules.All);
        Planner = new CleanupPlanner(BuiltInRules.All);
        History = new ScanHistoryService();
        Wsl = new WslService();
    }

    public static AppServices Current { get; private set; } = null!;

    public static AppServices Initialize(bool demo = false)
    {
        Current = new AppServices(demo);
        if (demo)
        {
            DemoData.Seed(Current);
        }

        return Current;
    }

    public bool IsDemo { get; }

    public RuleValidationReport RuleReport { get; }

    public string? RuleError { get; }

    public bool IsRuleSetValid => RuleReport.IsValid;

    public ScanService Scanner { get; }

    public AppDataAnalyzer AppData { get; }

    public CleanupPlanner Planner { get; }

    public QuarantineManager Quarantine { get; }

    public string? QuarantineWarning { get; }

    public ScanHistoryService History { get; }

    public WslService Wsl { get; }

    public ScanResult? Scan { get; private set; }

    public AppDataAnalysisReport? AppDataReport { get; private set; }

    public CleanupPlan? Plan { get; private set; }

    public bool IsElevated => ElevationHelper.IsElevated;

    public string EngineBadge => Scan is null
        ? (ScanService.CanUseMft(@"C:\") ? "MFT 直读引擎待命" : "目录遍历引擎（未提权）")
        : Scan.EngineType == ScanEngines.MftStreamReader
            ? "MFT 直读引擎"
            : "目录遍历引擎";

    public event EventHandler? ScanUpdated;

    public void ApplyScan(ScanResult scan, AppDataAnalysisReport? appData = null, CleanupPlan? plan = null)
    {
        Scan = scan;
        AppDataReport = appData;
        Plan = plan;

        if (plan is not null)
        {
            scan.Reconciliation.WhitelistSafeCleanableBytes = plan.TotalBytes;
        }

        ScanUpdated?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyAppData(AppDataAnalysisReport report, CleanupPlan? plan = null)
    {
        AppDataReport = report;
        if (plan is not null)
        {
            Plan = plan;
        }

        ScanUpdated?.Invoke(this, EventArgs.Empty);
    }

    public void NotifyPlanChanged() => ScanUpdated?.Invoke(this, EventArgs.Empty);

    /// <summary>Reloads the deterministic demo snapshot (used by --demo and the scan button in demo mode).</summary>
    public void LoadDemoSnapshot()
    {
        DemoData.Seed(this);
        ScanUpdated?.Invoke(this, EventArgs.Empty);
    }

    private static (QuarantineManager Manager, string? Warning) CreateQuarantineManager(bool demo)
    {
        if (demo)
        {
            // The demo must never write sample batches into the production
            // quarantine; it uses a clearly separate sandbox directory.
            string demoRoot = Path.Combine(AppPaths.DataDirectory, "DemoQuarantine");
            Directory.CreateDirectory(demoRoot);
            return (new QuarantineManager(new QuarantineConfiguration { Root = demoRoot }), null);
        }

        string preferred = QuarantineConfiguration.DefaultRoot;
        try
        {
            Directory.CreateDirectory(preferred);
            var manager = new QuarantineManager(new QuarantineConfiguration { Root = preferred });
            return (manager, null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            string fallback = AppPaths.QuarantineFallback;
            Directory.CreateDirectory(fallback);
            var manager = new QuarantineManager(new QuarantineConfiguration { Root = fallback });
            return (manager,
                $"无法写入 {preferred}（需要管理员权限），隔离区已回退到用户目录：{fallback}");
        }
    }
}
