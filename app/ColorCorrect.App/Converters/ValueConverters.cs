using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;
using ColorCorrect.App.Models;

namespace ColorCorrect.App.Converters;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;
}

public sealed class DeltaEQualityTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d && double.IsFinite(d))
        {
            return d switch
            {
                < 1.0 => "Excellent",
                < 2.0 => "Very good",
                < 3.5 => "Good",
                < 5.0 => "Fair",
                _ => "Poor",
            };
        }

        return "—";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        AvaloniaProperty.UnsetValue;

}

/// <summary>Value-dependent brushes must also re-evaluate when the owning window changes theme.</summary>
public abstract class ThemeAwareBrushConverter : IValueConverter, IMultiValueConverter
{
    public abstract object Convert(object? value, Type targetType, object? parameter, CultureInfo culture);

    // The second binding observes ActualThemeVariant. It deliberately triggers
    // resource resolution even when an immutable log level/metric is unchanged.
    public object Convert(System.Collections.Generic.IList<object?> values, Type targetType,
        object? parameter, CultureInfo culture) =>
        Convert(values.Count > 0 ? values[0] : null, targetType, parameter, culture);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        AvaloniaProperty.UnsetValue;

    /// <summary>
    /// Resolve a theme brush. The fallback (dark-theme value) is used only when the
    /// resource is missing, e.g. in isolated converter tests.
    /// </summary>
    protected static IBrush ThemeBrush(string key, string fallback)
    {
        if (Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true
            && value is IBrush brush)
        {
            return brush;
        }
        return new SolidColorBrush(Color.Parse(fallback));
    }
}

public sealed class ImprovementBrushConverter : ThemeAwareBrushConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double percent && double.IsFinite(percent) && Math.Abs(percent) >= 0.05)
            return percent > 0 ? ThemeBrush("SuccessBrush", "#3D9B7A") : ThemeBrush("WarningBrush", "#C9A05A");
        return ThemeBrush("TextSecondaryBrush", "#9A9CA3");
    }
}

public sealed class DeltaEQualityBrushConverter : ThemeAwareBrushConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d && double.IsFinite(d))
        {
            return d switch
            {
                < 1.0 => ThemeBrush("QualityExcellentBrush", "#3D9B7A"),
                < 2.0 => ThemeBrush("QualityGoodBrush", "#4F9A90"),
                < 3.5 => ThemeBrush("QualityFairBrush", "#C9A05A"),
                < 5.0 => ThemeBrush("QualityWarningBrush", "#D9822B"),
                _ => ThemeBrush("QualityPoorBrush", "#D97070"),
            };
        }

        return ThemeBrush("TextSecondaryBrush", "#9A9CA3");
    }

}

public sealed class DeltaEQualityBgBrushConverter : ThemeAwareBrushConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d && double.IsFinite(d))
        {
            return d switch
            {
                < 1.0 => ThemeBrush("QualityExcellentBgBrush", "#1A34D399"),
                < 2.0 => ThemeBrush("QualityGoodBgBrush", "#143DDC97"),
                < 3.5 => ThemeBrush("QualityFairBgBrush", "#1AE8B84A"),
                < 5.0 => ThemeBrush("QualityWarningBgBrush", "#1AF59E0B"),
                _ => ThemeBrush("QualityPoorBgBrush", "#1AF87171"),
            };
        }

        return Brushes.Transparent;
    }

}

public sealed class LogLevelToBrushConverter : ThemeAwareBrushConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is LogLevel level
            ? level switch
            {
                LogLevel.Debug => ThemeBrush("LogTimeBrush", "#8B8E95"),
                LogLevel.Success => ThemeBrush("SuccessBrush", "#3D9B7A"),
                LogLevel.Warning => ThemeBrush("WarningBrush", "#C9A05A"),
                LogLevel.Error => ThemeBrush("ErrorBrush", "#D97070"),
                _ => ThemeBrush("LogTextBrush", "#D4D5D7"),
            }
            : ThemeBrush("LogTextBrush", "#D4D5D7");
    }


}

public sealed class LogLevelToWeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is LogLevel level
            ? level switch
            {
                LogLevel.Warning => FontWeight.SemiBold,
                LogLevel.Error => FontWeight.Bold,
                LogLevel.Success => FontWeight.Medium,
                _ => FontWeight.Normal,
            }
            : FontWeight.Normal;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        AvaloniaProperty.UnsetValue;
}

public sealed class NumericToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string;
        if (string.IsNullOrWhiteSpace(text))
        {
            return AvaloniaProperty.UnsetValue;
        }

        if (targetType == typeof(int) || targetType == typeof(int?))
        {
            return int.TryParse(text, NumberStyles.Integer, culture, out var i) ? i : AvaloniaProperty.UnsetValue;
        }

        if (targetType == typeof(decimal) || targetType == typeof(decimal?))
        {
            return decimal.TryParse(text, NumberStyles.Float | NumberStyles.AllowExponent, culture, out var m)
                ? m
                : AvaloniaProperty.UnsetValue;
        }

        if (targetType == typeof(double) || targetType == typeof(double?))
        {
            return double.TryParse(text, NumberStyles.Float | NumberStyles.AllowExponent, culture, out var d)
                ? d
                : AvaloniaProperty.UnsetValue;
        }

        return AvaloniaProperty.UnsetValue;
    }
}
