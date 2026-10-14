using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ColorCorrect.Engine;

namespace ColorCorrect.App.Controls;

public enum PlotKind
{
    Bars,
    StageBars,
    Scatter,
    Residual,
    RenditionOriginal,
    RenditionCorrected,
}

public sealed class PlotHoverEventArgs : EventArgs
{
    public PlotHoverEventArgs(int index, PatchRow? patch, Point local, bool corrected)
    {
        Index = index;
        Patch = patch;
        Local = local;
        Corrected = corrected;
    }

    public int Index { get; }
    public PatchRow? Patch { get; }
    public Point Local { get; }
    public bool Corrected { get; }
}

public sealed class PatchPlotControl : Control
{
    public static readonly StyledProperty<CorrectionReport?> ReportProperty =
        AvaloniaProperty.Register<PatchPlotControl, CorrectionReport?>(nameof(Report));

    public static readonly StyledProperty<PlotKind> KindProperty =
        AvaloniaProperty.Register<PatchPlotControl, PlotKind>(nameof(Kind));

    private int _hoverIndex = -1;

    static PatchPlotControl()
    {
        AffectsRender<PatchPlotControl>(ReportProperty, KindProperty);
    }

    public PatchPlotControl()
    {
        ClipToBounds = true;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        PlotTheme.Listen(this);
    }

    public event EventHandler<PlotHoverEventArgs>? PlotHoverChanged;

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width) : 0;
        var h = double.IsFinite(availableSize.Height) ? Math.Max(0, availableSize.Height) : 160;
        return new Size(w, h);
    }

    public CorrectionReport? Report
    {
        get => GetValue(ReportProperty);
        set => SetValue(ReportProperty, value);
    }

    public PlotKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty)
        {
            Cursor = IsRendition ? new Cursor(StandardCursorType.Cross) : Cursor.Default;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsRendition)
        {
            return;
        }

        UpdateHover(e.GetPosition(this));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverIndex < 0)
        {
            return;
        }

        _hoverIndex = -1;
        InvalidateVisual();
        PlotHoverChanged?.Invoke(this, new PlotHoverEventArgs(-1, null, default, false));
    }

    private bool IsRendition => Kind is PlotKind.RenditionOriginal or PlotKind.RenditionCorrected;

    private void UpdateHover(Point local)
    {
        var index = HitRendition(local);
        if (index == _hoverIndex && index < 0)
        {
            return;
        }

        var changed = index != _hoverIndex;
        _hoverIndex = index;
        if (changed)
        {
            InvalidateVisual();
        }

        var report = Report;
        var patch = report is not null && index >= 0 && index < report.Patches.Count
            ? report.Patches[index]
            : null;
        PlotHoverChanged?.Invoke(this, new PlotHoverEventArgs(index, patch, local, Kind == PlotKind.RenditionCorrected));
    }

    private int HitRendition(Point p)
    {
        var report = Report;
        if (report is null || report.Patches.Count == 0)
        {
            return -1;
        }

        var layout = MeasureRendition(report.Patches.Count, Bounds.Width, Bounds.Height);
        var x = p.X - layout.Ox;
        var y = p.Y - layout.Oy;
        var cell = layout.Square + layout.Gap;
        if (x < 0 || y < 0 || cell <= 0)
        {
            return -1;
        }

        var col = (int)(x / cell);
        var row = (int)(y / cell);
        if (col < 0 || row < 0 || col >= layout.Cols || row >= layout.Rows)
        {
            return -1;
        }

        if (x - col * cell > layout.Square || y - row * cell > layout.Square)
        {
            return -1;
        }

        var index = row * layout.Cols + col;
        return index < report.Patches.Count ? index : -1;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 8 || h < 8)
        {
            return;
        }

        var local = new Rect(0, 0, w, h);
        using (context.PushClip(local))
        {
            var rendition = Kind is PlotKind.RenditionOriginal or PlotKind.RenditionCorrected;
            context.FillRectangle(PlotTheme.Solid(rendition ? PlotTheme.RenditionBg : PlotTheme.Bg), local);
            var report = Report;
            if (Kind == PlotKind.StageBars)
            {
                DrawStageBars(context, report, w, h);
                return;
            }

            if (report is null || report.Patches.Count == 0)
            {
                PlotTheme.DrawText(context, "Run Apply to plot patches", w / 2, h / 2 - 7, 12, PlotTheme.Label, TextAlignment.Center);
                return;
            }

            switch (Kind)
            {
                case PlotKind.Bars:
                    DrawBars(context, report, w, h);
                    break;
                case PlotKind.Scatter:
                    DrawScatter(context, report, w, h);
                    break;
                case PlotKind.Residual:
                    DrawResidual(context, report, w, h);
                    break;
                case PlotKind.RenditionCorrected:
                    DrawRendition(context, report, w, h, corrected: true);
                    break;
                default:
                    DrawRendition(context, report, w, h, corrected: false);
                    break;
            }
        }
    }

    private static void DrawBars(DrawingContext context, CorrectionReport report, double w, double h)
    {
        var patches = report.Patches;
        var max = Math.Max(1, patches.Max(p => Math.Max(Finite(p.Before), Finite(p.After)))) * 1.12;
        var map = new PlotMap(w, h, 0, patches.Count, 0, max);
        DrawAxes(context, w, h, map, "Patch", "CIEDE2000");
        var group = Math.Max(2, map.PlotW / Math.Max(1, patches.Count));
        var barW = Math.Max(1, group * 0.36);
        var before = Color.FromRgb(245, 158, 11);
        var after = Color.FromRgb(16, 185, 129);
        using (context.PushClip(new Rect(map.PadL, map.PadT, map.PlotW, map.PlotH)))
        {
            for (var i = 0; i < patches.Count; i++)
            {
                var p = patches[i];
                var x = map.PadL + i * group;
                var yb = map.Sy(Finite(p.Before));
                var ya = map.Sy(Finite(p.After));
                var y0 = map.Sy(0);
                context.FillRectangle(PlotTheme.Solid(before), new Rect(x + group * 0.12, yb, barW, y0 - yb));
                context.FillRectangle(PlotTheme.Solid(after), new Rect(x + group * 0.5, ya, barW, y0 - ya));
            }
        }

        DrawLegend(context, map.PadL + 8, map.PadT + 8, [
            ("Before", before, LegendShape.Square),
            ("After", after, LegendShape.Square),
        ]);
    }

    private static void DrawStageBars(DrawingContext context, CorrectionReport? report, double w, double h)
    {
        var stages = report?.EffectiveStages ?? [];
        if (stages.Count == 0)
        {
            PlotTheme.DrawText(context, "No ΔE stage data available", w / 2, h / 2 - 7, 12, PlotTheme.Label, TextAlignment.Center);
            return;
        }

        var max = 1.0;
        foreach (var stage in stages)
        {
            max = Math.Max(max, Math.Max(Finite(stage.MeanBefore), Finite(stage.MeanAfter)));
        }

        max *= 1.18;
        var map = new PlotMap(w, h, 0, stages.Count, 0, max, 52, 14, 28, 48);
        DrawCategoryAxes(context, w, h, map, stages.Select(s => s.Name).ToArray(), "Stage", "Mean ΔE");

        var group = Math.Max(8, map.PlotW / stages.Count);
        var barW = Math.Min(36, Math.Max(6, group * 0.28));
        var gap = barW * 0.28;
        var before = Color.FromRgb(239, 68, 68);
        var after = Color.FromRgb(34, 197, 94);
        var beforeLabel = Color.FromRgb(252, 165, 165);
        var afterLabel = Color.FromRgb(134, 239, 172);
        var y0 = map.Sy(0);

        using (context.PushClip(new Rect(map.PadL, map.PadT - 16, map.PlotW, map.PlotH + 16)))
        {
            for (var i = 0; i < stages.Count; i++)
            {
                var stage = stages[i];
                var cx = map.PadL + (i + 0.5) * group;
                var yb = map.Sy(Finite(stage.MeanBefore));
                var ya = map.Sy(Finite(stage.MeanAfter));
                var bx = cx - barW - gap / 2;
                var ax = cx + gap / 2;
                context.DrawRectangle(
                    PlotTheme.Solid(Color.FromArgb(217, before.R, before.G, before.B)),
                    new Pen(PlotTheme.Solid(Color.FromRgb(185, 28, 28)), 0.8),
                    new Rect(bx, yb, barW, Math.Max(1, y0 - yb)),
                    3, 3);
                context.DrawRectangle(
                    PlotTheme.Solid(Color.FromArgb(217, after.R, after.G, after.B)),
                    new Pen(PlotTheme.Solid(Color.FromRgb(22, 101, 52)), 0.8),
                    new Rect(ax, ya, barW, Math.Max(1, y0 - ya)),
                    3, 3);
                PlotTheme.DrawText(context, Finite(stage.MeanBefore).ToString("0.0"), bx + barW / 2, yb - 14, 9,
                    beforeLabel, TextAlignment.Center, FontWeight.SemiBold);
                PlotTheme.DrawText(context, Finite(stage.MeanAfter).ToString("0.0"), ax + barW / 2, ya - 14, 9,
                    afterLabel, TextAlignment.Center, FontWeight.SemiBold);
            }
        }

        DrawLegend(context, map.PadL + map.PlotW - 108, map.PadT + 6, [
            ("Before", before, LegendShape.Square),
            ("After", after, LegendShape.Square),
        ]);
    }

    private static void DrawScatter(DrawingContext context, CorrectionReport report, double w, double h)
    {
        var map = new PlotMap(w, h, 0, 1, 0, 1);
        DrawAxes(context, w, h, map, "Reference mean RGB (sRGB 0–1)", "Measured mean RGB (sRGB 0–1)",
            [0, 0.2, 0.4, 0.6, 0.8, 1], [0, 0.2, 0.4, 0.6, 0.8, 1]);
        using (context.PushClip(new Rect(map.PadL, map.PadT, map.PlotW, map.PlotH)))
        {
            var ideal = new Pen(PlotTheme.Solid(Color.FromArgb(140, PlotTheme.Legend.R, PlotTheme.Legend.G, PlotTheme.Legend.B)), 1.25)
            {
                DashStyle = new DashStyle([6, 4], 0),
            };
            context.DrawLine(ideal, new Point(map.Sx(0), map.Sy(0)), new Point(map.Sx(1), map.Sy(1)));
        }

        var original = new System.Collections.Generic.List<(double X, double Y)>();
        var corrected = new System.Collections.Generic.List<(double X, double Y)>();
        using (context.PushClip(new Rect(map.PadL, map.PadT, map.PlotW, map.PlotH)))
        {
            foreach (var p in report.Patches)
            {
                if (p.Measured.Length < 3 || p.Reference.Length < 3)
                {
                    continue;
                }

                var x = Mean(p.Reference);
                var y = Mean(p.Measured);
                original.Add((x, y));
                DrawTriangle(context, map.Sx(x), map.Sy(y), BrushFromRgb(p.Measured, 0.65));
                if (p.Corrected.Length >= 3)
                {
                    var cy = Mean(p.Corrected);
                    corrected.Add((x, cy));
                    context.DrawEllipse(BrushFromRgb(p.Corrected, 0.85), new Pen(PlotTheme.Solid(PlotTheme.Ink), 1),
                        new Point(map.Sx(x), map.Sy(cy)), 4.2, 4.2);
                }
            }
        }

        using (context.PushClip(new Rect(map.PadL, map.PadT, map.PlotW, map.PlotH)))
        {
            DrawFit(context, map, original, Color.FromRgb(245, 158, 11), dashed: true);
            DrawFit(context, map, corrected, Color.FromRgb(16, 185, 129), dashed: false);
        }

        DrawLegend(context, map.PadL + 8, map.PadT + 8, [
            ("Original △", PlotTheme.Label, LegendShape.Triangle),
            (corrected.Count > 0 ? "Corrected ●" : "Corrected (Apply first)", PlotTheme.Legend, LegendShape.Circle),
            ("1:1 ideal", PlotTheme.Legend, LegendShape.Dash),
        ]);
    }

    private static void DrawResidual(DrawingContext context, CorrectionReport report, double w, double h)
    {
        var points = new System.Collections.Generic.List<(double X, double Y, int Channel, bool Sat)>();
        var maxAbs = 0.2;
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

        var map = new PlotMap(w, h, 0, 1, -maxAbs, maxAbs);
        DrawAxes(context, w, h, map, "Reference mean RGB (sRGB 0–1)", "ΔRGB (sRGB, measured − reference)",
            [0, 0.2, 0.4, 0.6, 0.8, 1]);
        using (context.PushClip(new Rect(map.PadL, map.PadT, map.PlotW, map.PlotH)))
        {
            var zero = new Pen(PlotTheme.Solid(Color.FromArgb(140, PlotTheme.Legend.R, PlotTheme.Legend.G, PlotTheme.Legend.B)), 1)
            {
                DashStyle = new DashStyle([6, 4], 0),
            };
            context.DrawLine(zero, new Point(map.Sx(0), map.Sy(0)), new Point(map.Sx(1), map.Sy(0)));
        }

        var colors = new[] { Color.FromRgb(244, 63, 94), Color.FromRgb(16, 185, 129), Color.FromRgb(96, 165, 250) };
        using (context.PushClip(new Rect(map.PadL, map.PadT, map.PlotW, map.PlotH)))
        {
            foreach (var point in points)
            {
                var alpha = point.Sat ? (byte)102 : (byte)217;
                var color = colors[point.Channel];
                context.FillRectangle(
                    new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)),
                    new Rect(map.Sx(point.X) - 2.5, map.Sy(point.Y) - 2.5, 5, 5));
            }
        }

        DrawLegend(context, map.PadL + 8, map.PadT + 8, [
            ("R residual", colors[0], LegendShape.Square),
            ("G residual", colors[1], LegendShape.Square),
            ("B residual", colors[2], LegendShape.Square),
        ]);
    }

    private void DrawRendition(DrawingContext context, CorrectionReport report, double w, double h, bool corrected)
    {
        var n = report.Patches.Count;
        var layout = MeasureRendition(n, w, h);
        var inner = Math.Max(1, layout.Square * 0.55);
        var stroke = new Pen(new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)), 1);
        var hover = new Pen(PlotTheme.Solid(PlotTheme.BrushColor("BorderFocusBrush", Color.FromRgb(129, 140, 248))), 2);
        for (var i = 0; i < n; i++)
        {
            var rect = layout.PatchRect(i);
            var p = report.Patches[i];
            context.FillRectangle(BrushFromRgb(p.Reference, 1), rect);
            context.DrawRectangle(stroke, new Rect(rect.X + 0.5, rect.Y + 0.5, rect.Width - 1, rect.Height - 1));
            var innerRgb = corrected ? p.Corrected : p.Measured;
            if (innerRgb.Length >= 3)
            {
                var ix = rect.X + (layout.Square - inner) / 2;
                var iy = rect.Y + (layout.Square - inner) / 2;
                context.FillRectangle(BrushFromRgb(innerRgb, 1), new Rect(ix, iy, inner, inner));
            }

            if (i == _hoverIndex)
            {
                context.DrawRectangle(hover, new Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2));
            }
        }
    }

    private static RenditionLayout MeasureRendition(int n, double w, double h)
    {
        var (rows, cols) = InferGrid(n);
        const double inset = 8;
        var availableW = Math.Max(1, w - inset * 2);
        var availableH = Math.Max(1, h - inset * 2);
        var minGap = Math.Max(4, Math.Round(Math.Min(availableW, availableH) * 0.04));
        var square = Math.Max(2, Math.Min(
            (availableW - minGap * Math.Max(0, cols - 1)) / cols,
            (availableH - minGap * Math.Max(0, rows - 1)) / rows));
        var gridW = cols * square + Math.Max(0, cols - 1) * minGap;
        var gridH = rows * square + Math.Max(0, rows - 1) * minGap;
        return new RenditionLayout(rows, cols, (w - gridW) / 2, (h - gridH) / 2, square, minGap);
    }

    private static (int Rows, int Cols) InferGrid(int n)
    {
        if (n == 24) return (4, 6);
        if (n == 140) return (10, 14);
        var cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(n)));
        return (Math.Max(1, (int)Math.Ceiling(n / (double)cols)), cols);
    }

    private static void DrawAxes(
        DrawingContext context,
        double w,
        double h,
        PlotMap map,
        string xlabel,
        string ylabel,
        double[]? xTicks = null,
        double[]? yTicks = null)
    {
        xTicks ??= NiceTicks(map.X0, map.X1, 6);
        yTicks ??= NiceTicks(map.Y0, map.Y1, 5);
        var grid = new Pen(PlotTheme.Solid(PlotTheme.Grid), 1);
        var axis = new Pen(PlotTheme.Solid(PlotTheme.Axis), 1.25);
        using (context.PushClip(new Rect(map.PadL, map.PadT, map.PlotW, map.PlotH)))
        {
            foreach (var y in yTicks)
            {
                var py = map.Sy(y) + 0.5;
                context.DrawLine(grid, new Point(map.PadL, py), new Point(map.PadL + map.PlotW, py));
            }

            foreach (var x in xTicks)
            {
                var px = map.Sx(x) + 0.5;
                context.DrawLine(grid, new Point(px, map.PadT), new Point(px, map.PadT + map.PlotH));
            }
        }

        context.DrawRectangle(null, axis, new Rect(map.PadL + 0.5, map.PadT + 0.5, map.PlotW - 1, map.PlotH - 1));
        var xSpan = Math.Abs(map.X1 - map.X0);
        var ySpan = Math.Abs(map.Y1 - map.Y0);
        foreach (var x in xTicks)
        {
            var px = map.Sx(x);
            context.DrawLine(axis, new Point(px, map.PadT + map.PlotH), new Point(px, map.PadT + map.PlotH + 5));
            PlotTheme.DrawText(context, FormatTick(x, xSpan), px, map.PadT + map.PlotH + 8, 11, PlotTheme.Label, TextAlignment.Center);
        }

        foreach (var y in yTicks)
        {
            var py = map.Sy(y);
            context.DrawLine(axis, new Point(map.PadL, py), new Point(map.PadL - 5, py));
            PlotTheme.DrawText(context, FormatTick(y, ySpan), map.PadL - 8, py - 6, 11, PlotTheme.Label, TextAlignment.Right);
        }

        PlotTheme.DrawText(context, xlabel, map.PadL + map.PlotW / 2, h - 16, 11, PlotTheme.Label, TextAlignment.Center);
        using (context.PushTransform(Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(13, map.PadT + map.PlotH / 2)))
        {
            PlotTheme.DrawText(context, ylabel, 0, -6, 11, PlotTheme.Label, TextAlignment.Center);
        }
    }

    private static void DrawCategoryAxes(
        DrawingContext context,
        double w,
        double h,
        PlotMap map,
        string[] labels,
        string xlabel,
        string ylabel)
    {
        var yTicks = NiceTicks(map.Y0, map.Y1, 5);
        var grid = new Pen(PlotTheme.Solid(PlotTheme.Grid), 1) { DashStyle = new DashStyle([4, 4], 0) };
        var axis = new Pen(PlotTheme.Solid(PlotTheme.Axis), 1.25);
        using (context.PushClip(new Rect(map.PadL, map.PadT, map.PlotW, map.PlotH)))
        {
            foreach (var y in yTicks)
            {
                var py = map.Sy(y) + 0.5;
                context.DrawLine(grid, new Point(map.PadL, py), new Point(map.PadL + map.PlotW, py));
            }
        }

        context.DrawLine(axis, new Point(map.PadL, map.PadT), new Point(map.PadL, map.PadT + map.PlotH));
        context.DrawLine(axis, new Point(map.PadL, map.PadT + map.PlotH), new Point(map.PadL + map.PlotW, map.PadT + map.PlotH));
        var ySpan = Math.Abs(map.Y1 - map.Y0);
        foreach (var y in yTicks)
        {
            var py = map.Sy(y);
            context.DrawLine(axis, new Point(map.PadL, py), new Point(map.PadL - 5, py));
            PlotTheme.DrawText(context, FormatTick(y, ySpan), map.PadL - 8, py - 6, 11, PlotTheme.Label, TextAlignment.Right);
        }

        var group = Math.Max(1, map.PlotW / Math.Max(1, labels.Length));
        for (var i = 0; i < labels.Length; i++)
        {
            var cx = map.PadL + (i + 0.5) * group;
            context.DrawLine(axis, new Point(cx, map.PadT + map.PlotH), new Point(cx, map.PadT + map.PlotH + 5));
            PlotTheme.DrawText(context, labels[i], cx, map.PadT + map.PlotH + 8, 11, PlotTheme.Legend, TextAlignment.Center, FontWeight.SemiBold);
        }

        PlotTheme.DrawText(context, xlabel, map.PadL + map.PlotW / 2, h - 16, 11, PlotTheme.Label, TextAlignment.Center);
        using (context.PushTransform(Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(13, map.PadT + map.PlotH / 2)))
        {
            PlotTheme.DrawText(context, ylabel, 0, -6, 11, PlotTheme.Label, TextAlignment.Center);
        }
    }

    private static void DrawLegend(DrawingContext context, double x, double y, (string Label, Color Color, LegendShape Shape)[] items)
    {
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            var dy = y + i * 16;
            switch (item.Shape)
            {
                case LegendShape.Triangle:
                    DrawTriangle(context, x + 4, dy + 4, PlotTheme.Solid(item.Color));
                    break;
                case LegendShape.Circle:
                    context.DrawEllipse(PlotTheme.Solid(item.Color), new Pen(PlotTheme.Solid(PlotTheme.Ink), 1), new Point(x + 4, dy + 4), 4, 4);
                    break;
                case LegendShape.Dash:
                    context.DrawLine(new Pen(PlotTheme.Solid(item.Color), 1.25) { DashStyle = new DashStyle([4, 3], 0) },
                        new Point(x - 1, dy + 4), new Point(x + 10, dy + 4));
                    break;
                default:
                    context.FillRectangle(PlotTheme.Solid(item.Color), new Rect(x, dy, 8, 8));
                    break;
            }

            PlotTheme.DrawText(context, item.Label, x + 14, dy - 1, 11, PlotTheme.Legend);
        }
    }

    private static void DrawTriangle(DrawingContext context, double cx, double cy, IBrush fill)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(cx, cy - 5), true);
            g.LineTo(new Point(cx + 5, cy + 4));
            g.LineTo(new Point(cx - 5, cy + 4));
            g.EndFigure(true);
        }

        context.DrawGeometry(fill, new Pen(PlotTheme.Solid(PlotTheme.Ink), 1), geo);
    }

    private static void DrawFit(DrawingContext context, PlotMap map, System.Collections.Generic.List<(double X, double Y)> points, Color color, bool dashed)
    {
        if (points.Count < 2)
        {
            return;
        }

        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (x, y) in points)
        {
            sx += x;
            sy += y;
            sxx += x * x;
            sxy += x * y;
        }

        var n = points.Count;
        var denom = n * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-12)
        {
            return;
        }

        var slope = (n * sxy - sx * sy) / denom;
        var intercept = (sy - slope * sx) / n;
        var pen = new Pen(PlotTheme.Solid(color), 1.25);
        if (dashed)
        {
            pen.DashStyle = new DashStyle([5, 4], 0);
        }

        context.DrawLine(pen, new Point(map.Sx(0), map.Sy(intercept)), new Point(map.Sx(1), map.Sy(slope + intercept)));
    }

    private static double[] NiceTicks(double min, double max, int count)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || Math.Abs(max - min) < 1e-12)
        {
            return [min];
        }

        var span = max - min;
        var raw = span / Math.Max(1, count - 1);
        var mag = Math.Pow(10, Math.Floor(Math.Log10(Math.Abs(raw))));
        var norm = raw / mag;
        var step = (norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10) * mag;
        var start = Math.Ceiling((min - 1e-9) / step) * step;
        var ticks = new System.Collections.Generic.List<double>();
        for (var value = start; value <= max + step * 1e-6; value += step)
        {
            ticks.Add(Math.Round(value, 8));
        }

        return ticks.Count > 0 ? ticks.ToArray() : [min, max];
    }

    private static string FormatTick(double value, double span)
    {
        if (Math.Abs(value) < 1e-12) return "0";
        if (span >= 20) return Math.Round(value).ToString("0");
        if (span >= 2) return value.ToString("0.#");
        return value.ToString("0.##");
    }

    private static double Finite(double v) => double.IsFinite(v) ? v : 0;

    private static double Mean(double[] rgb) => rgb.Length < 3 ? 0 : (rgb[0] + rgb[1] + rgb[2]) / 3;

    private static IBrush BrushFromRgb(double[] rgb, double alpha)
    {
        if (rgb.Length < 3)
        {
            return Brushes.Gray;
        }

        byte Ch(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
        return new SolidColorBrush(Color.FromArgb((byte)Math.Clamp((int)(alpha * 255), 0, 255), Ch(rgb[0]), Ch(rgb[1]), Ch(rgb[2])));
    }

    private enum LegendShape { Square, Triangle, Circle, Dash }

    private readonly record struct RenditionLayout(int Rows, int Cols, double Ox, double Oy, double Square, double Gap)
    {
        public Rect PatchRect(int i)
        {
            var r = i / Cols;
            var c = i % Cols;
            return new Rect(Ox + c * (Square + Gap), Oy + r * (Square + Gap), Square, Square);
        }
    }

    private readonly struct PlotMap
    {
        public PlotMap(double w, double h, double x0, double x1, double y0, double y1)
            : this(w, h, x0, x1, y0, y1, 52, 14, 16, 42)
        {
        }

        public PlotMap(double w, double h, double x0, double x1, double y0, double y1, double padL, double padR, double padT, double padB)
        {
            PadL = padL;
            PadR = padR;
            PadT = padT;
            PadB = padB;
            PlotW = Math.Max(10, w - PadL - PadR);
            PlotH = Math.Max(10, h - PadT - PadB);
            X0 = x0;
            X1 = x1;
            Y0 = y0;
            Y1 = y1;
        }

        public double PadL { get; }
        public double PadR { get; }
        public double PadT { get; }
        public double PadB { get; }
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
