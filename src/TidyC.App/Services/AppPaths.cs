using System.IO;

namespace TidyC.App.Services;

/// <summary>
/// Resolves a writable per-user data directory. Some managed/sandboxed
/// environments deny writes outside the app's own folder, so the resolver
/// verifies a real write before trusting a candidate.
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> LazyDataDirectory = new(ResolveDataDirectory, isThreadSafe: true);

    public static string DataDirectory => LazyDataDirectory.Value;

    public static string QuarantineFallback => Path.Combine(DataDirectory, "Quarantine");

    public static string ResolveDataDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "TidyC"));
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "TidyC-Data"));
        candidates.Add(Path.Combine(Path.GetTempPath(), "TidyC"));

        foreach (string candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                string probe = Path.Combine(candidate, ".write-probe");
                File.WriteAllText(probe, DateTime.UtcNow.ToString("O"));
                File.Delete(probe);
                return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Try the next candidate.
            }
        }

        return AppContext.BaseDirectory;
    }

    public static string EnsureSubdirectory(string name)
    {
        string path = Path.Combine(DataDirectory, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
