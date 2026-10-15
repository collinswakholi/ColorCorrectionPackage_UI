using System.Linq;
using Avalonia.Media;
using ColorCorrect.App.Controls;
using ColorCorrect.Engine;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ColorCorrect.App.ViewModels;

public partial class ColorHoverState : ObservableObject
{
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _context = "";
    [ObservableProperty] private string _r = "";
    [ObservableProperty] private string _g = "";
    [ObservableProperty] private string _b = "";
    [ObservableProperty] private string _hex = "";
    [ObservableProperty] private string _l = "";
    [ObservableProperty] private string _a = "";
    [ObservableProperty] private string _labB = "";
    [ObservableProperty] private string _extra = "";
    [ObservableProperty] private IBrush? _swatch;
    [ObservableProperty] private IBrush? _swatch2;
    [ObservableProperty] private string _swatch2Label = "";
    [ObservableProperty] private bool _isCompare;
    [ObservableProperty] private string _sampleLabel = "";
    [ObservableProperty] private string _refR = "";
    [ObservableProperty] private string _refG = "";
    [ObservableProperty] private string _refB = "";
    [ObservableProperty] private string _refHex = "";
    [ObservableProperty] private string _refL = "";
    [ObservableProperty] private string _refA = "";
    [ObservableProperty] private string _refLabB = "";
    [ObservableProperty] private string _deltaR = "";
    [ObservableProperty] private string _deltaG = "";
    [ObservableProperty] private string _deltaB = "";
    [ObservableProperty] private string _deltaL = "";
    [ObservableProperty] private string _deltaA = "";
    [ObservableProperty] private string _deltaLabB = "";
    [ObservableProperty] private double _left;
    [ObservableProperty] private double _top;

    public bool HasExtra => !string.IsNullOrWhiteSpace(Extra);
    public bool HasSwatch2 => Swatch2 is not null;
    public string CopyLine => IsCompare
        ? string.Join("  ", new[]
        {
            Title, SampleLabel, $"RGB {R}, {G}, {B}", Hex, $"Lab {L}, {A}, {LabB}",
            "Reference", $"RGB {RefR}, {RefG}, {RefB}", RefHex, $"Lab {RefL}, {RefA}, {RefLabB}",
            $"Δ RGB {DeltaR}, {DeltaG}, {DeltaB}", $"Δ Lab {DeltaL}, {DeltaA}, {DeltaLabB}", Extra,
        }.Where(part => !string.IsNullOrWhiteSpace(part)))
        : string.Join("  ", new[] { Title, Context, $"RGB {R}, {G}, {B}", Hex, $"Lab {L}, {A}, {LabB}", Extra }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    public void Hide()
    {
        IsVisible = false;
    }

    public void ShowPixel(PickedColor picked)
    {
        Title = picked.Pixel;
        Context = "Pixel";
        IsCompare = false;
        SampleLabel = "";
        ApplyChannels(picked.R, picked.G, picked.B, picked.L, picked.A, picked.LabB);
        ClearDeltas();
        Extra = "";
        Swatch2 = null;
        Swatch2Label = "";
        IsVisible = true;
        OnPropertyChanged(nameof(HasExtra));
        OnPropertyChanged(nameof(HasSwatch2));
    }

    public void ShowPatch(PatchRow row, bool corrected)
    {
        var sample = corrected && row.Corrected.Length >= 3 ? row.Corrected : row.Measured;
        var (r, g, b, l, a, labB) = FromSrgb(sample);
        var (rr, rg, rb, rl, ra, rLabB) = FromSrgb(row.Reference);
        Title = string.IsNullOrWhiteSpace(row.Name) ? "Patch" : row.Name;
        Context = corrected ? "Corrected" : "Original";
        SampleLabel = Context;
        IsCompare = true;
        ApplyChannels(r, g, b, l, a, labB);
        RefR = rr.ToString();
        RefG = rg.ToString();
        RefB = rb.ToString();
        RefHex = $"#{rr:x2}{rg:x2}{rb:x2}";
        RefL = rl.ToString("0.00");
        RefA = ra.ToString("0.00");
        RefLabB = rLabB.ToString("0.00");
        DeltaR = Signed(r - rr);
        DeltaG = Signed(g - rg);
        DeltaB = Signed(b - rb);
        DeltaL = Signed(l - rl);
        DeltaA = Signed(a - ra);
        DeltaLabB = Signed(labB - rLabB);
        var before = double.IsFinite(row.Before) ? row.Before.ToString("0.00") : "—";
        var after = double.IsFinite(row.After) ? row.After.ToString("0.00") : "—";
        Extra = row.Saturated ? $"ΔE {before} → {after}  sat" : $"ΔE {before} → {after}";
        Swatch2 = new SolidColorBrush(Color.FromRgb(rr, rg, rb));
        Swatch2Label = "Reference";
        IsVisible = true;
        OnPropertyChanged(nameof(HasExtra));
        OnPropertyChanged(nameof(HasSwatch2));
    }

    public void Place(double pointerX, double pointerY, double hostW, double hostH, double cardW, double cardH)
    {
        const double gap = 14;
        var left = pointerX + gap;
        var top = pointerY - cardH - 8;
        if (left + cardW > hostW - 8)
        {
            left = pointerX - cardW - gap;
        }

        if (top < 8)
        {
            top = pointerY + gap;
        }

        if (top + cardH > hostH - 8)
        {
            top = Math.Max(8, hostH - cardH - 8);
        }

        Left = Math.Clamp(left, 8, Math.Max(8, hostW - cardW - 8));
        Top = Math.Clamp(top, 8, Math.Max(8, hostH - cardH - 8));
    }

    private void ApplyChannels(byte r, byte g, byte b, double l, double a, double labB)
    {
        R = r.ToString();
        G = g.ToString();
        B = b.ToString();
        Hex = $"#{r:x2}{g:x2}{b:x2}";
        L = l.ToString("0.00");
        A = a.ToString("0.00");
        LabB = labB.ToString("0.00");
        Swatch = new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    private void ClearDeltas()
    {
        DeltaR = DeltaG = DeltaB = "";
        DeltaL = DeltaA = DeltaLabB = "";
    }

    private static string Signed(int value) => value > 0 ? $"+{value}" : value.ToString();

    private static string Signed(double value)
    {
        if (Math.Abs(value) < 0.005)
        {
            return "0.00";
        }

        return value > 0 ? $"+{value:0.00}" : value.ToString("0.00");
    }

    private static (byte R, byte G, byte B, double L, double A, double LabB) FromSrgb(double[] rgb)
    {
        byte Ch(int i)
        {
            if (rgb.Length <= i)
            {
                return 0;
            }

            return (byte)Math.Clamp((int)Math.Round(rgb[i] * 255), 0, 255);
        }

        var r = Ch(0);
        var g = Ch(1);
        var b = Ch(2);
        var lab = PixelSampler.SrgbToLab(r, g, b);
        return (r, g, b, lab.L, lab.A, lab.B);
    }

    private static IBrush BrushFrom(double[] rgb)
    {
        if (rgb.Length < 3)
        {
            return Brushes.Gray;
        }

        byte Ch(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
        return new SolidColorBrush(Color.FromRgb(Ch(rgb[0]), Ch(rgb[1]), Ch(rgb[2])));
    }
}
