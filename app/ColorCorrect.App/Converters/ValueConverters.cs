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

public sealed class DeltaEQualityBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d && double.IsFinite(d))
        {
            return d switch
            {
                < 1.0 => ThemeBrush("QualityExcellentBrush", "#10B981"),
                < 2.0 => ThemeBrush("QualityGoodBrush", "#34D399"),
                < 3.5 => ThemeBrush("QualityFairBrush", "#F59E0B"),
                < 5.0 => ThemeBrush("QualityWarningBrush", "#F97316"),
                _ => ThemeBrush("QualityPoorBrush", "#EF4444"),
            };
        }

        return ThemeBrush("TextSecondaryBrush", "#94A3B8");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        AvaloniaProperty.UnsetValue;

    private static IBrush ThemeBrush(string key, string fallback)
    {
        if (Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true
            && value is IBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Color.Parse(fallback));
    }
}

public sealed class DeltaEQualityBgBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d && double.IsFinite(d))
        {
            return d switch
            {
                < 1.0 => ThemeBrush("QualityExcellentBgBrush", "#1A10B981"),
                < 2.0 => ThemeBrush("QualityGoodBgBrush", "#1434D399"),
                < 3.5 => ThemeBrush("QualityFairBgBrush", "#1AF59E0B"),
                < 5.0 => ThemeBrush("QualityWarningBgBrush", "#1AF97316"),
                _ => ThemeBrush("QualityPoorBgBrush", "#1AEF4444"),
            };
        }

        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        AvaloniaProperty.UnsetValue;

    private static IBrush ThemeBrush(string key, string fallback)
    {
        if (Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true
            && value is IBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Color.Parse(fallback));
    }
}

public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is LogLevel level
            ? level switch
            {
                LogLevel.Debug => ThemeBrush("LogTimeBrush", "#8EA0B8"),
                LogLevel.Success => ThemeBrush("SuccessBrush", "#10B981"),
                LogLevel.Warning => ThemeBrush("WarningBrush", "#F59E0B"),
                LogLevel.Error => ThemeBrush("ErrorBrush", "#EF4444"),
                _ => ThemeBrush("LogTextBrush", "#CBD5E1"),
            }
            : ThemeBrush("LogTextBrush", "#CBD5E1");
    }

    private static IBrush ThemeBrush(string key, string fallback)
    {
        if (Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true
            && value is IBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Color.Parse(fallback));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        AvaloniaProperty.UnsetValue;
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
