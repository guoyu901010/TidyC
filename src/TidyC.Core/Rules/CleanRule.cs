namespace TidyC.Core.Rules;

public enum RuleScope
{
    /// <summary>Measure and move the whole matched directory tree.</summary>
    DirectoryTree = 0,

    /// <summary>Only the matched files themselves (no recursion below a file).</summary>
    File = 1,

    /// <summary>Atomic whole-directory move. Reserved for <c>C:\Windows.old</c>.</summary>
    WholeDirectory = 2,
}

/// <summary>
/// One reviewed entry of the whitelist knowledge base. A rule is both the
/// detection pattern and the explanation shown to the user: purpose, evidence
/// and consequence are mandatory and validated by <see cref="RuleValidator"/>.
/// </summary>
public sealed class CleanRule
{
    public required string Id { get; init; }

    public required string AppName { get; init; }

    public required string AppId { get; init; }

    /// <summary>
    /// Absolute templates. <c>%LOCALAPPDATA%</c>-style tokens and <c>*</c>
    /// single-segment wildcards are allowed; a trailing <c>\*</c> means
    /// "measure every child separately".
    /// </summary>
    public required string[] PathPatterns { get; init; }

    public required RiskLevel Risk { get; init; }

    public RuleScope Scope { get; init; } = RuleScope.DirectoryTree;

    public required string Category { get; init; }

    public required string Purpose { get; init; }

    public required string Evidence { get; init; }

    public required string Consequence { get; init; }

    /// <summary>Rules that only exist for reporting (DISM / powercfg / VSS targets) are not moveable.</summary>
    public bool IsMoveable { get; init; } = true;

    public bool Enabled { get; init; } = true;

    public override string ToString() => $"{Id} [{Risk}] {string.Join("; ", PathPatterns)}";
}
