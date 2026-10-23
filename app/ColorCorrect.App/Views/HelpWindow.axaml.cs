using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ColorCorrect.Engine;

namespace ColorCorrect.App.Views;

public partial class HelpWindow : Window
{
    internal const string ResearchUrl = "https://acsess.onlinelibrary.wiley.com/doi/full/10.1002/ppj2.70067";
    internal sealed record Topic(string Title, string[] Paragraphs);
    internal static Topic[] LoadTopics()
    {
        using var stream = AssetLoader.Open(new Uri("avares://ColorCorrect.App/Resources/UserGuide.json"));
        return JsonSerializer.Deserialize<Topic[]>(stream) ?? [];
    }

    private readonly Topic[] _topics;
    private Topic[] _filtered = [];
    private Bitmap? _illustration;

    public HelpWindow()
    {
        _topics = LoadTopics();
        InitializeComponent();
        FilterTopics("");
        AddHandler(InputElement.KeyDownEvent, OnGuideKeyDown,
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        DialogLayout.FitToOwner(this);
        Dispatcher.UIThread.Post(() => SearchBox.Focus(NavigationMethod.Tab), DispatcherPriority.Input);
    }

    private void Search_Changed(object? sender, TextChangedEventArgs e)
    {
        // XAML can raise TextChanged before the remaining named controls exist.
        if (TopicList is not null) FilterTopics(SearchBox.Text ?? "");
    }

    private void FilterTopics(string query)
    {
        query = query.Trim();
        _filtered = _topics.Where(t => t.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || t.Paragraphs.Any(p => p.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        TopicList.ItemsSource = _filtered.Select(t => t.Title).ToArray();
        TopicList.SelectedIndex = _filtered.Length > 0 ? 0 : -1;
        ShowArticle();
    }

    private void Topic_Changed(object? sender, SelectionChangedEventArgs e) => ShowArticle();

    private void ShowArticle()
    {
        if (ArticlePanel is null) return;
        ArticlePanel.Children.Clear();
        _illustration?.Dispose();
        _illustration = null;
        var topic = TopicList.SelectedIndex >= 0 && TopicList.SelectedIndex < _filtered.Length
            ? _filtered[TopicList.SelectedIndex]
            : new Topic("No matching topics", ["Try a shorter search, such as FFC, NN, ΔE, or API."]);
        ArticlePanel.Children.Add(new TextBlock
        {
            Text = topic.Title, FontSize = 19, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        if (topic.Title == "Start here")
        {
            var flow = new StackPanel { Spacing = 8 };
            flow.Children.Add(new TextBlock { Text = "Enabled stages, in order", FontSize = 12 });
            var stages = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            foreach (var (label, index) in new[] { "FFC", "GC", "WB", "CC" }.Select((s, i) => (s, i)))
            {
                var cell = new Border
                {
                    CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 7),
                    Margin = new Thickness(0, 0, 6, 4),
                    Child = new TextBlock { Text = $"{index + 1}  {label}", FontSize = 14, FontWeight = FontWeight.SemiBold }
                };
                cell.Bind(Border.BackgroundProperty, this.GetResourceObservable("SidebarBrush"));
                stages.Children.Add(cell);
            }
            flow.Children.Add(stages);
            flow.Children.Add(new TextBlock
            {
                Text = "Blank field → FFC\nChart neutrals → GC / WB    ·    Chart color patches → CC\nSaved model → matching images; no new chart needed",
                FontSize = 12, LineHeight = 19, TextWrapping = TextWrapping.Wrap
            });
            ArticlePanel.Children.Add(flow);
        }
        AddIllustration(topic.Title);
        foreach (var paragraph in topic.Paragraphs)
        {
            ArticlePanel.Children.Add(new TextBlock
            {
                Text = paragraph, FontSize = 14, LineHeight = 22, TextWrapping = TextWrapping.Wrap
            });
        }
        GuideScroll.Offset = new Vector(0, 0);
    }

    private void AddIllustration(string title)
    {
        if (title == "FFC · illumination")
        {
            var field = new Border { Height = 90, CornerRadius = new CornerRadius(8),
                Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops = new GradientStops { new GradientStop(Colors.Gray, 0), new GradientStop(Colors.White, 1) } } };
            ArticlePanel.Children.Add(field);
            ArticlePanel.Children.Add(new TextBlock { Text = "Blank white field (schematic) → estimate spatial gain → compensate uneven illumination. Use Blank.JPG, never the color chart, as the FFC white reference.",
                TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        }
        var picture = title == "Session & catalog" ? "session-workbench.png"
            : title == "CC & neural networks" ? "process-controls.png" : null;
        if (picture is not null)
        {
            using var stream = AssetLoader.Open(new Uri("avares://ColorCorrect.App/Resources/Guide/" + picture));
            _illustration = new Bitmap(stream);
            var image = new Image { Source = _illustration, Stretch = Stretch.Uniform };
            Avalonia.Automation.AutomationProperties.SetName(image, "Illustration: " + title);
            ArticlePanel.Children.Add(image);
            ArticlePanel.Children.Add(new TextBlock { Text = "Workbench example — read the steps below for current controls.",
                TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        }
        if (title == "GC & white balance" || title == "Start here")
        {
            var chart = new UniformGrid { Rows = 4, Columns = 6, Height = 112 };
            string[] colors = ["#735244", "#c29682", "#627a9d", "#576c43", "#8580b1", "#67bdaa",
                "#d67e2c", "#505ba6", "#c15a63", "#5e3c6c", "#9dbc40", "#e0a32e",
                "#383d96", "#469449", "#af363c", "#e7c71f", "#bb5695", "#0885a1",
                "#f3f3f3", "#c8c8c8", "#a0a0a0", "#7a7a7a", "#555555", "#343434"];
            for (var i = 0; i < colors.Length; i++) chart.Children.Add(new Border {
                Background = Brush.Parse(colors[i]), Margin = new Thickness(2), CornerRadius = new CornerRadius(3),
                BorderBrush = Brushes.Teal, BorderThickness = new Thickness(i >= 18 ? 3 : 0) });
            ArticlePanel.Children.Add(chart);
            ArticlePanel.Children.Add(new TextBlock { Text = "Schematic Classic 24: outlined neutral row → GC tone response / WB. Color patches → CC. Not a reference-value table or a printable calibration target.",
                TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        }
    }

    private async void Research_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!await Launcher.LaunchUriAsync(new Uri(ResearchUrl)))
                GuideStatus.Text = "Browser unavailable. Research DOI: 10.1002/ppj2.70067";
        }
        catch (Exception)
        {
            GuideStatus.Text = "Could not open browser. Research DOI: 10.1002/ppj2.70067";
        }
    }

    private async void OfflineResearch_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(EnginePaths.FindRepoRoot(), "docs", "research", "ppj2.70067.pdf");
            if (!File.Exists(path))
            {
                GuideStatus.Text = "PDF not bundled: publisher download was blocked. Place the study PDF at docs/research/ppj2.70067.pdf for offline access.";
                return;
            }
            if (!await Launcher.LaunchFileInfoAsync(new FileInfo(path)))
                GuideStatus.Text = "Open docs/research/ppj2.70067.pdf with a PDF reader.";
        }
        catch (Exception) { GuideStatus.Text = "Could not open offline PDF. See docs/research/README.md."; }
    }

    private async void Documentation_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var directory = new DirectoryInfo(Path.Combine(EnginePaths.FindRepoRoot(), "docs"));
            if (!directory.Exists || !await Launcher.LaunchDirectoryInfoAsync(directory))
                GuideStatus.Text = "Find docs/ and notebooks/ beside the application payload.";
        }
        catch (Exception)
        {
            GuideStatus.Text = "Find docs/ and notebooks/ beside the application payload.";
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
    private void OnGuideKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
