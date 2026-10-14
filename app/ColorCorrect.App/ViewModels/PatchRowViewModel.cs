using Avalonia.Media;
using ColorCorrect.Engine;

namespace ColorCorrect.App.ViewModels;

public sealed class StageDeltaERow
{
    public StageDeltaERow(StageDeltaE stage, string qualityLabel)
    {
        Name = string.IsNullOrWhiteSpace(stage.Name) ? "—" : stage.Name;
        MeanAfter = stage.MeanAfter;
        BeforeText = Format(stage.MeanBefore);
        AfterText = Format(stage.MeanAfter);
        RangeText = $"ΔE {BeforeText} → {AfterText}";
        QualityLabel = qualityLabel;
        var improve = stage.ImprovementPercent;
        if (double.IsFinite(improve) && System.Math.Abs(improve) >= 0.05)
        {
            Improved = improve > 0;
            ImprovementText = Improved ? $"{improve:0.0}% ↓" : $"{System.Math.Abs(improve):0.0}% ↑";
            ImprovementBrush = new SolidColorBrush(Improved ? Color.FromRgb(16, 185, 129) : Color.FromRgb(249, 115, 22));
        }
        else
        {
            Improved = false;
            ImprovementText = "—";
            ImprovementBrush = new SolidColorBrush(Color.FromRgb(154, 156, 163));
        }

        HasElapsed = double.IsFinite(stage.ElapsedSecs);
        ElapsedText = HasElapsed ? $"{stage.ElapsedSecs:0.00}s" : "";
    }

    public string Name { get; }
    public double MeanAfter { get; }
    public string BeforeText { get; }
    public string AfterText { get; }
    public string RangeText { get; }
    public string QualityLabel { get; }
    public string ImprovementText { get; }
    public IBrush ImprovementBrush { get; }
    public bool Improved { get; }
    public bool HasElapsed { get; }
    public string ElapsedText { get; }

    private static string Format(double v) => double.IsFinite(v) ? v.ToString("0.00") : "—";
}

public sealed class PatchRowViewModel
{
    public PatchRowViewModel(PatchRow row)
    {
        Name = string.IsNullOrWhiteSpace(row.Name) ? "—" : row.Name;
        Before = Format(row.Before);
        After = Format(row.After);
        Sat = row.Saturated ? "yes" : "";
        Measured = Brush(row.Measured);
        Reference = Brush(row.Reference);
        Corrected = Brush(row.Corrected);
    }

    public string Name { get; }
    public string Before { get; }
    public string After { get; }
    public string Sat { get; }
    public IBrush Measured { get; }
    public IBrush Reference { get; }
    public IBrush Corrected { get; }

    private static string Format(double v) => double.IsFinite(v) ? v.ToString("0.00") : "—";

    private static IBrush Brush(double[] rgb)
    {
        if (rgb.Length < 3)
        {
            return Brushes.Gray;
        }

        byte Ch(double v) => (byte)System.Math.Clamp((int)System.Math.Round(v * 255), 0, 255);
        return new SolidColorBrush(Color.FromRgb(Ch(rgb[0]), Ch(rgb[1]), Ch(rgb[2])));
    }
}
