using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Path = Avalonia.Controls.Shapes.Path;

namespace ColorCorrect.App.Views;

/// <summary>
/// Numbers drawn by the Guide's figures. Generated from the public test photographs by
/// scripts/generate_guide_assets.py; nothing here is typed in by hand.
/// </summary>
internal sealed record GuideData(
    string Model, double MeanBefore, double MeanAfter, double MedianBefore, double MedianAfter,
    double MaxBefore, double MaxAfter, double ValidationMean, double ValidationMedian, double ValidationMax,
    int ValidationFolds, GuideStage[] Stages, GuidePatch[] Patches, double FfcGainMax, int FfcDegree,
    double[] ProfileMeasured, double[] ProfileCorrected, GuidePair[] DeltaEPairs)
{
    private static readonly Lazy<GuideData> Cached = new(() =>
    {
        using var stream = AssetLoader.Open(new Uri(GuideVisuals.AssetRoot + "guide-data.json"));
        return JsonSerializer.Deserialize<GuideData>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Guide data is empty.");
    });

    public static GuideData Load() => Cached.Value;
}

internal sealed record GuideStage(string Name, double Before, double After);
internal sealed record GuidePatch(string Name, string Measured, string Corrected, string Reference,
    double Before, double After, double MeasuredL, double CorrectedL, double ReferenceL);
internal sealed record GuidePair(double DeltaE, string A, string B);

/// <summary>
/// Builds the Guide's figures. Chrome uses theme resources so every figure follows the
/// light and dark themes; only photographs and measured patch colors are literal colors.
/// </summary>
internal sealed class GuideVisuals
{
    internal const string AssetRoot = "avares://ColorCorrect.App/Assets/Guide/";
    internal static readonly string[] Ids =
    [
        "chart-split", "run-mock", "pipeline", "stage-bars", "ffc", "gc", "cc-models", "de-scale",
        "metric-mock", "rendition", "model-mock", "sample-split", "batch-mock", "device-mock", "modes",
    ];

    private readonly Window _host;
    private readonly List<Bitmap> _bitmaps;

    public GuideVisuals(Window host, List<Bitmap> bitmaps)
    {
        _host = host;
        _bitmaps = bitmaps;
    }

    public Control Build(string id) => id switch
    {
        "chart-split" => Split("chart-split.jpg", "Before · as photographed", "After · corrected", ChartStrip()),
        "sample-split" => Split("sample-split.jpg", "Before", "After · same model", null),
        "run-mock" => RunMock(),
        "pipeline" => Pipeline(),
        "stage-bars" => StageBars(),
        "ffc" => Ffc(),
        "gc" => ToneAndBalance(),
        "cc-models" => ModelTable(),
        "de-scale" => DeltaEScale(),
        "metric-mock" => MetricMock(),
        "rendition" => Rendition(),
        "model-mock" => ModelMock(),
        "batch-mock" => BatchMock(),
        "device-mock" => DeviceMock(),
        "modes" => Modes(),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown guide figure."),
    };

    // ------------------------------------------------------------------ primitives

    private T Res<T>(T target, AvaloniaProperty property, string key) where T : AvaloniaObject
    {
        target.Bind(property, _host.GetResourceObservable(key));
        return target;
    }

    internal TextBlock Text(string text, string size = "FontSizeBodySm", string brush = "TextPrimaryBrush",
        FontWeight weight = FontWeight.Normal, TextWrapping wrap = TextWrapping.Wrap)
    {
        var block = new TextBlock { Text = text, FontWeight = weight, TextWrapping = wrap };
        Res(block, TextBlock.FontSizeProperty, size);
        Res(block, TextBlock.ForegroundProperty, brush);
        return block;
    }

    private Border Box(Control child, string background = "SidebarBrush", string border = "ThemeBorderBrush",
        double thickness = 1, string radius = "RadiusMd", Thickness? padding = null)
    {
        var box = new Border { Child = child, BorderThickness = new Thickness(thickness), Padding = padding ?? new Thickness(8, 6) };
        Res(box, Border.BackgroundProperty, background);
        Res(box, Border.BorderBrushProperty, border);
        Res(box, Border.CornerRadiusProperty, radius);
        return box;
    }

    private static Control Figure(Control content, string name)
    {
        content.IsHitTestVisible = false;
        AutomationProperties.SetName(content, "Illustration: " + name);
        return content;
    }

    private Bitmap Bitmap(string file)
    {
        using var stream = AssetLoader.Open(new Uri(AssetRoot + file));
        var bitmap = new Bitmap(stream);
        _bitmaps.Add(bitmap);
        return bitmap;
    }

    private static IBrush Hex(string hex) => new SolidColorBrush(Color.Parse(hex));

    private static string QualityKey(double deltaE) => deltaE switch
    {
        < 1.0 => "QualityExcellent",
        < 2.0 => "QualityGood",
        < 3.5 => "QualityFair",
        < 5.0 => "QualityWarning",
        _ => "QualityPoor",
    };

    // Same thresholds and words as DeltaEQualityTextConverter in the main window.
    private static string QualityWord(double deltaE) => deltaE switch
    {
        < 1.0 => "Excellent",
        < 2.0 => "Very good",
        < 3.5 => "Good",
        < 5.0 => "Fair",
        _ => "Poor",
    };

    private static string F(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private Border Callout(int number)
    {
        var badge = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = number.ToString(CultureInfo.InvariantCulture), FontSize = 10, FontWeight = FontWeight.Bold,
                Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        return Res(badge, Border.BackgroundProperty, "PrimaryBrush");
    }

    private Control WithCallout(Control control, int number, bool before = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (before) row.Children.Add(Callout(number));
        row.Children.Add(control);
        if (!before) row.Children.Add(Callout(number));
        return row;
    }

    private Control Legend(params string[] items)
    {
        var list = new StackPanel { Spacing = 5, Margin = new Thickness(0, 10, 0, 0) };
        for (var i = 0; i < items.Length; i++)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var badge = Callout(i + 1);
            badge.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(badge);
            var text = Text(items[i], "FontSizeBodySm", "TextSecondaryBrush");
            text.Margin = new Thickness(8, 1, 0, 0);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            list.Children.Add(row);
        }
        return list;
    }

    private Border FakeButton(string label, bool primary = false, double minWidth = 0)
    {
        var text = Text(label, "FontSizeBodySm", "TextPrimaryBrush",
            primary ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping.NoWrap);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        // Matches the app's PrimaryButton, which always uses white text on PrimaryBrush.
        if (primary) text.Foreground = Brushes.White;
        var button = Box(text, primary ? "PrimaryBrush" : "CardBrush", primary ? "PrimaryBrush" : "ThemeBorderBrush",
            1, "RadiusMd", new Thickness(10, 6));
        button.MinWidth = minWidth;
        return button;
    }

    private Control FakeCheck(string label, bool on)
    {
        var mark = Box(new TextBlock
        {
            Text = on ? "✓" : "", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        }, on ? "PrimaryBrush" : "CardBrush", on ? "PrimaryBrush" : "TextMutedBrush", 1, "RadiusSm", new Thickness(0));
        mark.Width = 15;
        mark.Height = 15;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(mark);
        row.Children.Add(Text(label, "FontSizeBodySm", "TextPrimaryBrush", wrap: TextWrapping.NoWrap));
        return row;
    }

    private Control FakeRadio(string label, bool on)
    {
        var ring = new Ellipse { Width = 14, Height = 14, StrokeThickness = on ? 4 : 1.5, VerticalAlignment = VerticalAlignment.Center };
        Res(ring, Shape.StrokeProperty, on ? "PrimaryBrush" : "TextMutedBrush");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        row.Children.Add(ring);
        row.Children.Add(Text(label, "FontSizeBodySm", "TextPrimaryBrush", wrap: TextWrapping.NoWrap));
        return row;
    }

    private Control FakeCombo(string label)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(Text(label, "FontSizeBodySm", "TextPrimaryBrush", wrap: TextWrapping.NoWrap));
        var chevron = Text("▾", "FontSizeBodySm", "TextSecondaryBrush");
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(chevron);
        return Box(grid, "CardBrush", "ThemeBorderBrush", 1, "RadiusMd", new Thickness(10, 6));
    }

    private Control Caps(string text)
    {
        var caption = Text(text, "FontSizeCaption", "TextMutedBrush", FontWeight.SemiBold);
        caption.LetterSpacing = 1.1;
        return caption;
    }

    private Border MockFrame(Control content) =>
        Box(content, "SidebarBrush", "ThemeBorderBrush", 1, "RadiusLg", new Thickness(14, 12));

    // ------------------------------------------------------------------ photographs

    private Control Split(string file, string beforeLabel, string afterLabel, Control? footer)
    {
        var image = new Image { Source = Bitmap(file), Stretch = Stretch.Uniform, MaxHeight = 300 };
        var layers = new Grid();
        layers.Children.Add(image);
        var halves = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var divider = new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Left };
        Res(divider, Border.BackgroundProperty, "CardBrush");
        Grid.SetColumn(divider, 1);
        halves.Children.Add(divider);
        halves.Children.Add(Chip(beforeLabel, HorizontalAlignment.Left, 0));
        halves.Children.Add(Chip(afterLabel, HorizontalAlignment.Right, 1));
        layers.Children.Add(halves);
        var frame = new Border { ClipToBounds = true, Child = layers, HorizontalAlignment = HorizontalAlignment.Center };
        Res(frame, Border.CornerRadiusProperty, "RadiusLg");
        if (footer is null) return Figure(frame, beforeLabel + " and " + afterLabel);
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(frame);
        stack.Children.Add(footer);
        return Figure(stack, beforeLabel + " and " + afterLabel);

        Control Chip(string label, HorizontalAlignment align, int column)
        {
            var chip = Box(Text(label, "FontSizeCaption", "TextPrimaryBrush", FontWeight.SemiBold, TextWrapping.NoWrap),
                "CardBrush", "CardBrush", 0, "RadiusSm", new Thickness(7, 3));
            chip.Opacity = 0.92;
            chip.Margin = new Thickness(8);
            chip.HorizontalAlignment = align;
            chip.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(chip, column);
            return chip;
        }
    }

    private Control ChartStrip()
    {
        var data = GuideData.Load();
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Pill($"Mean ΔE00 before  {F(data.MeanBefore)}", data.MeanBefore));
        row.Children.Add(Pill($"after  {F(data.MeanAfter)}", data.MeanAfter));
        row.Children.Add(Pill($"CC-only validation  {F(data.ValidationMean)}", data.ValidationMean));
        return row;
    }

    private Control Pill(string text, double deltaE)
    {
        var key = QualityKey(deltaE);
        var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        Res(dot, Shape.FillProperty, key + "Brush");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(dot);
        row.Children.Add(Text(text, "FontSizeCaption", "TextPrimaryBrush", FontWeight.SemiBold, TextWrapping.NoWrap));
        var pill = Box(row, key + "BgBrush", key + "Brush", 1, "RadiusMd", new Thickness(8, 4));
        pill.Margin = new Thickness(0, 0, 6, 6);
        return pill;
    }

    // ------------------------------------------------------------------ diagrams

    private Control Pipeline()
    {
        (string Abbr, string Name, string Source)[] stages =
        [
            ("FFC", "Flat field", "Blank photo"), ("SAT", "Clipping", "Chart, fit only"),
            ("GC", "Tone", "Chart greys"), ("WB", "White", "Chart greys"), ("CC", "Color", "Chart colors"),
        ];
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        for (var i = 0; i < stages.Length; i++)
        {
            if (i > 0) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        }
        for (var i = 0; i < stages.Length; i++)
        {
            var column = i * 2;
            var source = Text(stages[i].Source, "FontSizeCaption", "TextSecondaryBrush");
            source.TextAlignment = TextAlignment.Center;
            source.Margin = new Thickness(0, 0, 0, 4);
            Grid.SetColumn(source, column);
            grid.Children.Add(source);

            var card = new StackPanel { Spacing = 1 };
            var abbr = Text(stages[i].Abbr, "FontSizeSubheading", i == 1 ? "TextSecondaryBrush" : "AccentTextBrush", FontWeight.Bold, TextWrapping.NoWrap);
            abbr.HorizontalAlignment = HorizontalAlignment.Center;
            var name = Text(stages[i].Name, "FontSizeCaption", "TextSecondaryBrush");
            name.TextAlignment = TextAlignment.Center;
            card.Children.Add(abbr);
            card.Children.Add(name);
            var box = Box(card, i == 1 ? "InsetBrush" : "PrimaryLightBrush", i == 1 ? "ThemeBorderBrush" : "PrimaryBrush", 1, "RadiusMd", new Thickness(2, 8));
            Grid.SetRow(box, 1);
            Grid.SetColumn(box, column);
            grid.Children.Add(box);
            if (i > 0)
            {
                var arrow = new Path { Data = Geometry.Parse("M0,0 L5,4 L0,8"), StrokeThickness = 1.6, Margin = new Thickness(3, 0), VerticalAlignment = VerticalAlignment.Center };
                Res(arrow, Shape.StrokeProperty, "TextMutedBrush");
                Grid.SetRow(arrow, 1);
                Grid.SetColumn(arrow, column - 1);
                grid.Children.Add(arrow);
            }
        }
        var fast = Bracket("Fast mode: one 65³ LUT");
        Grid.SetRow(fast, 2);
        Grid.SetColumn(fast, 4);
        Grid.SetColumnSpan(fast, 5);
        grid.Children.Add(fast);
        var same = Bracket("same pass");
        Grid.SetRow(same, 2);
        grid.Children.Add(same);
        return Figure(grid, "Correction stages: FFC, saturation, GC, WB, CC");

        Control Bracket(string label)
        {
            var bracket = new Border { BorderThickness = new Thickness(1, 0, 1, 1), Height = 7, Margin = new Thickness(4, 6, 4, 0) };
            Res(bracket, Border.BorderBrushProperty, "TextMutedBrush");
            var text = Text(label, "FontSizeCaption", "TextSecondaryBrush");
            text.TextAlignment = TextAlignment.Center;
            text.Margin = new Thickness(0, 3, 0, 0);
            var stack = new StackPanel();
            stack.Children.Add(bracket);
            stack.Children.Add(text);
            return stack;
        }
    }

    private Control StageBars()
    {
        var data = GuideData.Load();
        var rows = new List<(string Label, double Value)> { ("As photographed", data.Stages[0].Before) };
        rows.AddRange(data.Stages.Select(s => ("After " + s.Name, s.After)));
        var max = rows.Max(r => r.Value);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowSpacing = 6, ColumnSpacing = 10 };
        for (var i = 0; i < rows.Count; i++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var i = 0; i < rows.Count; i++)
        {
            var label = Text(rows[i].Label, "FontSizeBodySm", "TextSecondaryBrush", wrap: TextWrapping.NoWrap);
            Grid.SetRow(label, i);
            grid.Children.Add(label);
            var track = new Grid { ColumnDefinitions = new ColumnDefinitions($"{rows[i].Value.ToString(CultureInfo.InvariantCulture)}*,{(max - rows[i].Value + 0.001).ToString(CultureInfo.InvariantCulture)}*"), Height = 14, VerticalAlignment = VerticalAlignment.Center };
            var bar = new Border { CornerRadius = new CornerRadius(3) };
            Res(bar, Border.BackgroundProperty, QualityKey(rows[i].Value) + "Brush");
            track.Children.Add(bar);
            Grid.SetRow(track, i);
            Grid.SetColumn(track, 1);
            grid.Children.Add(track);
            var value = Text(F(rows[i].Value), "FontSizeBodySm", "TextPrimaryBrush", FontWeight.SemiBold, TextWrapping.NoWrap);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 2);
            grid.Children.Add(value);
        }
        return Figure(grid, "Mean delta E after each stage");
    }

    private Control Ffc()
    {
        var data = GuideData.Load();
        var images = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*") };
        images.Children.Add(Captioned(new Image { Source = Bitmap("blank-field.jpg"), Stretch = Stretch.Uniform }, "Blank.JPG, contrast stretched"));
        var gain = new StackPanel { Spacing = 4 };
        gain.Children.Add(new Image { Source = Bitmap("ffc-gain.png"), Stretch = Stretch.Uniform });
        var ramp = new Border
        {
            Height = 8, CornerRadius = new CornerRadius(4),
            // Same stops as colormap() in scripts/generate_guide_assets.py.
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#1C4D54"), 0), new GradientStop(Color.Parse("#408C85"), .25),
                    new GradientStop(Color.Parse("#CCBD85"), .5), new GradientStop(Color.Parse("#E68C47"), .75),
                    new GradientStop(Color.Parse("#C74D38"), 1),
                },
            },
        };
        gain.Children.Add(ramp);
        var scale = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        scale.Children.Add(Text("×1.00", "FontSizeCaption", "TextSecondaryBrush"));
        var top = Text("×" + data.FfcGainMax.ToString("0.00", CultureInfo.InvariantCulture), "FontSizeCaption", "TextSecondaryBrush");
        top.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(top, 1);
        scale.Children.Add(top);
        gain.Children.Add(scale);
        var gainCard = Captioned(gain, $"Gain map, degree-{data.FfcDegree} fit");
        Grid.SetColumn(gainCard, 2);
        images.Children.Add(gainCard);

        var plot = new GuidePlot { Height = 120, Margin = new Thickness(0, 10, 0, 0), Painter = PaintProfile };
        var stack = new StackPanel();
        stack.Children.Add(images);
        stack.Children.Add(plot);
        stack.Children.Add(PlotLegend(("TextSecondaryBrush", "as photographed", false), ("AccentTextBrush", "after the FFC gain", false)));
        return Figure(stack, "Flat-field correction from Blank.JPG");

        Control Captioned(Control content, string caption)
        {
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(content);
            panel.Children.Add(Text(caption, "FontSizeCaption", "TextSecondaryBrush"));
            return panel;
        }

        void PaintProfile(GuidePlot p, DrawingContext dc, Rect r)
        {
            var all = data.ProfileMeasured.Concat(data.ProfileCorrected).ToArray();
            var lo = Math.Floor(all.Min() - 0.5);
            var hi = Math.Ceiling(all.Max() + 0.5);
            var area = p.Axes(dc, r, "position across the frame →", "L*", lo, hi);
            p.Line(dc, area, data.ProfileMeasured, lo, hi, p.Brush("TextSecondaryBrush"), 1.6);
            p.Line(dc, area, data.ProfileCorrected, lo, hi, p.Brush("AccentTextBrush"), 2.2);
        }
    }

    private Control PlotLegend(params (string Brush, string Label, bool Hollow)[] entries)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (brush, label, hollow) in entries)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 14, 0) };
            Shape mark = hollow
                ? new Ellipse { Width = 9, Height = 9, StrokeThickness = 1.6 }
                : new Rectangle { Width = 14, Height = 3 };
            Res(mark, hollow ? Shape.StrokeProperty : Shape.FillProperty, brush);
            mark.VerticalAlignment = VerticalAlignment.Center;
            item.Children.Add(mark);
            item.Children.Add(Text(label, "FontSizeCaption", "TextSecondaryBrush", wrap: TextWrapping.NoWrap));
            row.Children.Add(item);
        }
        return row;
    }

    private Control ToneAndBalance()
    {
        var data = GuideData.Load();
        var neutrals = data.Patches.Skip(18).Take(6).ToArray();
        var plot = new GuidePlot { Height = 190, Painter = Paint };
        var strip = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("22,22"), Margin = new Thickness(0, 10, 0, 0) };
        var photo = Text("Photo", "FontSizeCaption", "TextSecondaryBrush");
        photo.VerticalAlignment = VerticalAlignment.Center;
        var fixedLabel = Text("Corrected", "FontSizeCaption", "TextSecondaryBrush");
        fixedLabel.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(fixedLabel, 1);
        strip.Children.Add(photo);
        strip.Children.Add(fixedLabel);
        foreach (var (row, pick) in new[] { (0, (Func<GuidePatch, string>)(p => p.Measured)), (1, p => p.Corrected) })
        {
            var cells = new UniformGrid { Columns = neutrals.Length, Margin = new Thickness(10, 1, 0, 1) };
            foreach (var patch in neutrals) cells.Children.Add(new Border { Background = Hex(pick(patch)), Margin = new Thickness(1, 0) });
            Grid.SetRow(cells, row);
            Grid.SetColumn(cells, 1);
            strip.Children.Add(cells);
        }
        var stack = new StackPanel();
        stack.Children.Add(plot);
        stack.Children.Add(PlotLegend(("TextSecondaryBrush", "as photographed", true), ("AccentTextBrush", "after GC and WB (and CC)", false)));
        stack.Children.Add(strip);
        return Figure(stack, "Grey patch lightness before and after correction");

        void Paint(GuidePlot p, DrawingContext dc, Rect r)
        {
            var area = p.Axes(dc, r, "reference L*", "L*", 0, 100);
            Point At(double x, double y) => new(area.X + area.Width * x / 100, area.Bottom - area.Height * y / 100);
            dc.DrawLine(new Pen(p.Brush("TextMutedBrush"), 1, new DashStyle([4, 3], 0)), At(0, 0), At(100, 100));
            var muted = new Pen(p.Brush("TextSecondaryBrush"), 1.6);
            var accent = p.Brush("AccentTextBrush");
            foreach (var patch in neutrals)
            {
                var measured = At(patch.ReferenceL, patch.MeasuredL);
                var corrected = At(patch.ReferenceL, patch.CorrectedL);
                dc.DrawLine(new Pen(p.Brush("ThemeBorderBrush"), 1), measured, corrected);
                dc.DrawEllipse(null, muted, measured, 4.5, 4.5);
                dc.DrawEllipse(accent, null, corrected, 4, 4);
            }
        }
    }

    private Control ModelTable()
    {
        (string Name, string Note, int Flex, int Risk, bool Default)[] rows =
        [
            ("3x3 CCM", "9 numbers", 1, 1, false),
            ("Root polynomial", "degree 2 or 3", 3, 2, true),
            ("PLS", "components", 3, 2, false),
            ("Polynomial regression", "degree 1–7", 4, 4, false),
            ("Neural net", "1–4 layers", 5, 5, false),
        ];
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 14, RowSpacing = 8 };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var flexHead = Caps("FLEXIBILITY");
        Grid.SetColumn(flexHead, 1);
        var riskHead = Caps("OVERFIT RISK");
        Grid.SetColumn(riskHead, 2);
        grid.Children.Add(Caps("METHOD"));
        grid.Children.Add(flexHead);
        grid.Children.Add(riskHead);
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var name = new StackPanel { Spacing = 1 };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            title.Children.Add(Text(rows[i].Name, "FontSizeBodySm", "TextPrimaryBrush", FontWeight.SemiBold));
            if (rows[i].Default)
            {
                var tag = Box(Text("default", "FontSizeCaption", "AccentTextBrush", FontWeight.SemiBold, TextWrapping.NoWrap),
                    "PrimaryLightBrush", "PrimaryBrush", 1, "RadiusSm", new Thickness(5, 0));
                tag.VerticalAlignment = VerticalAlignment.Center;
                title.Children.Add(tag);
            }
            name.Children.Add(title);
            name.Children.Add(Text(rows[i].Note, "FontSizeCaption", "TextSecondaryBrush"));
            Grid.SetRow(name, i + 1);
            grid.Children.Add(name);
            var flex = Dots(rows[i].Flex, "AccentTextBrush");
            Grid.SetRow(flex, i + 1);
            Grid.SetColumn(flex, 1);
            grid.Children.Add(flex);
            var risk = Dots(rows[i].Risk, "WarningBrush");
            Grid.SetRow(risk, i + 1);
            Grid.SetColumn(risk, 2);
            grid.Children.Add(risk);
        }
        return Figure(Box(grid, "SidebarBrush", "ThemeBorderBrush", 1, "RadiusLg", new Thickness(14, 12)), "Color model comparison");

        Control Dots(int filled, string brush)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            for (var i = 0; i < 5; i++)
            {
                var dot = new Ellipse { Width = 8, Height = 8 };
                Res(dot, Shape.FillProperty, i < filled ? brush : "ThemeBorderBrush");
                row.Children.Add(dot);
            }
            return row;
        }
    }

    private Control DeltaEScale()
    {
        var data = GuideData.Load();
        (string Word, string Range, double Sample)[] bands =
        [
            ("Excellent", "< 1", 0.5), ("Very good", "1–2", 1.5), ("Good", "2–3.5", 3), ("Fair", "3.5–5", 4), ("Poor", "≥ 5", 6),
        ];
        var scale = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"), ColumnSpacing = 3 };
        for (var i = 0; i < bands.Length; i++)
        {
            var cell = new StackPanel { Spacing = 3 };
            var bar = new Border { Height = 8, CornerRadius = new CornerRadius(4) };
            Res(bar, Border.BackgroundProperty, QualityKey(bands[i].Sample) + "Brush");
            cell.Children.Add(bar);
            cell.Children.Add(Text(bands[i].Word, "FontSizeCaption", "TextPrimaryBrush", FontWeight.SemiBold));
            cell.Children.Add(Text(bands[i].Range, "FontSizeCaption", "TextSecondaryBrush"));
            Grid.SetColumn(cell, i);
            scale.Children.Add(cell);
        }
        var pairs = new UniformGrid { Columns = data.DeltaEPairs.Length, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var pair in data.DeltaEPairs)
        {
            var swatch = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Height = 34 };
            swatch.Children.Add(new Border { Background = Hex(pair.A), CornerRadius = new CornerRadius(4, 0, 0, 4) });
            var b = new Border { Background = Hex(pair.B), CornerRadius = new CornerRadius(0, 4, 4, 0) };
            Grid.SetColumn(b, 1);
            swatch.Children.Add(b);
            var cell = new StackPanel { Spacing = 3, Margin = new Thickness(2, 0) };
            cell.Children.Add(swatch);
            var label = Text("ΔE00 " + pair.DeltaE.ToString("0.#", CultureInfo.InvariantCulture), "FontSizeCaption", "TextSecondaryBrush");
            label.TextAlignment = TextAlignment.Center;
            cell.Children.Add(label);
            pairs.Children.Add(cell);
        }
        var stack = new StackPanel();
        stack.Children.Add(scale);
        stack.Children.Add(pairs);
        return Figure(stack, "Delta E quality scale and example color pairs");
    }

    private Control MetricMock()
    {
        var data = GuideData.Load();
        var cards = new UniformGrid { Columns = 3 };
        cards.Children.Add(Card("Fit mean ΔE", data.MeanAfter, data.MeanBefore, 1));
        cards.Children.Add(Card("Median ΔE", data.MedianAfter, data.MedianBefore, 0));
        cards.Children.Add(Card("Max ΔE", data.MaxAfter, data.MaxBefore, 2));
        var validation = Text($"CC-only validation · {data.ValidationFolds}-fold mean {F(data.ValidationMean)} · median {F(data.ValidationMedian)} · max {F(data.ValidationMax)}",
            "FontSizeCaption", "TextSecondaryBrush");
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(cards);
        stack.Children.Add(WithCallout(validation, 3, before: true));
        var frame = new StackPanel();
        frame.Children.Add(MockFrame(stack));
        frame.Children.Add(Legend(
            "The quality word comes from the scale above. The large number is after correction, the small one before.",
            "Max ΔE is the worst patch. Check it when the mean looks good.",
            "Validation holds patches out of the fit. Trust it more than the fit mean."));
        return Figure(frame, "Delta E summary cards");

        Control Card(string title, double after, double before, int callout)
        {
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            header.Children.Add(Text(title, "FontSizeCaption", "TextSecondaryBrush"));
            var word = Text(QualityWord(after), "FontSizeCaption", QualityKey(after) + "Brush", FontWeight.SemiBold, TextWrapping.NoWrap);
            Grid.SetColumn(word, 1);
            header.Children.Add(word);
            var values = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            values.Children.Add(Text(F(after), "FontSizeHeading", "TextPrimaryBrush", FontWeight.SemiBold, TextWrapping.NoWrap));
            var was = Text(F(before), "FontSizeCaption", "TextMutedBrush", wrap: TextWrapping.NoWrap);
            was.VerticalAlignment = VerticalAlignment.Bottom;
            was.Margin = new Thickness(0, 0, 0, 3);
            values.Children.Add(was);
            if (callout > 0)
            {
                var badge = Callout(callout);
                badge.Margin = new Thickness(2, 0, 0, 0);
                values.Children.Add(badge);
            }
            var body = new StackPanel { Spacing = 2 };
            body.Children.Add(header);
            body.Children.Add(values);
            var card = Box(body, "CardBrush", "ThemeBorderBrush", 1, "RadiusMd", new Thickness(8, 6));
            card.Margin = new Thickness(0, 0, 6, 0);
            return card;
        }
    }

    private Control Rendition()
    {
        var data = GuideData.Load();
        var grid = new UniformGrid { Columns = 6 };
        foreach (var patch in data.Patches)
        {
            var halves = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
            halves.Children.Add(new Border { Background = Hex(patch.Measured) });
            var corrected = new Border { Background = Hex(patch.Corrected) };
            Grid.SetColumn(corrected, 1);
            halves.Children.Add(corrected);
            var reference = new Border { Background = Hex(patch.Reference), Height = 8, Margin = new Thickness(0, 2, 0, 0) };
            DockPanel.SetDock(reference, Dock.Bottom);
            var tile = new DockPanel { Height = 42, Margin = new Thickness(1.5) };
            tile.Children.Add(reference);
            tile.Children.Add(halves);
            grid.Children.Add(new Border { ClipToBounds = true, CornerRadius = new CornerRadius(3), Child = tile });
        }
        var frame = Box(grid, "InsetBrush", "ThemeBorderBrush", 1, "RadiusLg", new Thickness(6));
        return Figure(frame, "Chart rendition: photographed, corrected and reference colors");
    }

    // ------------------------------------------------------------------ control mock-ups

    private Control RunMock()
    {
        var chips = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, done) in new[] { ("FFC", true), ("SAT", true), ("GC", true), ("WB", true), ("CC", false), ("Save result", false) })
        {
            var chip = Box(Text(label, "FontSizeCaption", done ? "AccentTextBrush" : "TextSecondaryBrush", FontWeight.SemiBold, TextWrapping.NoWrap),
                done ? "PrimaryLightBrush" : "CardBrush", done ? "PrimaryBrush" : "ThemeBorderBrush", 1, "RadiusSm", new Thickness(6, 2));
            chip.Margin = new Thickness(0, 0, 4, 4);
            chips.Children.Add(chip);
        }
        var progress = new Grid { ColumnDefinitions = new ColumnDefinitions("4*,2*"), Height = 4, Margin = new Thickness(0, 2, 0, 8) };
        var filled = new Border { CornerRadius = new CornerRadius(2) };
        Res(filled, Border.BackgroundProperty, "PrimaryBrush");
        var rest = new Border { CornerRadius = new CornerRadius(2) };
        Res(rest, Border.BackgroundProperty, "ThemeBorderBrush");
        Grid.SetColumn(rest, 1);
        progress.Children.Add(filled);
        progress.Children.Add(rest);

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto") };
        var run = FakeButton("Fit + apply", primary: true);
        run.Padding = new Thickness(10, 10);
        buttons.Children.Add(run);
        var batch = FakeButton("Batch", minWidth: 64);
        batch.Padding = new Thickness(10, 10);
        Grid.SetColumn(batch, 2);
        buttons.Children.Add(batch);

        var panel = new StackPanel();
        panel.Children.Add(WithCallout(chips, 1));
        panel.Children.Add(progress);
        var runRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        runRow.Children.Add(buttons);
        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        badges.Children.Add(Callout(2));
        badges.Children.Add(Callout(3));
        Grid.SetColumn(badges, 1);
        runRow.Children.Add(badges);
        panel.Children.Add(runRow);
        var hint = Text("Chart detected. Fit + apply trains a new model from this still.", "FontSizeCaption", "TextSecondaryBrush");
        hint.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(WithCallout(hint, 4));

        var stack = new StackPanel();
        stack.Children.Add(MockFrame(panel));
        stack.Children.Add(Legend(
            "Stage chips: lit while a stage runs or after it finishes.",
            "Fit + apply (F5). It reads Apply model when Use loaded is chosen.",
            "Batch (Ctrl+B): the same work for many photos.",
            "The hint says what to do next, or what is missing."));
        return Figure(stack, "Run area");
    }

    private Control ModelMock()
    {
        var radios = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        radios.Children.Add(WithCallout(FakeRadio("Use loaded", true), 1));
        radios.Children.Add(WithCallout(FakeRadio("Fit from image", false), 2));
        var buttons = new UniformGrid { Columns = 3, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var label in new[] { "Load", "Save", ".cube" })
        {
            var b = FakeButton(label);
            b.Margin = new Thickness(0, 0, 6, 0);
            buttons.Children.Add(b);
        }
        var buttonRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        buttonRow.Children.Add(buttons);
        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) };
        badges.Children.Add(Callout(3));
        badges.Children.Add(Callout(4));
        Grid.SetColumn(badges, 1);
        buttonRow.Children.Add(badges);

        var panel = new StackPanel();
        var header = Text("Model", "FontSizeBodySm", "TextPrimaryBrush", FontWeight.SemiBold);
        header.Margin = new Thickness(0, 0, 0, 8);
        panel.Children.Add(header);
        panel.Children.Add(radios);
        panel.Children.Add(buttonRow);
        var stack = new StackPanel();
        stack.Children.Add(MockFrame(panel));
        stack.Children.Add(Legend(
            "Use loaded: the main button becomes Apply model and reuses this model.",
            "Fit from image: Fit + apply trains a new model from the selected chart photo.",
            "Load and Save: the model as a JSON file.",
            ".cube: a 3D LUT of GC, WB and CC, without FFC."));
        return Figure(stack, "Model section");
    }

    private Control BatchMock()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Text("Batch", "FontSizeSubheading", "TextPrimaryBrush", FontWeight.SemiBold));
        var files = new StackPanel { Spacing = 4 };
        files.Children.Add(FakeCheck("Sample_3572.JPG", true));
        files.Children.Add(FakeCheck("Sample_3573.JPG", true));
        files.Children.Add(FakeCheck("Blank.JPG", false));
        panel.Children.Add(WithCallout(files, 1));
        panel.Children.Add(Text("FFC whites and *_corrected outputs start unchecked.", "FontSizeCaption", "TextSecondaryBrush"));
        panel.Children.Add(WithCallout(FakeCheck("Preview results", true), 2));
        var fit = FakeButton("Fit from chart");
        fit.HorizontalAlignment = HorizontalAlignment.Stretch;
        var apply = FakeButton("Apply current model");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,6,Auto") };
        grid.Children.Add(fit);
        grid.Children.Add(Positioned(Callout(3), 0, 1));
        grid.Children.Add(Positioned(apply, 2, 0));
        grid.Children.Add(Positioned(Callout(4), 2, 1));
        panel.Children.Add(grid);
        var stack = new StackPanel();
        stack.Children.Add(MockFrame(panel));
        stack.Children.Add(Legend(
            "Tick the photos to process. The white reference starts unticked.",
            "Preview results: on shows each result as it finishes; off is faster.",
            "Fit from chart: detect and fit each selected chart photo separately.",
            "Apply current model: reuse the loaded model; no chart needed."));
        return Figure(stack, "Batch panel");

        static Control Positioned(Control c, int row, int column)
        {
            Grid.SetRow(c, row);
            Grid.SetColumn(c, column);
            if (column == 1) c.Margin = new Thickness(6, 0, 0, 0);
            return c;
        }
    }

    private Control DeviceMock()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Text("Processing settings", "FontSizeSubheading", "TextPrimaryBrush", FontWeight.SemiBold));
        panel.Children.Add(Text("Image correction", "FontSizeBodySm", "TextSecondaryBrush"));
        panel.Children.Add(Row(FakeCombo("Auto (recommended)"), 1));
        var advanced = Text("▾  Advanced", "FontSizeBodySm", "TextPrimaryBrush", FontWeight.SemiBold);
        advanced.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(advanced);
        panel.Children.Add(Text("Detection engine", "FontSizeBodySm", "TextSecondaryBrush"));
        panel.Children.Add(Row(FakeCombo("Auto (recommended)"), 2));
        var apply = FakeButton("Apply engine change");
        panel.Children.Add(Row(apply, 3));
        panel.Children.Add(Row(FakeCheck("Compatibility mode", false), 4));
        var note = Text("Reproduce older results. Slower processing.", "FontSizeCaption", "TextSecondaryBrush");
        note.Margin = new Thickness(23, 0, 0, 0);
        panel.Children.Add(note);
        var stack = new StackPanel();
        stack.Children.Add(MockFrame(panel));
        stack.Children.Add(Legend(
            "Where corrections run: Auto, CPU or GPU.",
            "How charts are found: Auto, CPU or a GPU engine.",
            "Restarts the engine with the new detection choice.",
            "Off (the default) is fast mode."));
        return Figure(stack, "Processing settings");

        Control Row(Control content, int number)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(content);
            var badge = Callout(number);
            badge.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(badge, 1);
            row.Children.Add(badge);
            return row;
        }
    }

    private Control Modes()
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(Flow("FAST MODE · DEFAULT", ["Photo", "FFC + 65³ LUT", "Result"], [false, true, false], "One pass over the pixels. GPU when faster."));
        stack.Children.Add(Flow("COMPATIBILITY MODE", ["Photo", "FFC", "GC", "WB", "CC", "Result"], [false, true, true, true, true, false], "One pass per stage. Slower."));
        return Figure(Box(stack, "SidebarBrush", "ThemeBorderBrush", 1, "RadiusLg", new Thickness(14, 12)), "Fast and compatibility modes");

        Control Flow(string title, string[] steps, bool[] stage, string note)
        {
            var panel = new StackPanel { Spacing = 5 };
            panel.Children.Add(Caps(title));
            var row = new WrapPanel { Orientation = Orientation.Horizontal };
            for (var i = 0; i < steps.Length; i++)
            {
                if (i > 0)
                {
                    var arrow = Text("→", "FontSizeBodySm", "TextMutedBrush");
                    arrow.Margin = new Thickness(5, 3, 5, 0);
                    row.Children.Add(arrow);
                }
                var chip = Box(Text(steps[i], "FontSizeCaption", stage[i] ? "AccentTextBrush" : "TextPrimaryBrush", FontWeight.SemiBold, TextWrapping.NoWrap),
                    stage[i] ? "PrimaryLightBrush" : "CardBrush", stage[i] ? "PrimaryBrush" : "ThemeBorderBrush", 1, "RadiusSm", new Thickness(7, 3));
                chip.Margin = new Thickness(0, 0, 0, 4);
                row.Children.Add(chip);
            }
            panel.Children.Add(row);
            panel.Children.Add(Text(note, "FontSizeCaption", "TextSecondaryBrush"));
            return panel;
        }
    }
}

/// <summary>A small, theme-aware vector plot. Brushes are resolved on every render.</summary>
internal sealed class GuidePlot : Control
{
    public Action<GuidePlot, DrawingContext, Rect>? Painter { get; init; }

    public GuidePlot() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    public IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Painter?.Invoke(this, context, new Rect(Bounds.Size));
    }

    private FormattedText Label(string text) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface(TextElement.GetFontFamily(this)), 10, Brush("TextSecondaryBrush"));

    /// <summary>Draws a framed plot area with y-axis bounds and returns the inner area.</summary>
    public Rect Axes(DrawingContext dc, Rect bounds, string xLabel, string yLabel, double lo, double hi)
    {
        var loText = Label(lo.ToString("0", CultureInfo.InvariantCulture));
        var hiText = Label(hi.ToString("0", CultureInfo.InvariantCulture));
        var yText = Label(yLabel);
        var xText = Label(xLabel);
        var left = Math.Max(Math.Max(loText.Width, hiText.Width), yText.Width) + 8;
        var area = new Rect(bounds.X + left, bounds.Y + 4, Math.Max(10, bounds.Width - left - 4), Math.Max(10, bounds.Height - xText.Height - 10));
        var grid = new Pen(Brush("PlotGridBrush"), 1);
        for (var i = 0; i <= 4; i++)
        {
            var y = area.Y + area.Height * i / 4;
            dc.DrawLine(grid, new Point(area.X, y), new Point(area.Right, y));
        }
        dc.DrawRectangle(null, new Pen(Brush("PlotAxisBrush"), 1), area);
        dc.DrawText(hiText, new Point(area.X - hiText.Width - 5, area.Y - hiText.Height / 2));
        dc.DrawText(loText, new Point(area.X - loText.Width - 5, area.Bottom - loText.Height / 2));
        dc.DrawText(yText, new Point(area.X - yText.Width - 5, area.Y + area.Height / 2 - yText.Height / 2));
        dc.DrawText(xText, new Point(area.X + (area.Width - xText.Width) / 2, area.Bottom + 4));
        return area;
    }

    public void Line(DrawingContext dc, Rect area, IReadOnlyList<double> values, double lo, double hi, IBrush brush, double thickness)
    {
        if (values.Count < 2) return;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (var i = 0; i < values.Count; i++)
            {
                var point = new Point(area.X + area.Width * i / (values.Count - 1),
                    area.Bottom - area.Height * (values[i] - lo) / (hi - lo));
                if (i == 0) g.BeginFigure(point, false);
                else g.LineTo(point);
            }
            g.EndFigure(false);
        }
        dc.DrawGeometry(null, new Pen(brush, thickness, lineJoin: PenLineJoin.Round), geometry);
    }
}
