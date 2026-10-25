using TidyC.Core.Scan;

namespace TidyC.Core.Rules;

public sealed class CleanupCandidate
{
    public required CleanRule Rule { get; init; }

    public required string Path { get; init; }

    public long SizeBytes { get; init; }

    public long FileCount { get; init; }

    public bool IsDirectory { get; init; }

    public RiskLevel Risk => Rule.Risk;
}

public sealed class CleanupPlan
{
    public List<CleanupCandidate> Candidates { get; } = new();

    public long GreenBytes { get; set; }

    public long YellowBytes { get; set; }

    public long TotalBytes => GreenBytes + YellowBytes;

    public bool IsEmpty => Candidates.Count == 0;

    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    public List<string> Warnings { get; } = new();

    public IEnumerable<CleanupCandidate> Selectable => Candidates.Where(c => c.Risk.IsSelectable());
}

/// <summary>
/// Turns the whitelist into a concrete, honest cleanup offer: only GREEN/YELLOW
/// moveable rule targets that actually exist, with the measured allocated bytes
/// that would be released after the quarantine is purged.
/// </summary>
public sealed class CleanupPlanner
{
    private readonly IReadOnlyList<CleanRule> _rules;
    private readonly IFileSystemProbe _probe;
    private readonly PathExpander _expander;

    public CleanupPlanner(
        IReadOnlyList<CleanRule>? rules = null,
        IFileSystemProbe? probe = null,
        PathExpander? expander = null)
    {
        _rules = rules ?? BuiltInRules.All;
        _probe = probe ?? new FileSystemProbe();
        _expander = expander ?? new PathExpander();
    }

    public CleanupPlan Build(CancellationToken ct = default, bool enforceProtectedPaths = true)
    {
        var plan = new CleanupPlan { GeneratedAt = DateTime.UtcNow };

        IReadOnlyList<CompiledRule> compiled = RuleMatcher.CompileAll(
                _rules.Where(r => r.Enabled && r.IsMoveable && r.Risk.IsSelectable()),
                _expander)
            .ToList();

        IReadOnlyList<RuleMatch> matches = RuleScanner.ScanTargets(
            compiled,
            _probe,
            accept: path => !enforceProtectedPaths || !RuleValidator.IsForbiddenCleanRoot(path),
            ct: ct);

        foreach (RuleMatch match in matches)
        {
            var candidate = new CleanupCandidate
            {
                Rule = match.Rule,
                Path = match.AbsolutePath,
                SizeBytes = match.AllocatedBytes,
                FileCount = match.FileCount,
                IsDirectory = match.IsDirectory,
            };

            plan.Candidates.Add(candidate);
            if (match.Rule.Risk == RiskLevel.Green)
            {
                plan.GreenBytes += match.AllocatedBytes;
            }
            else
            {
                plan.YellowBytes += match.AllocatedBytes;
            }
        }

        plan.Candidates.Sort(static (a, b) => b.SizeBytes.CompareTo(a.SizeBytes));

        if (plan.IsEmpty)
        {
            plan.Warnings.Add("当前机器上未发现可清理的白名单缓存");
        }

        return plan;
    }

    /// <summary>Convenience for the Overview footer: total whitelist-cleanable bytes.</summary>
    public long MeasureCleanableBytes(CancellationToken ct = default) => Build(ct).TotalBytes;
}
