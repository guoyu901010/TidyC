using System.Security.Cryptography;
using System.Text;
using TidyC.Core.Sys;

namespace TidyC.Core.Rules;

public sealed class RuleValidationIssue
{
    public RuleValidationIssue(string ruleId, string code, string message)
    {
        RuleId = ruleId;
        Code = code;
        Message = message;
    }

    public string RuleId { get; }

    public string Code { get; }

    public string Message { get; }

    public override string ToString() => $"[{Code}] {RuleId}: {Message}";
}

public sealed class RuleValidationReport
{
    public List<RuleValidationIssue> Issues { get; } = new();

    public IReadOnlyList<CleanRule> Rules { get; internal set; } = Array.Empty<CleanRule>();

    public string RulesetSignature { get; internal set; } = string.Empty;

    public bool IsValid => Issues.Count == 0;

    public string ToText() =>
        IsValid
            ? $"规则集校验通过：{Rules.Count} 条规则，签名 {RulesetSignature}"
            : $"规则集校验失败（{Issues.Count} 项）：{Environment.NewLine}" +
              string.Join(Environment.NewLine, Issues.Select(i => "  - " + i));
}

public sealed class RuleSetViolationException : Exception
{
    public RuleSetViolationException(RuleValidationReport report)
        : base(report.ToText())
    {
        Report = report;
    }

    public RuleValidationReport Report { get; }
}

/// <summary>
/// Whistle-blower for the knowledge base. A rule that tries to reach the drive
/// root, a Windows component folder, Program Files, a personal folder or the
/// quarantine itself is rejected no matter who wrote it; a single invalid rule
/// blocks the whole rule set (fail closed at startup).
/// </summary>
public static class RuleValidator
{
    private static readonly PathExpander DefaultExpander = new();

    /// <summary>White-list of ancestors any rule target must live under.</summary>
    public static readonly string[] AllowedRootTemplates =
    {
        @"%LOCALAPPDATA%",
        @"%APPDATA%",
        @"%USERPROFILE%\AppData\LocalLow",
        @"%SystemRoot%\Temp",
        @"%SystemRoot%\Logs",
        @"%SystemRoot%\Minidump",
        @"%SystemRoot%\Prefetch",
        @"%SystemRoot%\SoftwareDistribution\Download",
        @"%SystemRoot%\SystemTemp",
        @"%SystemRoot%\LiveKernelReports",
        @"%SystemRoot%\Panther",
        @"%SystemRoot%\ServiceProfiles\LocalService\AppData\Local\FontCache",
        @"%ProgramData%\Microsoft\Windows\DeliveryOptimization",
        @"%ProgramData%\Microsoft\Windows\WER",
        @"%ProgramData%\Microsoft\Network\Downloader",
        @"%ProgramData%\Package Cache",
        @"%USERPROFILE%\.gradle\caches",
        @"%USERPROFILE%\.m2\repository",
        @"%USERPROFILE%\.nuget\packages",
        @"%USERPROFILE%\.cache",
        @"%USERPROFILE%\.vscode\extensions",
        @"%SystemDrive%\Windows.old",
    };

    /// <summary>Hard circuit breakers. Subtree entries cannot be aimed at even with wildcards.</summary>
    public static readonly (string Template, bool WholeSubtree)[] ProtectedTemplates =
    {
        (@"%SystemRoot%", false),
        (@"%SystemRoot%\System32", true),
        (@"%SystemRoot%\SysWOW64", true),
        (@"%SystemRoot%\WinSxS", true),
        (@"%SystemRoot%\Fonts", true),
        (@"%SystemRoot%\assembly", true),
        (@"%SystemRoot%\Boot", true),
        (@"%SystemRoot%\servicing", true),
        (@"%SystemRoot%\CSC", true),
        (@"%SystemDrive%\Program Files", true),
        (@"%SystemDrive%\Program Files (x86)", true),
        (@"%SystemDrive%\Users", false),
        (@"%USERPROFILE%", false),
        (@"%USERPROFILE%\AppData", false),
        (@"%USERPROFILE%\Desktop", true),
        (@"%USERPROFILE%\Documents", true),
        (@"%USERPROFILE%\Downloads", true),
        (@"%USERPROFILE%\Pictures", true),
        (@"%USERPROFILE%\Music", true),
        (@"%USERPROFILE%\Videos", true),
        (@"%USERPROFILE%\OneDrive", true),
        (@"%USERPROFILE%\Favorites", true),
        (@"%USERPROFILE%\Links", true),
        (@"%USERPROFILE%\Contacts", true),
        (@"%USERPROFILE%\Saved Games", true),
        (@"%USERPROFILE%\Searches", true),
        (@"%SystemDrive%\System Volume Information", true),
        (@"%SystemDrive%\$Recycle.Bin", true),
        (@"%SystemDrive%\Recovery", true),
        (@"%SystemDrive%\Boot", true),
        (@"%SystemDrive%\EFI", true),
        (@"%ProgramData%", false),
        (@"%ProgramData%\Microsoft\Crypto", true),
        (@"%ProgramData%\Microsoft\Protect", true),
        (@"%SystemDrive%\TidyC_Quarantine", true),
    };

    private static readonly string[] ProtectedFileNames =
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "bootmgr", "ntldr", "boot.ini",
    };

    public static bool IsForbiddenCleanRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        string expanded = DefaultExpander.Expand(path);
        if (PathKey.HasParentTraversal(expanded) || expanded.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        string normalized = PathKey.Normalize(expanded);
        if (normalized.Length == 3 && normalized[1] == ':' && normalized[2] == '\\')
        {
            return true;
        }

        string leaf = PathKey.Leaf(normalized);
        foreach (string protectedFile in ProtectedFileNames)
        {
            if (string.Equals(leaf, protectedFile, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // A wildcard path is judged by its static prefix so "…\Downloads\*"
        // cannot slip past the personal-folder breaker.
        string candidate = RuleMatcher.ContainsWildcard(normalized)
            ? RuleMatcher.StaticPrefix(normalized)
            : normalized;

        if (HitsPersonalFolderOfAnyProfile(candidate))
        {
            return true;
        }

        foreach ((string template, bool wholeSubtree) in ProtectedTemplates)
        {
            string protectedPath = DefaultExpander.Expand(template);
            if (wholeSubtree ? PathKey.IsUnder(candidate, protectedPath) : PathKey.AreEqual(candidate, protectedPath))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Protects the personal folders of every profile on the box, not just the
    /// signed-in one: <c>C:\Users\*\Downloads</c> is never a legal clean root.
    /// </summary>
    private static bool HitsPersonalFolderOfAnyProfile(string candidate)
    {
        string[] segments = candidate.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 2 < segments.Length; i++)
        {
            if (!segments[i].Equals("Users", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsPersonalFolderName(segments[i + 2]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPersonalFolderName(string name)
    {
        foreach (string personal in PersonalFolderNames)
        {
            if (name.Equals(personal, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(personal + " - ", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] PersonalFolderNames =
    {
        "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos", "OneDrive",
        "Favorites", "Links", "Contacts", "Saved Games", "Searches", "3D Objects",
    };

    public static bool IsInsideWhitelist(string path)
    {
        string expanded = DefaultExpander.Expand(path);
        if (PathKey.HasParentTraversal(expanded))
        {
            return false;
        }

        string candidate = RuleMatcher.ContainsWildcard(expanded)
            ? RuleMatcher.StaticPrefix(PathKey.Normalize(expanded))
            : PathKey.Normalize(expanded);

        foreach (string template in AllowedRootTemplates)
        {
            string allowed = DefaultExpander.Expand(template);
            if (PathKey.IsUnder(candidate, allowed))
            {
                return true;
            }
        }

        return false;
    }

    public static RuleValidationReport Validate(IReadOnlyList<CleanRule>? rules = null, PathExpander? expander = null)
    {
        rules ??= BuiltInRules.All;
        expander ??= DefaultExpander;

        var report = new RuleValidationReport { Rules = rules };
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var riskByPattern = new Dictionary<string, (RiskLevel Risk, string RuleId)>(StringComparer.OrdinalIgnoreCase);

        foreach (CleanRule rule in rules)
        {
            void Fail(string code, string message) =>
                report.Issues.Add(new RuleValidationIssue(rule.Id ?? "<null>", code, message));

            if (string.IsNullOrWhiteSpace(rule.Id) || !IsValidId(rule.Id))
            {
                Fail("InvalidId", "规则 Id 必须为 3-64 位字母、数字、下划线或点");
            }
            else if (!ids.Add(rule.Id))
            {
                Fail("DuplicateId", "规则 Id 重复，规则库必须一一对应");
            }

            if (string.IsNullOrWhiteSpace(rule.AppName) || string.IsNullOrWhiteSpace(rule.AppId))
            {
                Fail("MissingApplication", "规则必须声明应用名称与 app_id");
            }

            if (string.IsNullOrWhiteSpace(rule.Category))
            {
                Fail("MissingCategory", "规则必须归类（BrowserCache / UserData / …）");
            }

            if (!rule.Purpose.Trim().Equals(rule.Purpose, StringComparison.Ordinal) || rule.Purpose.Length < 6)
            {
                Fail("MissingPurpose", "用途说明缺失：清理清单必须向用户解释这块空间是什么");
            }

            if (rule.Evidence.Length < 6 || !rule.Evidence.Contains("BuiltInRule", StringComparison.OrdinalIgnoreCase))
            {
                Fail("MissingEvidence", "证据链缺失：必须指明命中的知识库条目");
            }

            if (rule.Consequence.Length < 6)
            {
                Fail("MissingConsequence", "后果说明缺失：必须描述清理后的真实影响");
            }

            if (rule.PathPatterns is null || rule.PathPatterns.Length == 0)
            {
                Fail("MissingPattern", "规则未包含任何路径模板");
                continue;
            }

            if (rule.Scope == RuleScope.WholeDirectory)
            {
                string expected = PathKey.Normalize(expander.Expand(@"%SystemDrive%\Windows.old"));
                foreach (string pattern in rule.PathPatterns)
                {
                    string expanded = PathKey.Normalize(expander.Expand(pattern));
                    if (!PathKey.AreEqual(expanded, expected))
                    {
                        Fail("WholeDirectoryNotAllowed", "整目录原子搬移仅允许 C:\\Windows.old");
                    }

                    if (!rule.Risk.IsSelectable())
                    {
                        Fail("WholeDirectoryRisk", "整目录原子搬移只允许绿/黄风险等级");
                    }
                }
            }

            foreach (string pattern in rule.PathPatterns)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    Fail("EmptyPattern", "路径模板为空");
                    continue;
                }

                if (PathKey.HasParentTraversal(pattern))
                {
                    Fail("ParentTraversal", $"路径模板包含 .. 上跳：{pattern}");
                    continue;
                }

                string expanded = PathKey.Normalize(expander.Expand(pattern));
                if (expanded.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    Fail("UncPath", $"拒绝 UNC / 设备路径：{pattern}");
                    continue;
                }

                if (expanded.Length < 3 || expanded[1] != ':' || expanded[2] != '\\')
                {
                    Fail("NotAbsolute", $"路径模板展开后不是绝对路径：{pattern} → {expanded}");
                    continue;
                }

                string staticPrefix = RuleMatcher.StaticPrefix(expanded);
                string[] segments = staticPrefix.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                int minimumSegments = rule.Scope == RuleScope.WholeDirectory ? 2 : 3;
                if (segments.Length < minimumSegments)
                {
                    Fail("TooShallow", $"路径模板过于宽泛（静态前缀不足 {minimumSegments} 段）：{expanded}");
                }

                if (IsForbiddenCleanRoot(expanded) || IsForbiddenCleanRoot(staticPrefix))
                {
                    Fail("ForbiddenRoot", $"模板命中硬编码禁清区（C:\\、Windows 本体、Program Files、个人目录等）：{pattern}");
                }

                if (!IsInsideWhitelist(expanded))
                {
                    Fail("OutsideWhitelist", $"模板不在白名单根目录内，默认拒绝：{pattern}");
                }

                string riskKey = expanded.EndsWith(@"\*", StringComparison.Ordinal)
                    ? expanded.Substring(0, expanded.Length - 2)
                    : expanded;

                riskKey = riskKey.ToUpperInvariant();
                if (riskByPattern.TryGetValue(riskKey, out (RiskLevel Risk, string RuleId) existing))
                {
                    if (existing.Risk != rule.Risk)
                    {
                        Fail("RiskConflict",
                            $"同一目标被赋予不同风险等级（{existing.Risk} @ {existing.RuleId} vs {rule.Risk}）：{pattern}");
                    }
                }
                else
                {
                    riskByPattern[riskKey] = (rule.Risk, rule.Id);
                }
            }
        }

        report.RulesetSignature = ComputeSignature(rules);
        return report;
    }

    /// <summary>Startup fuse: never run the engine against an unvalidated rule set.</summary>
    public static RuleValidationReport EnsureValid(IReadOnlyList<CleanRule>? rules = null, PathExpander? expander = null)
    {
        RuleValidationReport report = Validate(rules, expander);
        if (!report.IsValid)
        {
            throw new RuleSetViolationException(report);
        }

        return report;
    }

    public static string ComputeSignature(IEnumerable<CleanRule> rules)
    {
        var builder = new StringBuilder();
        foreach (CleanRule rule in rules.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(rule.Id).Append('|')
                .Append(rule.Risk.ToContractString()).Append('|')
                .Append(rule.Scope).Append('|')
                .Append(string.Join(';', rule.PathPatterns))
                .Append('\n');
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsValidId(string id)
    {
        if (id.Length is < 3 or > 64)
        {
            return false;
        }

        foreach (char c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not '_' and not '.' and not '-')
            {
                return false;
            }
        }

        return true;
    }
}
