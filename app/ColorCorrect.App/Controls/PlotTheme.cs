using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ColorCorrect.App.Controls;

internal static class PlotTheme
{
    public static Color BrushColor(string key, Color fallback)
    {
        var app = Application.Current;
        if (app is null)
        {
            return fallback;
        }

        if (app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is ISolidColorBrush brush)
        {
            return brush.Color;
        }

        return fallback;
    }

    public static Color Bg => BrushColor("PlotBgBrush", Color.FromRgb(18, 21, 28));
    public static Color Axis => BrushColor("PlotAxisBrush", Color.FromRgb(100, 116, 139));
    public static Color Grid => BrushColor("PlotGridBrush", Color.FromArgb(72, 148, 163, 184));
    public static Color Label => BrushColor("PlotLabelBrush", Color.FromRgb(203, 213, 225));
    public static Color Legend => BrushColor("PlotLegendBrush", Color.FromRgb(238, 241, 246));
    public static Color Ink => BrushColor("PlotInkBrush", Color.FromRgb(11, 13, 17));
    public static Color BeforeValue => BrushColor("PlotBeforeValueBrush", Color.FromRgb(252, 165, 165));
    public static Color AfterValue => BrushColor("PlotAfterValueBrush", Color.FromRgb(134, 239, 172));
    public static Color RenditionBg => BrushColor("RenditionBgBrush", Color.FromRgb(21, 24, 28));

    public static IBrush Solid(Color color) => new SolidColorBrush(color);

    public static void Listen(Control control)
    {
        if (Application.Current is { } app)
        {
            app.ActualThemeVariantChanged += (_, _) => control.InvalidateVisual();
        }
    }

    public static void DrawText(
        DrawingContext context,
        string text,
        double x,
        double y,
        double size,
        Color color,
        TextAlignment align = TextAlignment.Left,
        FontWeight? weight = null)
    {
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Inter, Segoe UI, sans-serif"), FontStyle.Normal, weight ?? FontWeight.Normal),
            size,
            Solid(color));
        var origin = align switch
        {
            TextAlignment.Center => new Point(x - formatted.Width / 2, y),
            TextAlignment.Right => new Point(x - formatted.Width, y),
            _ => new Point(x, y),
        };
        context.DrawText(formatted, origin);
    }
}
