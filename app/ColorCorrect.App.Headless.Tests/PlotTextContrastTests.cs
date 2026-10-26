using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ColorCorrect.App.Controls;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class PlotTextContrastTests
{
    [AvaloniaFact]
    public void RenderedStageValuesUseReadableThemeColorsInBothThemes()
    {
        var previous = Application.Current!.RequestedThemeVariant;
        var plot = new PatchPlotControl
        {
            Kind = PlotKind.StageBars,
            Report = new CorrectionReport { Stages = [new StageDeltaE("CC", 10, 2, 10, 2, 10, 2, 80, 1)] },
        };
        var window = new Window { Width = 500, Height = 320, Content = plot };
        window.Show();
        try
        {
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark, ThemeVariant.Light })
            {
                Application.Current.RequestedThemeVariant = theme;
                Dispatcher.UIThread.RunJobs();
                foreach (var color in new[] { PlotTheme.BeforeValue, PlotTheme.AfterValue })
                    Assert.True(Contrast(color, PlotTheme.Bg) >= 4.5, $"Stage value text is too faint in {theme}.");
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                var evidence = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestResults", "UI"));
                Directory.CreateDirectory(evidence);
                frame.Save(Path.Combine(evidence, $"plot-values-{theme.Key.ToString()!.ToLowerInvariant()}.png"));
                var drawing = new DrawingGroup();
                using (var context = drawing.Open()) plot.Render(context);
                var glyphs = Glyphs(drawing).ToArray();
                // Record the actual renderer's glyph brushes; nine-point LCD
                // antialiasing blends RGB channels independently in raster pixels.
                // The retained PNGs support visual inspection of the same render.
                foreach (var color in new[] { PlotTheme.BeforeValue, PlotTheme.AfterValue })
                    Assert.Contains(glyphs, glyph => glyph.Foreground is ISolidColorBrush brush && brush.Color == color);
            }
        }
        finally
        {
            window.Close();
            Application.Current.RequestedThemeVariant = previous;
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static IEnumerable<GlyphRunDrawing> Glyphs(Drawing drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var entry in Glyphs(child)) yield return entry;
    }

    private static double Contrast(Color a, Color b)
    {
        static double Linear(byte channel) { var v = channel / 255.0; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        static double Light(Color color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        var x = Light(a); var y = Light(b);
        return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
}
