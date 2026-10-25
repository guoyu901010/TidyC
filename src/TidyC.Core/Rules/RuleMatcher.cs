using System.Text.RegularExpressions;
using TidyC.Core.Sys;

namespace TidyC.Core.Rules;

/// <summary>A knowledge-base rule with its pattern already expanded and compiled.</summary>
public sealed class CompiledRule
{
    internal CompiledRule(CleanRule rule, string rawPattern, string expandedPattern, Regex regex, bool enumeratesChildren)
    {
        Rule = rule;
        RawPattern = rawPattern;
        ExpandedPattern = PathKey.Normalize(expandedPattern);
        Regex = regex;
        EnumeratesChildren = enumeratesChildren;
    }

    public CleanRule Rule { get; }

    public string RawPattern { get; }

    public string ExpandedPattern { get; }

    public Regex Regex { get; }

    /// <summary>Pattern ends with <c>\*</c>: each child is measured as its own item.</summary>
    public bool EnumeratesChildren { get; }

    public bool IsMatch(string path) => Regex.IsMatch(PathKey.Normalize(path));

    public override string ToString() => $"{Rule.Id}: {ExpandedPattern}";
}

public static class RuleMatcher
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    public static Regex BuildRegex(string expandedPattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expandedPattern);

        var builder = new System.Text.StringBuilder(expandedPattern.Length * 2);
        builder.Append('^');

        for (int i = 0; i < expandedPattern.Length; i++)
        {
            char c = expandedPattern[i];
            if (c == '*')
            {
                bool recursive = i + 1 < expandedPattern.Length && expandedPattern[i + 1] == '*';
                if (recursive)
                {
                    builder.Append(".*");
                    i++;
                }
                else
                {
                    builder.Append(@"[^\\]*");
                }
            }
            else
            {
                builder.Append(Regex.Escape(c.ToString()));
            }
        }

        builder.Append('$');
        return new Regex(
            builder.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
            MatchTimeout);
    }

    public static IReadOnlyList<CompiledRule> CompileAll(IEnumerable<CleanRule> rules, PathExpander expander)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(expander);

        var compiled = new List<CompiledRule>();
        foreach (CleanRule rule in rules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            foreach (string raw in rule.PathPatterns)
            {
                string expanded = expander.Expand(raw);
                if (string.IsNullOrWhiteSpace(expanded))
                {
                    continue;
                }

                bool children = expanded.EndsWith(@"\*", StringComparison.Ordinal);
                compiled.Add(new CompiledRule(rule, raw, expanded, BuildRegex(expanded), children));
            }
        }

        return compiled;
    }

    /// <summary>Compiles one rule's patterns, used by unit tests and the validator.</summary>
    public static IReadOnlyList<CompiledRule> CompileRule(CleanRule rule, PathExpander expander)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var one = new List<CleanRule> { rule };
        return CompileAll(one, expander);
    }

    public static bool ContainsWildcard(string pattern) => pattern.IndexOf('*') >= 0;

    /// <summary>
    /// The static prefix that must exist before the first wildcard, used to
    /// prove a rule can never point at the drive root or a sensitive folder.
    /// </summary>
    public static string StaticPrefix(string expandedPattern)
    {
        int wildcard = expandedPattern.IndexOf('*');
        if (wildcard < 0)
        {
            return expandedPattern;
        }

        string prefix = expandedPattern.Substring(0, wildcard);
        int lastSeparator = prefix.LastIndexOf('\\');
        return lastSeparator <= 0 ? prefix.TrimEnd('\\') : prefix.Substring(0, lastSeparator);
    }
}
