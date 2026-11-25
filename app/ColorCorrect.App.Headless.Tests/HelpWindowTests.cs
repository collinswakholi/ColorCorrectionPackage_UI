using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ColorCorrect.App.Views;
using ColorCorrect.App.ViewModels;
using SkiaSharp;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class HelpWindowTests
{
    private static readonly string[] RequiredTopics =
    [
        "Get started", "How correction works", "Flat-field correction (FFC)", "Tone (GC) and white balance",
        "Choosing a color model (CC)", "Reading the ΔE analysis", "Models: save, load, .cube", "Batch processing",
        "Speed: devices and modes", "Sessions and files", "Keyboard shortcuts", "Troubleshooting",
    ];

    [AvaloniaFact]
    public void Guide_CoversEveryRequiredTask_WithValidTextAndKnownFigures()
    {
        var topics = HelpWindow.LoadTopics();
        Assert.Equal(topics.Length, topics.Select(t => t.Title).Distinct().Count());
        foreach (var title in RequiredTopics) Assert.Contains(topics, t => t.Title == title);
        string[] kinds = ["goal", "steps", "see", "fix", "keys", "text", "note", "visual"];
        foreach (var topic in topics)
        {
            Assert.False(string.IsNullOrWhiteSpace(topic.Summary));
            Assert.NotEmpty(topic.Sections);
            foreach (var section in topic.Sections)
            {
                Assert.Contains(section.Kind, kinds);
                Assert.NotEmpty(section.Items);
                if (section.Kind == "visual") Assert.Contains(section.Items[0], GuideVisuals.Ids);
                if (section.Kind is "fix" or "keys") Assert.All(section.Items, item => Assert.Contains('|', item));
                Assert.All(section.Items, item =>
                {
                    Assert.True(item.Split("**").Length % 2 == 1, $"Unbalanced bold markup: {item}");
                    Assert.True(item.Count(c => c == '`') % 2 == 0, $"Unbalanced key markup: {item}");
                });
            }
            foreach (var text in topic.SearchableText())
            {
                Assert.DoesNotContain("Â", text);
                Assert.DoesNotContain("â€", text);
                Assert.DoesNotContain("Î”", text);
            }
        }
        var figures = topics.SelectMany(t => t.Sections).Where(s => s.Kind == "visual").Select(s => s.Items[0]).ToHashSet();
        Assert.True(GuideVisuals.Ids.All(figures.Contains), "Every guide figure is used by a topic.");
    }

    [AvaloniaFact]
    public void Guide_StatesTheAppsRealLimitsAndControlNames()
    {
        var all = string.Join("\n", HelpWindow.LoadTopics().SelectMany(t => t.SearchableText()));
        // Limits and labels that the guide repeats must match the code they describe.
        Assert.Contains($"up to {MainWindowViewModel.MaxWorking} photos", all);
        Assert.Contains("200 megapixels", all);
        Assert.Contains("over the 200 MP limit", all);
        Assert.Contains("65×65×65", all);
        Assert.Contains("33-point 3D LUT", all);
        foreach (var label in new[]
                 {
                     "Fit + apply", "Apply model", "Use loaded", "Fit from image", "Relink image…", "Compatibility mode",
                     "Apply engine change", "Check setup", "Use CPU", "Preview results", "Apply current model", "Fit from chart",
                     "Save selected", "Skip saturated patches", "Watch folder", "Chart face", "White file", "Exported .cube (LUT does not include flat-field).",
                     "CIEDE2000", "ICC", "cannot certify your individual chart", "Blank.JPG", "not an internet server",
                 })
        {
            Assert.Contains(label, all);
        }
        var keys = HelpWindow.LoadTopics().Single(t => t.Title == "Keyboard shortcuts")
            .Sections.Where(s => s.Kind == "keys").SelectMany(s => s.Items).Select(i => i.Split('|')[0]).ToArray();
        foreach (var key in new[] { "F1", "F5", "Ctrl+O", "Ctrl+Shift+O", "Ctrl+S", "Ctrl+Shift+S", "Ctrl+Alt+O", "Ctrl+B", "Ctrl+T", "Ctrl+Alt+A", "Alt+Left / Alt+Right", "Delete", "Esc" })
        {
            Assert.Contains(key, keys);
        }
    }

    [AvaloniaFact]
    public void Search_MatchesBodyText_RequiresEveryWord_AndRanksTitlesFirst()
    {
        var topics = HelpWindow.LoadTopics();
        var byBody = HelpWindow.Search(topics, "megapixels");
        Assert.Equal(new[] { "Troubleshooting" }, byBody.Select(t => t.Title).ToArray());
        Assert.All(HelpWindow.Search(topics, "white reference"), t =>
        {
            var text = string.Join("\n", t.SearchableText());
            Assert.Contains("white", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("reference", text, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal("Batch processing", HelpWindow.Search(topics, "batch").First().Title);
        Assert.Contains(HelpWindow.Search(topics, "relink"), t => t.Title == "Sessions and files");
        Assert.Empty(HelpWindow.Search(topics, "chart-split")); // figure ids are not reader text
        Assert.Equal(topics.Length, HelpWindow.Search(topics, "   ").Length);
    }

    [AvaloniaFact]
    public void MainWindow_F1OpensGuide_EscapeReturnsFocus()
    {
        var window = new MainWindow
        {
            Width = 1002, Height = 672,
            DataContext = new MainWindowViewModel(persistenceEnabled: false) { Job = { Busy = false }}
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            window.KeyPressQwerty(PhysicalKey.F1, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            var guide = Assert.Single(window.OwnedWindows.OfType<HelpWindow>());
            Assert.Equal("ColorCorrect Studio · Guide", guide.Title);
            Assert.True(guide.SearchBox.IsFocused);
            Assert.True(window.ModalScrim.IsVisible);
            guide.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.False(guide.IsVisible);
            Assert.False(window.ModalScrim.IsVisible);
            Assert.True(window.HelpButton.IsFocused);
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public void Guide_SearchesBody_HandlesNoResults_AndClosesWithEscape()
    {
        var owner = new Window { Width = 1002, Height = 672 };
        owner.Show();
        var guide = new HelpWindow();
        var closed = guide.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();
        try
        {
            var total = HelpWindow.LoadTopics().Length;
            Assert.True(guide.SearchBox.IsFocused);
            Assert.Equal(total, guide.TopicList.ItemCount);
            Assert.Equal($"{total} topics", guide.ResultCount.Text);

            guide.SearchBox.Text = "megapixels";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, guide.TopicList.ItemCount);
            Assert.Equal("Troubleshooting", guide.TopicList.SelectedItem);
            Assert.Contains("Photos above 200 megapixels are refused", guide.ArticleText);
            Assert.Equal($"1 of {total} topics", guide.ResultCount.Text);

            guide.SearchBox.Text = "not-a-topic-12345";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, guide.TopicList.ItemCount);
            Assert.Equal("No matches", guide.ResultCount.Text);
            Assert.Contains("No matching topics", guide.ArticleText);
            var suggestions = guide.ArticlePanel.GetVisualDescendants().OfType<Button>().ToArray();
            Assert.Equal(HelpWindow.Suggestions.Length, suggestions.Length);
            Assert.All(suggestions, b => Assert.StartsWith("Search for ", AutomationProperties.GetName(b)));
            suggestions.Single(b => (string?)b.Content == "Relink").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Relink", guide.SearchBox.Text);
            Assert.True(guide.TopicList.ItemCount >= 2);

            guide.SearchBox.Text = "";
            Dispatcher.UIThread.RunJobs();
            guide.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(closed.IsCompleted);
        }
        finally { guide.Close(); owner.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public void Guide_KeyboardMovesBetweenSearchTopicsAndArticle()
    {
        var owner = new Window { Width = 1002, Height = 672 };
        owner.Show();
        var guide = new HelpWindow();
        _ = guide.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.True(guide.SearchBox.IsFocused);
            guide.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            var focused = Assert.IsAssignableFrom<Control>(guide.FocusManager?.GetFocusedElement());
            Assert.Same(guide.TopicList, focused.FindAncestorOfType<ListBox>());

            guide.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, guide.TopicList.SelectedIndex);
            Assert.Contains("How correction works", guide.ArticleText);

            guide.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(guide.GuideScroll.IsFocused);

            guide.TopicList.SelectedIndex = 0;
            guide.TopicList.ContainerFromIndex(0)!.Focus();
            Dispatcher.UIThread.RunJobs();
            guide.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(guide.SearchBox.IsFocused);
        }
        finally { guide.Close(); owner.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Guide_AllTopicsRenderWithinOwner_InBothThemes(bool light)
    {
        var theme = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var owner = new Window { Width = 1002, Height = 672, RequestedThemeVariant = theme };
        owner.Show();
        var guide = new HelpWindow { RequestedThemeVariant = theme };
        _ = guide.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.True(guide.Bounds.Width <= owner.ClientSize.Width);
            Assert.True(guide.Bounds.Height <= owner.ClientSize.Height);
            var figures = new HashSet<string>();
            for (var i = 0; i < guide.TopicList.ItemCount; i++)
            {
                guide.TopicList.SelectedIndex = i;
                Dispatcher.UIThread.RunJobs();
                Assert.True(guide.GuideScroll.Viewport.Height > 200);
                Assert.True(guide.GuideScroll.Extent.Width <= guide.GuideScroll.Viewport.Width + .5);
                Assert.StartsWith((string)guide.TopicList.SelectedItem!, guide.ArticleText);
                figures.UnionWith(guide.RenderedFigures);
                Assert.All(guide.ArticlePanel.GetVisualDescendants().OfType<Image>(), image =>
                {
                    var bitmap = Assert.IsAssignableFrom<Bitmap>(image.Source);
                    Assert.True(bitmap.PixelSize.Width > 100);
                });
            }
            Assert.Equal(GuideVisuals.Ids.ToHashSet(), figures);
            guide.TopicList.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            VisualRegression.Match(guide, $"guide-{theme.Key.ToString()!.ToLowerInvariant()}");
        }
        finally { guide.Close(); owner.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public void Guide_IsReadableAtMinimumSize()
    {
        var guide = new HelpWindow { Width = 600, Height = 360 };
        guide.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Equal(600, guide.MinWidth);
            Assert.Equal(360, guide.MinHeight);
            for (var i = 0; i < guide.TopicList.ItemCount; i++)
            {
                guide.TopicList.SelectedIndex = i;
                Dispatcher.UIThread.RunJobs();
                Assert.True(guide.GuideScroll.Viewport.Width >= 280, $"Article is {guide.GuideScroll.Viewport.Width:0} px wide.");
                Assert.True(guide.GuideScroll.Viewport.Height >= 150);
                Assert.True(guide.GuideScroll.Extent.Width <= guide.GuideScroll.Viewport.Width + .5,
                    $"{guide.TopicList.SelectedItem} overflows horizontally at the minimum size.");
            }
        }
        finally { guide.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public void Guide_InteractiveElementsAndFiguresHaveAccessibleNames()
    {
        var guide = new HelpWindow { Width = 900, Height = 640 };
        guide.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            foreach (var control in new Control[] { guide.SearchBox, guide.TopicList, guide.GuideScroll })
            {
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)));
            }
            Assert.All(guide.GetVisualDescendants().OfType<Button>(), b => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(b))));
            for (var i = 0; i < guide.TopicList.ItemCount; i++)
            {
                guide.TopicList.SelectedIndex = i;
                Dispatcher.UIThread.RunJobs();
                // Figures are pictures, not controls: they take no focus and say what they show.
                Assert.Empty(guide.ArticlePanel.GetVisualDescendants().OfType<InputElement>().Where(e => e.Focusable && e is not ScrollViewer));
                var figures = guide.ArticlePanel.GetVisualDescendants().OfType<Control>()
                    .Where(c => AutomationProperties.GetName(c)?.StartsWith("Illustration: ", StringComparison.Ordinal) == true).ToArray();
                Assert.Equal(guide.RenderedFigures.Count, figures.Length);
                Assert.All(figures, f => Assert.False(f.IsHitTestVisible));
            }
        }
        finally { guide.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public void Guide_SwitchesThemeWhileOpen()
    {
        var guide = new HelpWindow { Width = 900, Height = 640, RequestedThemeVariant = ThemeVariant.Light };
        guide.Show();
        guide.SearchBox.Text = "flat-field";
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Contains("ffc", guide.RenderedFigures);
            var light = MeanLuma(guide);
            guide.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();
            var dark = MeanLuma(guide);
            Assert.True(light - dark > 80, $"Light {light:0} vs dark {dark:0}: the guide did not follow the theme.");
            guide.RequestedThemeVariant = ThemeVariant.Light;
            Dispatcher.UIThread.RunJobs();
            Assert.True(Math.Abs(MeanLuma(guide) - light) < 3, "Returning to light restores the light rendering.");
        }
        finally { guide.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    /// <summary>
    /// Renders review screenshots of several topics in both themes into TestResults/Guide. Set
    /// CCP_GUIDE_SCREENSHOTS=1 to also refresh the reviewed subset in docs/images/guide.
    /// </summary>
    [AvaloniaFact]
    public void Guide_RendersReviewScreenshots()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var target = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestResults", "Guide"));
        var docs = Environment.GetEnvironmentVariable("CCP_GUIDE_SCREENSHOTS") == "1" ? Path.Combine(root, "docs", "images", "guide") : null;
        string[] published = ["get-started", "ffc", "delta-e", "cc-models", "batch", "troubleshooting", "minimum-size"];
        Directory.CreateDirectory(target);
        if (docs is not null) Directory.CreateDirectory(docs);
        void Save(Bitmap frame, string slug, ThemeVariant theme)
        {
            var name = $"{slug}-{theme.Key.ToString()!.ToLowerInvariant()}.png";
            frame.Save(Path.Combine(target, name));
            if (docs is not null && published.Contains(slug)) frame.Save(Path.Combine(docs, name));
        }
        (string Query, string Slug, double Scroll)[] shots =
        [
            ("", "get-started", 0), ("Flat-field correction", "ffc", 0), ("Reading the ΔE", "delta-e", 0),
            ("Choosing a color model", "cc-models", 0), ("Batch processing", "batch", 0),
            ("Keyboard shortcuts", "shortcuts", 0), ("Troubleshooting", "troubleshooting", 0),
            ("How correction works", "pipeline", 0), ("Tone (GC)", "gc-wb", 0), ("Speed: devices", "devices", 420),
            ("Models: save", "models", 160), ("Get started", "run-area", 880), ("Reading the ΔE", "rendition", 760),
        ];
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var guide = new HelpWindow { Width = 900, Height = 640, RequestedThemeVariant = theme };
            guide.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                foreach (var (query, slug, scroll) in shots)
                {
                    guide.SearchBox.Text = query;
                    Dispatcher.UIThread.RunJobs();
                    guide.GuideScroll.Offset = new Vector(0, scroll);
                    Dispatcher.UIThread.RunJobs();
                    using var frame = guide.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    Save(frame, slug, theme);
                }
            }
            finally { guide.Close(); Dispatcher.UIThread.RunJobs(); }

            var small = new HelpWindow { Width = 600, Height = 360, RequestedThemeVariant = theme };
            small.Show();
            small.SearchBox.Text = "Batch processing";
            Dispatcher.UIThread.RunJobs();
            try
            {
                using var frame = small.CaptureRenderedFrame();
                Assert.NotNull(frame);
                Save(frame, "minimum-size", theme);
            }
            finally { small.Close(); Dispatcher.UIThread.RunJobs(); }
        }
    }

    private static double MeanLuma(Window window)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var png = new MemoryStream();
        frame.Save(png);
        png.Position = 0;
        using var bitmap = SKBitmap.Decode(png);
        return bitmap.Pixels.Average(p => (p.Red * 54 + p.Green * 183 + p.Blue * 19) / 256.0);
    }
}
