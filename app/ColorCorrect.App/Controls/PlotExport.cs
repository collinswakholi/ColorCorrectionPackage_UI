using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using ColorCorrect.Engine;

namespace ColorCorrect.App.Controls;

internal static class PlotExport
{
    public static void Save(string path, CorrectionReport report, PlotKind kind, Control? visual = null)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".svg")
        {
            File.WriteAllText(path, ToSvg(report, kind));
            return;
        }

        if (visual is null)
        {
            throw new InvalidOperationException("A plotted control is required for PNG or JPEG export.");
        }

        var w = Math.Max(8, (int)Math.Ceiling(visual.Bounds.Width));
        var h = Math.Max(8, (int)Math.Ceiling(visual.Bounds.Height));
        if (w < 32 || h < 32)
        {
            w = Math.Max(w, 960);
            h = Math.Max(h, 540);
        }

        var bitmap = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        bitmap.Render(visual);
        if (ext is ".jpg" or ".jpeg")
        {
            bitmap.Save(path, 92);
        }
        else
        {
            bitmap.Save(path);
        }
    }

    public static string WithSuffix(string path, string suffix)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        return Path.Combine(dir, stem + suffix + ext);
    }

    public static string ToSvg(CorrectionReport report, PlotKind kind)
    {
        return kind switch
        {
            PlotKind.Bars => BarsSvg(report, 960, 420),
            PlotKind.StageBars => StageBarsSvg(report, 960, 420),
            PlotKind.Scatter => ScatterSvg(report, 640, 520),
            PlotKind.Residual => ResidualSvg(report, 640, 520),
            PlotKind.RenditionCorrected => RenditionSvg(report, 720, 480, corrected: true),
            _ => RenditionSvg(report, 720, 480, corrected: false),
        };
    }

    private static string BarsSvg(CorrectionReport report, double w, double h)
    {
        var patches = report.Patches;
        var max = Math.Max(1, patches.Select(p => Math.Max(Finite(p.Before), Finite(p.After))).DefaultIfEmpty(1).Max()) * 1.12;
        var map = new Map(w, h, 0, Math.Max(1, patches.Count), 0, max);
        var sb = Start(w, h, "#08090B");
        Axes(sb, map, "Patch", "CIEDE2000");
        var group = Math.Max(2, map.PlotW / Math.Max(1, patches.Count));
        var barW = Math.Max(1, group * 0.36);
        for (var i = 0; i < patches.Count; i++)
        {
            var p = patches[i];
            var x = map.PadL + i * group;
            Rect(sb, x + group * 0.12, map.Sy(Finite(p.Before)), barW, map.Sy(0) - map.Sy(Finite(p.Before)), "#C9A05A");
            Rect(sb, x + group * 0.5, map.Sy(Finite(p.After)), barW, map.Sy(0) - map.Sy(Finite(p.After)), "#3D9B7A");
        }

        Legend(sb, map.PadL + 8, map.PadT + 14, [("#C9A05A", "Before"), ("#3D9B7A", "After")]);
        return End(sb);
    }

    private static string StageBarsSvg(CorrectionReport report, double w, double h)
    {
        var stages = report.EffectiveStages;
        var max = Math.Max(1, stages.Select(s => Math.Max(Finite(s.MeanBefore), Finite(s.MeanAfter))).DefaultIfEmpty(1).Max()) * 1.18;
        var map = new Map(w, h, 0, Math.Max(1, stages.Count), 0, max);
        var sb = Start(w, h, "#08090B");
        Axes(sb, map, "Stage", "Mean ΔE");
        var group = Math.Max(8, map.PlotW / Math.Max(1, stages.Count));
        var barW = Math.Min(36, Math.Max(6, group * 0.28));
        var gap = barW * 0.28;
        for (var i = 0; i < stages.Count; i++)
        {
            var stage = stages[i];
            var cx = map.PadL + (i + 0.5) * group;
            var bx = cx - barW - gap / 2;
            var ax = cx + gap / 2;
            Rect(sb, bx, map.Sy(Finite(stage.MeanBefore)), barW, map.Sy(0) - map.Sy(Finite(stage.MeanBefore)), "#EF4444", 0.85);
            Rect(sb, ax, map.Sy(Finite(stage.MeanAfter)), barW, map.Sy(0) - map.Sy(Finite(stage.MeanAfter)), "#22C55E", 0.85);
            sb.AppendLine($"<text x=\"{F(bx + barW / 2)}\" y=\"{F(map.Sy(Finite(stage.MeanBefore)) - 4)}\" fill=\"#FCA5A5\" font-size=\"9\" text-anchor=\"middle\" font-family=\"Inter, Segoe UI, sans-serif\">{Esc(Finite(stage.MeanBefore).ToString("0.0"))}</text>");
            sb.AppendLine($"<text x=\"{F(ax + barW / 2)}\" y=\"{F(map.Sy(Finite(stage.MeanAfter)) - 4)}\" fill=\"#86EFAC\" font-size=\"9\" text-anchor=\"middle\" font-family=\"Inter, Segoe UI, sans-serif\">{Esc(Finite(stage.MeanAfter).ToString("0.0"))}</text>");
            sb.AppendLine($"<text x=\"{F(cx)}\" y=\"{F(map.PadT + map.PlotH + 16)}\" fill=\"#ECEDEE\" font-size=\"11\" text-anchor=\"middle\" font-weight=\"600\" font-family=\"Inter, Segoe UI, sans-serif\">{Esc(stage.Name)}</text>");
        }

        Legend(sb, map.PadL + 8, map.PadT + 14, [("#EF4444", "Before"), ("#22C55E", "After")]);
        return End(sb);
    }

    private static string ScatterSvg(CorrectionReport report, double w, double h)
    {
        var map = new Map(w, h, 0, 1, 0, 1);
        var sb = Start(w, h, "#08090B");
        Axes(sb, map, "Reference mean RGB", "Measured mean RGB");
        sb.AppendLine($"<line x1=\"{F(map.Sx(0))}\" y1=\"{F(map.Sy(0))}\" x2=\"{F(map.Sx(1))}\" y2=\"{F(map.Sy(1))}\" stroke=\"#9A9CA3\" stroke-dasharray=\"6 4\" stroke-width=\"1.2\"/>");
        foreach (var p in report.Patches)
        {
            if (p.Measured.Length < 3 || p.Reference.Length < 3)
            {
                continue;
            }

            var x = Mean(p.Reference);
            Triangle(sb, map.Sx(x), map.Sy(Mean(p.Measured)), Hex(p.Measured, 0.65));
            if (p.Corrected.Length >= 3)
            {
                sb.AppendLine($"<circle cx=\"{F(map.Sx(x))}\" cy=\"{F(map.Sy(Mean(p.Corrected)))}\" r=\"4.2\" fill=\"{Hex(p.Corrected, 0.85)}\" stroke=\"#08090B\"/>");
            }
        }

        Legend(sb, map.PadL + 8, map.PadT + 14, [("#C9A05A", "Original"), ("#3D9B7A", "Corrected")]);
        return End(sb);
    }

    private static string ResidualSvg(CorrectionReport report, double w, double h)
    {
        var maxAbs = 0.2;
        var points = new List<(double X, double Y, int C, bool Sat)>();
        foreach (var p in report.Patches)
        {
            if (p.Measured.Length < 3 || p.Reference.Length < 3)
            {
                continue;
            }

            var xref = Mean(p.Reference);
            for (var c = 0; c < 3; c++)
            {
                var residual = p.Measured[c] - p.Reference[c];
                maxAbs = Math.Max(maxAbs, Math.Abs(residual));
                points.Add((xref, residual, c, p.Saturated));
            }
        }

        var map = new Map(w, h, 0, 1, -maxAbs, maxAbs);
        var sb = Start(w, h, "#08090B");
        Axes(sb, map, "Reference mean RGB", "ΔRGB");
        sb.AppendLine($"<line x1=\"{F(map.Sx(0))}\" y1=\"{F(map.Sy(0))}\" x2=\"{F(map.Sx(1))}\" y2=\"{F(map.Sy(0))}\" stroke=\"#9A9CA3\" stroke-dasharray=\"6 4\"/>");
        var colors = new[] { "#C45C5C", "#3D9B7A", "#6A9A93" };
        foreach (var point in points)
        {
            var a = point.Sat ? 0.4 : 0.85;
            Rect(sb, map.Sx(point.X) - 2.5, map.Sy(point.Y) - 2.5, 5, 5, colors[point.C], a);
        }

        Legend(sb, map.PadL + 8, map.PadT + 14, [("#C45C5C", "R"), ("#3D9B7A", "G"), ("#6A9A93", "B")]);
        return End(sb);
    }

    private static string RenditionSvg(CorrectionReport report, double w, double h, bool corrected)
    {
        var n = report.Patches.Count;
        var (rows, cols) = n == 24 ? (4, 6) : n == 140 ? (10, 14) : Infer(n);
        const double inset = 16;
        var gap = 8.0;
        var square = Math.Max(2, Math.Min((w - inset * 2 - gap * Math.Max(0, cols - 1)) / cols,
            (h - inset * 2 - gap * Math.Max(0, rows - 1)) / rows));
        var gridW = cols * square + Math.Max(0, cols - 1) * gap;
        var gridH = rows * square + Math.Max(0, rows - 1) * gap;
        var ox = (w - gridW) / 2;
        var oy = (h - gridH) / 2;
        var inner = Math.Max(1, square * 0.55);
        var sb = Start(w, h, "#08090B");
        for (var i = 0; i < n; i++)
        {
            var r = i / cols;
            var c = i % cols;
            var x = ox + c * (square + gap);
            var y = oy + r * (square + gap);
            var p = report.Patches[i];
            Rect(sb, x, y, square, square, Hex(p.Reference, 1));
            var rgb = corrected ? p.Corrected : p.Measured;
            if (rgb.Length >= 3)
            {
                Rect(sb, x + (square - inner) / 2, y + (square - inner) / 2, inner, inner, Hex(rgb, 1));
            }
        }

        return End(sb);
    }

    private static (int Rows, int Cols) Infer(int n)
    {
        var cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(n)));
        return (Math.Max(1, (int)Math.Ceiling(n / (double)cols)), cols);
    }

    private static StringBuilder Start(double w, double h, string bg)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(w)}\" height=\"{F(h)}\" viewBox=\"0 0 {F(w)} {F(h)}\">");
        sb.AppendLine($"<rect width=\"100%\" height=\"100%\" fill=\"{bg}\"/>");
        return sb;
    }

    private static string End(StringBuilder sb)
    {
        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static void Axes(StringBuilder sb, Map map, string xlabel, string ylabel)
    {
        sb.AppendLine($"<line x1=\"{F(map.PadL)}\" y1=\"{F(map.PadT)}\" x2=\"{F(map.PadL)}\" y2=\"{F(map.PadT + map.PlotH)}\" stroke=\"#6E7178\"/>");
        sb.AppendLine($"<line x1=\"{F(map.PadL)}\" y1=\"{F(map.PadT + map.PlotH)}\" x2=\"{F(map.PadL + map.PlotW)}\" y2=\"{F(map.PadT + map.PlotH)}\" stroke=\"#6E7178\"/>");
        sb.AppendLine($"<text x=\"{F(map.PadL + map.PlotW / 2)}\" y=\"{F(map.PadT + map.PlotH + 32)}\" fill=\"#9A9CA3\" font-size=\"11\" text-anchor=\"middle\" font-family=\"Inter, Segoe UI, sans-serif\">{Esc(xlabel)}</text>");
        sb.AppendLine($"<text x=\"14\" y=\"{F(map.PadT + map.PlotH / 2)}\" fill=\"#9A9CA3\" font-size=\"11\" text-anchor=\"middle\" transform=\"rotate(-90 14 {F(map.PadT + map.PlotH / 2)})\" font-family=\"Inter, Segoe UI, sans-serif\">{Esc(ylabel)}</text>");
    }

    private static void Legend(StringBuilder sb, double x, double y, (string Color, string Label)[] items)
    {
        for (var i = 0; i < items.Length; i++)
        {
            Rect(sb, x, y + i * 16 - 8, 8, 8, items[i].Color);
            sb.AppendLine($"<text x=\"{F(x + 14)}\" y=\"{F(y + i * 16)}\" fill=\"#ECEDEE\" font-size=\"11\" font-family=\"Inter, Segoe UI, sans-serif\">{Esc(items[i].Label)}</text>");
        }
    }

    private static void Rect(StringBuilder sb, double x, double y, double w, double h, string color, double alpha = 1)
    {
        if (h < 0)
        {
            y += h;
            h = -h;
        }

        var fill = alpha >= 0.999 ? color : color;
        var extra = alpha >= 0.999 ? "" : $" fill-opacity=\"{F(alpha)}\"";
        sb.AppendLine($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(Math.Max(0, w))}\" height=\"{F(Math.Max(0, h))}\" fill=\"{fill}\"{extra}/>");
    }

    private static void Triangle(StringBuilder sb, double cx, double cy, string color) =>
        sb.AppendLine($"<polygon points=\"{F(cx)},{F(cy - 5)} {F(cx + 5)},{F(cy + 4)} {F(cx - 5)},{F(cy + 4)}\" fill=\"{color}\" stroke=\"#08090B\"/>");

    private static string Hex(double[] rgb, double alpha)
    {
        byte Ch(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
        var a = (byte)Math.Clamp((int)Math.Round(alpha * 255), 0, 255);
        return $"#{a:X2}{Ch(rgb[0]):X2}{Ch(rgb[1]):X2}{Ch(rgb[2]):X2}";
    }

    private static double Finite(double v) => double.IsFinite(v) ? v : 0;
    private static double Mean(double[] rgb) => rgb.Length < 3 ? 0 : (rgb[0] + rgb[1] + rgb[2]) / 3;
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private readonly struct Map
    {
        public Map(double w, double h, double x0, double x1, double y0, double y1)
        {
            PadL = 52;
            PadT = 16;
            PlotW = Math.Max(10, w - 66);
            PlotH = Math.Max(10, h - 58);
            X0 = x0;
            X1 = x1;
            Y0 = y0;
            Y1 = y1;
        }

        public double PadL { get; }
        public double PadT { get; }
        public double PlotW { get; }
        public double PlotH { get; }
        public double X0 { get; }
        public double X1 { get; }
        public double Y0 { get; }
        public double Y1 { get; }
        public double Sx(double x) => PadL + ((x - X0) / (X1 - X0 == 0 ? 1 : X1 - X0)) * PlotW;
        public double Sy(double y) => PadT + (1 - (y - Y0) / (Y1 - Y0 == 0 ? 1 : Y1 - Y0)) * PlotH;
    }
}
