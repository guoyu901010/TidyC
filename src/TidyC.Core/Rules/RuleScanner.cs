using System.Text.RegularExpressions;
using TidyC.Core.Scan;
using TidyC.Core.Sys;

namespace TidyC.Core.Rules;

public sealed class RuleMatch
{
    public required CleanRule Rule { get; init; }

    public required string AbsolutePath { get; init; }

    public long AllocatedBytes { get; init; }

    public long LogicalBytes { get; init; }

    public long FileCount { get; init; }

    public bool IsDirectory { get; init; }

    public DateTime LastWriteUtc { get; init; }
}

/// <summary>
/// Expands compiled rules against the filesystem. Wildcard segments are resolved
/// by enumerating only the directory that contains them (never a full drive
/// walk), and junctions are never entered.
/// </summary>
public static class RuleScanner
{
    private static readonly RiskLevel[] SeverityOrder =
    {
        RiskLevel.Red, RiskLevel.Gray, RiskLevel.Yellow, RiskLevel.Green,
    };

    public static IReadOnlyList<RuleMatch> ScanTargets(
        IEnumerable<CompiledRule> rules,
        IFileSystemProbe probe,
        Func<string, bool>? accept = null,
        CancellationToken ct = default,
        IProgress<RuleMatch>? progress = null,
        int maxMatchesPerRule = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(probe);

        var byPath = new Dictionary<string, RuleMatch>(StringComparer.OrdinalIgnoreCase);

        foreach (CompiledRule compiled in rules)
        {
            ct.ThrowIfCancellationRequested();
            int matchedForRule = 0;

            foreach (string candidate in ResolvePattern(compiled.ExpandedPattern, probe, accept, ct, maxMatchesPerRule))
            {
                ct.ThrowIfCancellationRequested();
                if (matchedForRule >= maxMatchesPerRule)
                {
                    break;
                }

                if (!probe.FileExists(candidate) && !probe.DirectoryExists(candidate))
                {
                    continue;
                }

                bool isDirectory = probe.DirectoryExists(candidate);

                PathMeasure measure = probe.Measure(candidate);
                if (measure.AllocatedBytes <= 0)
                {
                    continue;
                }

                matchedForRule++;
                AddOrUpgrade(byPath, compiled.Rule, candidate, measure, isDirectory);
                progress?.Report(byPath[PathKey.Normalize(candidate)]);
            }
        }

        return byPath.Values
            .OrderByDescending(m => m.AllocatedBytes)
            .ToList();
    }

    /// <summary>
    /// Walks the pattern's literal segments and expands each wildcard segment
    /// against the directory above it. Returns concrete absolute paths.
    /// </summary>
    internal static IReadOnlyList<string> ResolvePattern(
        string expandedPattern,
        IFileSystemProbe probe,
        Func<string, bool>? accept,
        CancellationToken ct,
        int maxResults)
    {
        string[] segments = PathKey.Normalize(expandedPattern)
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 2 || !segments[0].EndsWith(':'))
        {
            return Array.Empty<string>();
        }

        var current = new List<string> { segments[0] + "\\" };
        var results = new List<string>();

        for (int i = 1; i < segments.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            string segment = segments[i];
            bool isLast = i == segments.Length - 1;
            var next = new List<string>();

            foreach (string parent in current)
            {
                if (segment.IndexOf('*') >= 0)
                {
                    if (!probe.DirectoryExists(parent))
                    {
                        continue;
                    }

                    Regex segmentRegex = RuleMatcher.BuildRegex(segment);
                    foreach (FileSystemEntryInfo entry in probe.List(parent).Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (entry.IsReparsePoint || !segmentRegex.IsMatch(entry.Name))
                        {
                            continue;
                        }

                        if (isLast)
                        {
                            if (accept is not null && !accept(entry.FullPath))
                            {
                                continue;
                            }

                            results.Add(entry.FullPath);
                            if (results.Count >= maxResults)
                            {
                                return results;
                            }
                        }
                        else if (entry.IsDirectory)
                        {
                            next.Add(entry.FullPath);
                        }
                    }
                }
                else
                {
                    string candidate = PathKey.Combine(parent, segment);
                    bool exists = probe.FileExists(candidate) || probe.DirectoryExists(candidate);
                    if (!exists)
                    {
                        continue;
                    }

                    if (isLast)
                    {
                        if (accept is not null && !accept(candidate))
                        {
                            continue;
                        }

                        results.Add(candidate);
                        if (results.Count >= maxResults)
                        {
                            return results;
                        }
                    }
                    else if (probe.DirectoryExists(candidate))
                    {
                        next.Add(candidate);
                    }
                }
            }

            current = next;
            if (current.Count == 0)
            {
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// When two rules claim the same path the stricter risk wins: a cache rule can
    /// never downgrade a user-data lock.
    /// </summary>
    private static void AddOrUpgrade(
        Dictionary<string, RuleMatch> byPath,
        CleanRule rule,
        string path,
        PathMeasure measure,
        bool isDirectory)
    {
        string key = PathKey.Normalize(path);
        var match = new RuleMatch
        {
            Rule = rule,
            AbsolutePath = path,
            AllocatedBytes = measure.AllocatedBytes,
            LogicalBytes = measure.LogicalBytes,
            FileCount = measure.FileCount,
            IsDirectory = isDirectory,
            LastWriteUtc = measure.LastWriteUtc,
        };

        if (!byPath.TryGetValue(key, out RuleMatch? existing))
        {
            byPath[key] = match;
            return;
        }

        int existingRank = Array.IndexOf(SeverityOrder, existing.Rule.Risk);
        int incomingRank = Array.IndexOf(SeverityOrder, rule.Risk);
        if (incomingRank < existingRank)
        {
            byPath[key] = match;
        }
    }
}
