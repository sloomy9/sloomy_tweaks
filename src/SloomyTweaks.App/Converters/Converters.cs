using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SloomyTweaks.Core.Benchmark;
using SloomyTweaks.Core.Optimizations;

namespace SloomyTweaks.App.Converters;

/// <summary>true → Visible. ConverterParameter "invert" reverses it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is true;
        if (parameter is string s && s == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is Visibility.Visible;
}

/// <summary>Non-null, non-empty string or non-empty collection → Visible. "invert" reverses it.</summary>
public sealed class HasValueToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool has = value switch
        {
            null => false,
            string s => s.Length > 0,
            ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter is string p && p == "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class EvidenceToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        EvidenceLevel.Verified => Res("Success"),
        EvidenceLevel.Documented => Res("Accent2"),
        EvidenceLevel.Experimental => Res("Warning"),
        _ => Res("Danger"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    internal static object Res(string key) => Application.Current.Resources[key];
}

public sealed class StateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        "Enabled" => EvidenceToBrushConverter.Res("Success"),
        "Restart pending" => EvidenceToBrushConverter.Res("Warning"),
        "Unsupported" or "Not offered" => EvidenceToBrushConverter.Res("Danger"),
        "Guidance" => EvidenceToBrushConverter.Res("Accent2"),
        _ => EvidenceToBrushConverter.Res("Text.Secondary"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class VerdictToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ComparisonVerdict.Improved => EvidenceToBrushConverter.Res("Success"),
        ComparisonVerdict.Regressed => EvidenceToBrushConverter.Res("Danger"),
        ComparisonVerdict.InsufficientData => EvidenceToBrushConverter.Res("Warning"),
        _ => EvidenceToBrushConverter.Res("Text.Secondary"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>true → Danger brush, false → Success brush (for operation results).</summary>
public sealed class ErrorToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        EvidenceToBrushConverter.Res(value is true ? "Danger" : "Success");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BytesToReadableConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double bytes = value switch { long l => l, ulong u => u, int i => i, double d => d, _ => 0 };
        return bytes >= 1024d * 1024 * 1024 ? $"{bytes / 1024 / 1024 / 1024:0.0} GB" : $"{bytes / 1024 / 1024:0} MB";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BitsPerSecondConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is long bps && bps > 0
        ? bps >= 1_000_000_000 ? $"{bps / 1_000_000_000d:0.#} Gbps" : $"{bps / 1_000_000d:0} Mbps"
        : "Unknown";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullableNumberConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string format = parameter as string ?? "0.#";
        return value is double d && double.IsFinite(d) ? d.ToString(format, culture) : "—";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class JoinLinesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is IEnumerable<string> items ? string.Join(", ", items) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
