using System.Diagnostics;
using System.Text;
using TidyC.Core.Rules;
using TidyC.Core.Scan;

namespace TidyC.Core.Tests.TestSupport;

/// <summary>Isolated temp directory that cleans itself even after a failed test.</summary>
internal sealed class TempTree : IDisposable
{
    private static readonly Lazy<string> BaseDirectory = new(ResolveBaseDirectory, isThreadSafe: true);

    public TempTree(string label)
    {
        Root = Path.Combine(BaseDirectory.Value, $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>
    /// Some sandboxes lock down %TEMP%; tests therefore live under the repo's
    /// artifacts folder (or TIDYC_TEST_ROOT when set).
    /// </summary>
    private static string ResolveBaseDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("TIDYC_TEST_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Directory.CreateDirectory(configured);
            return configured;
        }

        // The sandbox grants writes only inside the repo workspace, so test
        // artifacts live there; the guard's personal-folder breaker is bypassed
        // per call in the tests that exercise temp paths.
        string? directory = AppContext.BaseDirectory;
        for (int depth = 0; depth < 10 && !string.IsNullOrEmpty(directory); depth++)
        {
            if (System.IO.File.Exists(Path.Combine(directory, "TidyC.sln")))
            {
                string artifacts = Path.Combine(directory, ".test-tmp");
                Directory.CreateDirectory(artifacts);
                return artifacts;
            }

            directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            string artifacts = Path.Combine(localAppData, "TidyC-Tests");
            Directory.CreateDirectory(artifacts);
            return artifacts;
        }

        string fallback = Path.Combine(Path.GetTempPath(), "tidyc-tests");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    public string Dir(params string[] segments)
    {
        string path = segments.Aggregate(Root, Path.Combine);
        Directory.CreateDirectory(path);
        return path;
    }

    public string File(string relativePath, int sizeBytes, byte fill = 0x41)
    {
        string path = At(relativePath);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        System.IO.File.WriteAllBytes(path, Enumerable.Repeat(fill, sizeBytes).ToArray());
        return path;
    }

    public string At(string relativePath) => Path.Combine(Root, relativePath);

    public void Dispose() => TryDelete(Root);

    public static void TryDelete(string path)
    {
        if (!Directory.Exists(path) && !System.IO.File.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                {
                    System.IO.File.SetAttributes(entry, FileAttributes.Normal);
                }

                Directory.Delete(path, recursive: true);
            }
            catch (Exception)
            {
                // Temp leftovers are harmless.
            }
        }
    }
}

internal static class Junctions
{
    /// <summary>
    /// Creates a directory junction with mklink /J. Junctions need no admin rights,
    /// which makes them the realistic TOCTOU attack vector to test against.
    /// </summary>
    public static bool TryCreate(string target, string link)
    {
        string? parent = Path.GetDirectoryName(link);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var startInfo = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(15000);
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>Small AppData-shaped sandbox with token overrides.</summary>
internal sealed class AppDataFixture : IDisposable
{
    public AppDataFixture()
    {
        Tree = new TempTree("appdata");
        Local = Tree.Dir("profile", "AppData", "Local");
        Roaming = Tree.Dir("profile", "AppData", "Roaming");
        LocalLow = Tree.Dir("profile", "AppData", "LocalLow");

        Expander = new PathExpander(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LOCALAPPDATA"] = Local,
            ["APPDATA"] = Roaming,
            ["USERPROFILE"] = Tree.At("profile"),
        });

        Probe = new FileSystemProbe(4096);
    }

    public TempTree Tree { get; }

    public string Local { get; }

    public string Roaming { get; }

    public string LocalLow { get; }

    public PathExpander Expander { get; }

    public FileSystemProbe Probe { get; }

    public string LocalPath(string relative) => Path.Combine(Local, relative);

    public string RoamingPath(string relative) => Path.Combine(Roaming, relative);

    public AppDataAnalyzer CreateAnalyzer(IReadOnlyList<CleanRule> rules) =>
        new(rules, Probe, Expander, () => new DateTime(2025, 5, 20, 9, 41, 22, DateTimeKind.Utc));

    public void Dispose() => Tree.Dispose();
}

internal static class TestRules
{
    public static CleanRule Rule(
        string id,
        RiskLevel risk,
        string[] patterns,
        RuleScope scope = RuleScope.DirectoryTree,
        string appId = "test.app",
        string appName = "Test App") => new()
    {
        Id = id,
        AppName = appName,
        AppId = appId,
        Risk = risk,
        Category = "Test",
        PathPatterns = patterns,
        Purpose = "测试用途：验证四级判定与路径匹配",
        Evidence = "匹配 BuiltInRule: " + id,
        Consequence = "测试后果：仅影响测试沙盒",
        Scope = scope,
    };
}
