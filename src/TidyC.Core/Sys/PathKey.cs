namespace TidyC.Core.Sys;

/// <summary>
/// Canonical path comparisons. Windows paths are case-insensitive and accept
/// both separators; every comparison in the engine goes through here so a
/// trailing slash or a forward slash can never defeat a safety check.
/// </summary>
public static class PathKey
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        string normalized = path.Trim().Replace('/', '\\');
        if (normalized.Length > 3 && normalized.EndsWith(@"\", StringComparison.Ordinal))
        {
            normalized = normalized.TrimEnd('\\');
        }
        else if (normalized.Length == 2 && normalized[1] == ':')
        {
            normalized += @"\";
        }

        return normalized;
    }

    public static string WithTrailingSeparator(string path)
    {
        string normalized = Normalize(path);
        return normalized.EndsWith(@"\", StringComparison.Ordinal) ? normalized : normalized + @"\";
    }

    public static bool AreEqual(string? left, string? right) =>
        string.Equals(Normalize(left ?? string.Empty), Normalize(right ?? string.Empty), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or strictly below it.</summary>
    public static bool IsUnder(string path, string root)
    {
        string p = Normalize(path);
        string r = WithTrailingSeparator(root);
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase) || AreEqual(p, root);
    }

    public static bool IsStrictlyUnder(string path, string root)
    {
        string p = Normalize(path);
        string r = WithTrailingSeparator(root);
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase) && !AreEqual(p, root);
    }

    public static string? RelativeTo(string path, string root)
    {
        string p = Normalize(path);
        string r = WithTrailingSeparator(root);
        if (AreEqual(p, root))
        {
            return string.Empty;
        }

        if (!p.StartsWith(r, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return p.Substring(r.Length);
    }

    public static string Leaf(string path)
    {
        string normalized = Normalize(path);
        int index = normalized.LastIndexOf('\\');
        return index < 0 ? normalized : normalized.Substring(index + 1);
    }

    public static string Combine(string directory, string leaf)
    {
        string normalized = Normalize(directory);
        return normalized.EndsWith(@"\", StringComparison.Ordinal) ? normalized + leaf : normalized + @"\" + leaf;
    }

    public static bool HasParentTraversal(string path) =>
        path.Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");

    public static string? RootOf(string path)
    {
        string normalized = Normalize(path);
        return normalized.Length >= 3 && normalized[1] == ':'
            ? normalized.Substring(0, 3)
            : null;
    }

    public static bool TryGetVolume(out string root)
    {
        root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        return !string.IsNullOrEmpty(root);
    }
}
