using System.Globalization;

namespace TidyC.Core.Sys;

/// <summary>Human readable disk sizes. Binary units, one decimal below 100, invariant formatting.</summary>
public static class ByteSize
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return "-" + Format(-bytes);
        }

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string text = unit == 0
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value >= 100
                ? value.ToString("0", CultureInfo.InvariantCulture)
                : value.ToString("0.0", CultureInfo.InvariantCulture);

        return text + " " + Units[unit];
    }

    public static string FormatSigned(long bytes)
    {
        string sign = bytes > 0 ? "+" : bytes < 0 ? "-" : "±";
        return sign + Format(Math.Abs(bytes));
    }

    public static string FormatCompact(long bytes) => Format(bytes).Replace(" ", string.Empty, StringComparison.Ordinal);

    public static double ToGigabytes(long bytes) => bytes / (1024d * 1024d * 1024d);
}
