using System.Collections.Frozen;

namespace TidyC.Core.Rules;

/// <summary>
/// Expands the environment tokens used by the knowledge base. Tests inject an
/// override map so the whole rule pipeline can run against temp folders without
/// touching the real profile.
/// </summary>
public sealed class PathExpander
{
    private static readonly FrozenDictionary<string, string> DefaultTokens = BuildDefaultTokens();

    private readonly FrozenDictionary<string, string> _tokens;

    public PathExpander(IReadOnlyDictionary<string, string>? overrides = null)
    {
        if (overrides is null || overrides.Count == 0)
        {
            _tokens = DefaultTokens;
            return;
        }

        var merged = new Dictionary<string, string>(DefaultTokens, StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in overrides)
        {
            merged[NormalizeToken(pair.Key)] = pair.Value;
        }

        _tokens = merged.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, string> Tokens => _tokens;

    public string Expand(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        string result = template.Trim();

        // Repeat so nested tokens (a token whose value contains another token)
        // resolve, with a hard iteration cap as a runaway guard.
        for (int pass = 0; pass < 4; pass++)
        {
            int start = result.IndexOf('%');
            if (start < 0)
            {
                break;
            }

            bool replaced = false;
            while (start >= 0)
            {
                int end = result.IndexOf('%', start + 1);
                if (end < 0)
                {
                    break;
                }

                string token = result.Substring(start + 1, end - start - 1);
                if (_tokens.TryGetValue(token, out string? value) && !string.IsNullOrEmpty(value))
                {
                    result = string.Concat(result.AsSpan(0, start), value, result.AsSpan(end + 1));
                    replaced = true;
                    start = result.IndexOf('%', start + value.Length);
                }
                else
                {
                    start = result.IndexOf('%', end + 1);
                }
            }

            if (!replaced)
            {
                break;
            }
        }

        return result;
    }

    public static string? GetToken(string rawToken)
    {
        string token = NormalizeToken(rawToken);
        return DefaultTokens.TryGetValue(token, out string? value) ? value : null;
    }

    private static string NormalizeToken(string token) =>
        token.Trim().Trim('%').ToUpperInvariant();

    private static FrozenDictionary<string, string> BuildDefaultTokens()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(string token, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                map[token] = value!;
            }
        }

        Add("LOCALAPPDATA", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Add("APPDATA", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Add("USERPROFILE", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Add("SystemRoot", Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows");
        Add("WINDIR", Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows");
        Add("SystemDrive", Environment.GetEnvironmentVariable("SystemDrive") ?? @"C:");
        Add("ProgramData", Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData");
        Add("ProgramFiles", Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files");
        Add("ProgramFilesX86", Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)");
        Add("TEMP", Path.GetTempPath().TrimEnd('\\'));
        Add("TMP", Path.GetTempPath().TrimEnd('\\'));
        Add("PUBLIC", Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public");
        Add("CommonProgramData", Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData");
        Add("OneDrive", Environment.GetEnvironmentVariable("OneDrive"));
        Add("OneDriveCommercial", Environment.GetEnvironmentVariable("OneDriveCommercial"));

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
