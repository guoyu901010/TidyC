using TidyC.Core.Rules;
using TidyC.Core.Tests.TestSupport;

namespace TidyC.Core.Tests;

public sealed class RuleValidatorTests
{
    [Fact]
    public void BuiltInRuleSet_PassesValidation()
    {
        RuleValidationReport report = RuleValidator.Validate();

        Assert.True(report.IsValid, report.ToText());
        Assert.StartsWith("sha256:", report.RulesetSignature);
        Assert.True(report.Rules.Count >= 30, $"规则库过小：{report.Rules.Count}");
        Assert.Contains(
            report.Rules,
            r => r.Risk == RiskLevel.Red && r.PathPatterns.Any(p => p.Contains("Packages", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void RejectsDriveRootPattern()
    {
        CleanRule rule = TestRules.Rule("Bad_Root_01", RiskLevel.Green, new[] { @"%SystemDrive%\*" });

        RuleValidationReport report = RuleValidator.Validate(new[] { rule });

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, i => i.Code is "TooShallow" or "ForbiddenRoot" or "OutsideWhitelist");
    }

    [Fact]
    public void RejectsWindowsSystem32()
    {
        CleanRule rule = TestRules.Rule("Bad_System32_01", RiskLevel.Green, new[] { @"%SystemRoot%\System32\*" });

        RuleValidationReport report = RuleValidator.Validate(new[] { rule });

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, i => i.Code == "ForbiddenRoot");
    }

    [Fact]
    public void RejectsProgramFiles()
    {
        CleanRule rule = TestRules.Rule("Bad_ProgramFiles_01", RiskLevel.Green, new[] { @"%ProgramFiles%\Vendor\Cache" });

        RuleValidationReport report = RuleValidator.Validate(new[] { rule });

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, i => i.Code is "ForbiddenRoot" or "OutsideWhitelist");
    }

    [Fact]
    public void RejectsPersonalDownloadsFolder()
    {
        CleanRule rule = TestRules.Rule("Bad_Downloads_01", RiskLevel.Green, new[] { @"%USERPROFILE%\Downloads\*" });

        RuleValidationReport report = RuleValidator.Validate(new[] { rule });

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, i => i.Code is "ForbiddenRoot" or "OutsideWhitelist");
        Assert.True(RuleValidator.IsForbiddenCleanRoot(@"C:\Users\someone\Downloads"));
    }

    [Fact]
    public void RejectsParentTraversal()
    {
        CleanRule rule = TestRules.Rule("Bad_Traversal_01", RiskLevel.Green, new[] { @"%LOCALAPPDATA%\..\Windows\Temp" });

        RuleValidationReport report = RuleValidator.Validate(new[] { rule });

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, i => i.Code == "ParentTraversal");
    }

    [Fact]
    public void RejectsWholeDirectoryOutsideWindowsOld()
    {
        CleanRule rule = TestRules.Rule(
            "Bad_WholeDir_01",
            RiskLevel.Yellow,
            new[] { @"%LOCALAPPDATA%\Temp" },
            RuleScope.WholeDirectory);

        RuleValidationReport report = RuleValidator.Validate(new[] { rule });

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, i => i.Code == "WholeDirectoryNotAllowed");
    }

    [Fact]
    public void RejectsDuplicateIds()
    {
        CleanRule first = TestRules.Rule("Dup_01", RiskLevel.Green, new[] { @"%SystemRoot%\Temp\*" });
        CleanRule second = TestRules.Rule("Dup_01", RiskLevel.Green, new[] { @"%SystemRoot%\Logs\*" });

        RuleValidationReport report = RuleValidator.Validate(new[] { first, second });

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, i => i.Code == "DuplicateId");
    }

    [Fact]
    public void RejectsMissingNarrative()
    {
        CleanRule rule = TestRules.Rule("Narrative_01", RiskLevel.Green, new[] { @"%SystemRoot%\Temp\*" });
        var broken = new CleanRule
        {
            Id = rule.Id,
            AppName = rule.AppName,
            AppId = rule.AppId,
            Risk = rule.Risk,
            Category = rule.Category,
            PathPatterns = rule.PathPatterns,
            Purpose = string.Empty,
            Evidence = "匹配 BuiltInRule: Narrative_01",
            Consequence = rule.Consequence,
        };

        RuleValidationReport report = RuleValidator.Validate(new[] { broken });

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, i => i.Code == "MissingPurpose");
    }

    [Fact]
    public void AllowsWindowsTempRule()
    {
        CleanRule rule = TestRules.Rule("Good_WinTemp_01", RiskLevel.Green, new[] { @"%SystemRoot%\Temp\*" });

        RuleValidationReport report = RuleValidator.Validate(new[] { rule });

        Assert.True(report.IsValid, report.ToText());
    }

    [Fact]
    public void EnsureValid_ThrowsForBadRuleSet()
    {
        CleanRule rule = TestRules.Rule("Bad_Throw_01", RiskLevel.Green, new[] { @"C:\Windows\System32\*" });

        RuleSetViolationException ex = Assert.Throws<RuleSetViolationException>(
            () => RuleValidator.EnsureValid(new[] { rule }));

        Assert.Contains("ForbiddenRoot", ex.Message);
    }
}
