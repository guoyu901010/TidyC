using System.Diagnostics;
using TidyC.Core.Scan;
using TidyC.Core.Sys;

namespace TidyC.Core.Rules;

public sealed class AppDataScanRequest
{
    public List<string> Scopes { get; init; } = new() { "Local", "Roaming", "LocalLow" };

    public bool IncludeGray { get; init; } = true;

    public int MaxGrayItems { get; init; } = 40;

    public int GrayMaxDepth { get; init; } = 6;

    /// <summary>Cap per rule so a machine with 400 Store packages cannot stall the UI.</summary>
    public int MaxItemsPerRule { get; init; } = 80;
}

public sealed class AppDataScanProgress
{
    public string Phase { get; init; } = string.Empty;

    public string? CurrentPath { get; init; }

    public int MatchedItems { get; init; }

    public long Bytes { get; init; }
}

/// <summary>
/// Builds the four-level AppData report: every item carries purpose, evidence and
/// consequence, and the four risk buckets are reconciled against the measured
/// total. Unknown folders become GRAY and are hard locked.
/// </summary>
public sealed class AppDataAnalyzer
{
    private readonly IReadOnlyList<CleanRule> _rules;
    private readonly IFileSystemProbe _probe;
    private readonly PathExpander _expander;
    private readonly Func<DateTime> _clock;

    public AppDataAnalyzer(
        IReadOnlyList<CleanRule>? rules = null,
        IFileSystemProbe? probe = null,
        PathExpander? expander = null,
        Func<DateTime>? clock = null)
    {
        _rules = rules ?? BuiltInRules.All;
        _probe = probe ?? new FileSystemProbe();
        _expander = expander ?? new PathExpander();
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public IReadOnlyList<CleanRule> Rules => _rules;

    public string RulesetSignature => RuleValidator.ComputeSignature(_rules);

    public AppDataAnalysisReport Analyze(
        AppDataScanRequest? request = null,
        IProgress<AppDataScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        request ??= new AppDataScanRequest();
        var stopwatch = Stopwatch.StartNew();
        DateTime now = _clock();

        IReadOnlyList<(string Scope, string Root)> scopes = ResolveScopes(request.Scopes);
        var report = new AppDataAnalysisReport
        {
            ScanId = $"APPDATA_{now:yyyyMMdd_HHmmss}",
            GeneratedAt = now,
            EngineVersion = BuiltInRules.EngineVersion,
            RulesetSignature = RulesetSignature,
            TargetScopes = request.Scopes.ToList(),
        };

        if (scopes.Count == 0)
        {
            report.Warnings.Add("未找到任何 AppData 目标目录（Local / Roaming / LocalLow）");
            return report;
        }

        IReadOnlyList<CompiledRule> compiled = RuleMatcher.CompileAll(_rules, _expander)
            .Where(c => scopes.Any(s => PathKey.IsUnder(c.ExpandedPattern, s.Root)))
            .ToList();

        IReadOnlyList<RuleMatch> matches = RuleScanner.ScanTargets(
            compiled,
            _probe,
            accept: path => scopes.Any(s => PathKey.IsUnder(path, s.Root)),
            ct: ct,
            progress: null,
            maxMatchesPerRule: request.MaxItemsPerRule);

        var groups = new Dictionary<string, ApplicationGroup>(StringComparer.OrdinalIgnoreCase);
        var coveredPaths = new List<string>();

        foreach (RuleMatch match in matches)
        {
            ct.ThrowIfCancellationRequested();

            (string scopeName, string scopeRoot) = FindScope(scopes, match.AbsolutePath);
            string? relative = PathKey.RelativeTo(match.AbsolutePath, scopeRoot);
            if (relative is null)
            {
                continue;
            }

            string relativeWithScope = string.IsNullOrEmpty(relative)
                ? scopeName
                : scopeName + "\\" + relative;

            if (!groups.TryGetValue(match.Rule.AppId, out ApplicationGroup? group))
            {
                group = new ApplicationGroup
                {
                    AppId = match.Rule.AppId,
                    AppName = match.Rule.AppName,
                };
                groups[match.Rule.AppId] = group;
            }

            var item = new AppDataItem
            {
                RelativePath = relativeWithScope,
                AbsolutePath = match.AbsolutePath,
                SizeBytes = match.AllocatedBytes,
                FileCount = match.FileCount,
                RiskLevel = match.Rule.Risk,
                Purpose = match.Rule.Purpose,
                Evidence = match.Rule.Evidence,
                Consequence = match.Rule.Consequence,
                RuleId = match.Rule.Id,
                IsDirectory = match.IsDirectory,
                LastWriteUtc = match.LastWriteUtc,
            };

            group.Items.Add(item);
            group.TotalBytes += item.SizeBytes;
            coveredPaths.Add(item.AbsolutePath);

            progress?.Report(new AppDataScanProgress
            {
                Phase = "match",
                CurrentPath = match.AbsolutePath,
                MatchedItems = coveredPaths.Count,
                Bytes = group.TotalBytes,
            });
        }

        if (request.IncludeGray)
        {
            AddGrayItems(scopes, coveredPaths, groups, request, ct);
        }

        report.Applications = groups.Values
            .Where(g => g.Items.Count > 0)
            .OrderByDescending(g => g.TotalBytes)
            .ThenBy(g => g.AppName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (ApplicationGroup group in report.Applications)
        {
            group.Items = group.Items
                .OrderBy(item => item.RiskLevel)
                .ThenByDescending(item => item.SizeBytes)
                .ToList();
        }

        report.Summary = Summarize(report.Applications);
        stopwatch.Stop();
        report.ScanDurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3);
        return report;
    }

    private void AddGrayItems(
        IReadOnlyList<(string Scope, string Root)> scopes,
        List<string> coveredPaths,
        Dictionary<string, ApplicationGroup> groups,
        AppDataScanRequest request,
        CancellationToken ct)
    {
        var grayCandidates = new List<(string ScopeName, string Path, PathMeasure Measure)>();

        foreach ((string scopeName, string scopeRoot) in scopes)
        {
            ct.ThrowIfCancellationRequested();
            if (!_probe.DirectoryExists(scopeRoot))
            {
                continue;
            }

            DirectoryListing listing = _probe.List(scopeRoot);
            foreach (FileSystemEntryInfo entry in listing.Entries)
            {
                if (!entry.IsDirectory || entry.IsReparsePoint)
                {
                    continue;
                }

                bool covered = coveredPaths.Any(p => PathKey.IsUnder(p, entry.FullPath));
                if (covered)
                {
                    continue;
                }

                ct.ThrowIfCancellationRequested();
                PathMeasure measure = _probe.Measure(entry.FullPath, maxDepth: request.GrayMaxDepth, ct: ct);
                if (measure.AllocatedBytes <= 0)
                {
                    continue;
                }

                grayCandidates.Add((scopeName, entry.FullPath, measure));
            }
        }

        foreach ((string scopeName, string path, PathMeasure measure) in grayCandidates
                     .OrderByDescending(c => c.Measure.AllocatedBytes)
                     .Take(request.MaxGrayItems))
        {
            string appName = PathKey.Leaf(path);
            string appId = "unknown." + new string(appName
                .ToLowerInvariant()
                .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
                .ToArray()).Trim('-');

            if (string.IsNullOrEmpty(appId) || appId == "unknown.")
            {
                appId = "unknown.misc";
            }

            if (!groups.TryGetValue(appId, out ApplicationGroup? group))
            {
                group = new ApplicationGroup { AppId = appId, AppName = appName };
                groups[appId] = group;
            }

            string? relative = PathKey.RelativeTo(path, scopes.First(s => s.Scope == scopeName).Root);

            var item = new AppDataItem
            {
                RelativePath = string.IsNullOrEmpty(relative) ? scopeName : scopeName + "\\" + relative,
                AbsolutePath = path,
                SizeBytes = measure.AllocatedBytes,
                FileCount = measure.FileCount,
                RiskLevel = RiskLevel.Gray,
                Purpose = "未识别目录：知识库未收录其用途",
                Evidence = "未命中任何 BuiltInRule 白名单条目",
                Consequence = "默认保留，硬编码禁止勾选；确认用途前不得清理",
                RuleId = "gray.fallback",
                IsDirectory = true,
                LastWriteUtc = measure.LastWriteUtc,
            };

            group.Items.Add(item);
            group.TotalBytes += item.SizeBytes;
        }
    }

    private static AppDataSummary Summarize(IEnumerable<ApplicationGroup> groups)
    {
        var summary = new AppDataSummary();
        foreach (ApplicationGroup group in groups)
        {
            foreach (AppDataItem item in group.Items)
            {
                switch (item.RiskLevel)
                {
                    case RiskLevel.Green:
                        summary.GreenSafeBytes += item.SizeBytes;
                        break;
                    case RiskLevel.Yellow:
                        summary.YellowCautionBytes += item.SizeBytes;
                        break;
                    case RiskLevel.Red:
                        summary.RedPreserveBytes += item.SizeBytes;
                        break;
                    default:
                        summary.GrayUnrecognizedBytes += item.SizeBytes;
                        break;
                }
            }
        }

        summary.TotalSizeBytes = summary.GreenSafeBytes + summary.YellowCautionBytes +
                                 summary.RedPreserveBytes + summary.GrayUnrecognizedBytes;
        return summary;
    }

    private IReadOnlyList<(string Scope, string Root)> ResolveScopes(IEnumerable<string> requested)
    {
        var scopes = new List<(string Scope, string Root)>();
        foreach (string scope in requested)
        {
            string? template = scope.Trim().ToUpperInvariant() switch
            {
                "LOCAL" => @"%LOCALAPPDATA%",
                "ROAMING" => @"%APPDATA%",
                "LOCALLOW" => @"%USERPROFILE%\AppData\LocalLow",
                _ => null,
            };

            if (template is null)
            {
                continue;
            }

            string root = PathKey.Normalize(_expander.Expand(template));
            if (_probe.DirectoryExists(root))
            {
                scopes.Add((scope, root));
            }
        }

        return scopes;
    }

    private static (string ScopeName, string Root) FindScope(
        IReadOnlyList<(string Scope, string Root)> scopes,
        string path)
    {
        (string Scope, string Root) best = scopes[0];
        int bestLength = -1;
        foreach ((string scope, string root) in scopes)
        {
            if (PathKey.IsUnder(path, root) && root.Length > bestLength)
            {
                best = (scope, root);
                bestLength = root.Length;
            }
        }

        return best;
    }
}
