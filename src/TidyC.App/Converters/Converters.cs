using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TidyC.Core.Rules;
using TidyC.Core.Sys;

namespace TidyC.App.Converters;

/// <summary>Long bytes → "184.2 GB".</summary>
public sealed class BytesToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long bytes ? ByteSize.Format(bytes) : "—";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class SignedBytesToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long bytes ? ByteSize.FormatSigned(bytes) : "—";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class RatioToPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double ratio ? (ratio * 100).ToString("0.0", culture) + "%" : "—";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class RiskToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is RiskLevel level ? level.ToChineseLabel() : value?.ToString() ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class RiskToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value is RiskLevel level
            ? level switch
            {
                RiskLevel.Green => "Brush.Risk.Safe.Light",
                RiskLevel.Yellow => "Brush.Risk.Caution.Warm",
                RiskLevel.Red => "Brush.Risk.Preserve",
                _ => "Brush.Risk.Unrecognized",
            }
            : "Brush.Text.Secondary";

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class RiskToPillStyleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value is RiskLevel level
            ? level switch
            {
                RiskLevel.Green => "Pill.Green",
                RiskLevel.Yellow => "Pill.Yellow",
                RiskLevel.Red => "Pill.Red",
                _ => "Pill.Gray",
            }
            : "Pill.Neutral";

        return Application.Current?.TryFindResource(key) as Style ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class RiskToSoftBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value is RiskLevel level
            ? level switch
            {
                RiskLevel.Green => "#14107C41",
                RiskLevel.Yellow => "#14FFB900",
                RiskLevel.Red => "#14E81123",
                _ => "#108A8886",
            }
            : "#108A8886";

        return (Color)ColorConverter.ConvertFromString(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Category string (WindowsSystem / AppData / …) → spectrum brush.</summary>
public sealed class CategoryToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = (value as string) switch
        {
            "WindowsSystem" => "Brush.Spectrum.Windows",
            "AppData" => "Brush.Spectrum.AppData",
            "UserProfiles" => "Brush.Spectrum.UserProfiles",
            "ProgramFiles" => "Brush.Spectrum.ProgramFiles",
            "ProtectedSystem" => "Brush.Spectrum.ProtectedSystem",
            "Quarantine" => "Brush.Spectrum.Quarantine",
            _ => "Brush.Spectrum.Other",
        };

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        bool invert = parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase);
        if (invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility visibility && visibility == Visibility.Visible;
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool hasItems = value switch
        {
            int count => count > 0,
            long count => count > 0,
            System.Collections.ICollection collection => collection.Count > 0,
            _ => false,
        };

        return hasItems ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    // Placeholder semantics: visible while the input is empty, hidden afterwards.
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool flag && !flag;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool flag && !flag;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool flag && flag ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool invert = parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase);
        bool isNull = value is null;
        return (isNull ^ invert) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class ProgressToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double percent || double.IsNaN(percent))
        {
            return 0d;
        }

        double max = 1d;
        if (parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            max = parsed;
        }

        return Math.Clamp(percent, 0, max) / max;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
