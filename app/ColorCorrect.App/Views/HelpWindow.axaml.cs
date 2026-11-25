using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ColorCorrect.Engine;
using Path = Avalonia.Controls.Shapes.Path;

namespace ColorCorrect.App.Views;

public partial class HelpWindow : Window
{
    internal const string ResearchUrl = "https://acsess.onlinelibrary.wiley.com/doi/full/10.1002/ppj2.70067";
    internal static readonly string[] Suggestions = ["FFC", "Batch", ".cube", "ΔE", "Relink", "Shortcuts"];

    /// <summary>
    /// One block of an article. Kind is goal, steps, see, fix ("Problem|Fix" items), keys
    /// ("Keys|Action" items), text, note or visual (Items[0] is the figure id, Items[1] its caption).
    /// Text may use **bold** and `key` markup.
    /// </summary>
    internal sealed record Section(string Kind, string? Heading, string[] Items);

    internal sealed record Topic(string Title, string Summary, Section[] Sections)
    {
        /// <summary>Everything a reader can see in the article, without markup or figure ids.</summary>
        public IEnumerable<string> SearchableText()
        {
            yield return Title;
            yield return Summary;
            foreach (var section in Sections)
            {
                if (section.Heading is { } heading) yield return heading;
                var items = section.Kind == "visual" ? section.Items.Skip(1) : section.Items;
                foreach (var item in items) yield return Plain(item);
            }
        }
    }

    internal static Topic[] LoadTopics()
    {
        using var stream = AssetLoader.Open(new Uri("avares://ColorCorrect.App/Resources/UserGuide.json"));
        return JsonSerializer.Deserialize<Topic[]>(stream) ?? [];
    }

    internal static string Plain(string text) => text.Replace("**", "", StringComparison.Ordinal)
        .Replace("`", "", StringComparison.Ordinal).Replace("|", " — ", StringComparison.Ordinal);

    /// <summary>Topics containing every word of the query; title matches first, then guide order.</summary>
    internal static Topic[] Search(IReadOnlyList<Topic> topics, string query)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return topics.ToArray();
        return topics
            .Select((topic, index) => (topic, index, text: string.Join("\n", topic.SearchableText())))
            .Where(t => terms.All(term => t.text.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(t => terms.All(term => t.topic.Title.Contains(term, StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
            .ThenBy(t => t.index)
            .Select(t => t.topic)
            .ToArray();
    }

    private readonly Topic[] _topics;
    private Topic[] _filtered = [];
    private readonly List<Bitmap> _bitmaps = [];
    private readonly GuideVisuals _visuals;
    private readonly StringBuilder _articleText = new();
    private readonly List<string> _figures = [];

    /// <summary>The plain text of the article on screen, for tests and assistive checks.</summary>
    internal string ArticleText => _articleText.ToString();
    internal IReadOnlyList<string> RenderedFigures => _figures;

    public HelpWindow()
    {
        _topics = LoadTopics();
        _visuals = new GuideVisuals(this, _bitmaps);
        InitializeComponent();
        FilterTopics("");
        AddHandler(InputElement.KeyDownEvent, OnGuideKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        SearchBox.AddHandler(InputElement.KeyDownEvent, Search_KeyDown, RoutingStrategies.Tunnel);
        TopicList.AddHandler(InputElement.KeyDownEvent, TopicList_KeyDown, RoutingStrategies.Tunnel);
        Closed += (_, _) => ReleaseBitmaps();
        GuideStatus.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty && GuideStatus.Text != DefaultStatus) GuideStatus.IsVisible = true;
        };
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // Give the article the room at the minimum window width.
        var compact = e.NewSize.Width < 760 || e.NewSize.Height < 480;
        BodyGrid.ColumnDefinitions[0].Width = new GridLength(e.NewSize.Width < 760 ? 164 : 200);
        Subtitle.IsVisible = !compact;
        // The default footer hint gives way at small sizes; real status messages always show.
        GuideStatus.IsVisible = !compact || GuideStatus.Text != DefaultStatus;
    }

    private const string DefaultStatus = "Available offline · F1 opens this guide · Esc closes it";

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

    private void Search_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Down or Key.Enter) || _filtered.Length == 0) return;
        e.Handled = true;
        if (TopicList.SelectedIndex < 0) TopicList.SelectedIndex = 0;
        FocusSelectedTopic();
    }

    private void TopicList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Up && TopicList.SelectedIndex <= 0)
        {
            e.Handled = true;
            SearchBox.Focus(NavigationMethod.Directional);
        }
        else if (e.Key is Key.Enter or Key.Right)
        {
            e.Handled = true;
            GuideScroll.Focus(NavigationMethod.Directional);
        }
    }

    private void FocusSelectedTopic()
    {
        TopicList.ScrollIntoView(TopicList.SelectedIndex);
        if (TopicList.ContainerFromIndex(TopicList.SelectedIndex) is { } item) item.Focus(NavigationMethod.Directional);
        else TopicList.Focus(NavigationMethod.Directional);
    }

    private void FilterTopics(string query)
    {
        query = query.Trim();
        var previous = TopicList.SelectedIndex >= 0 && TopicList.SelectedIndex < _filtered.Length ? _filtered[TopicList.SelectedIndex] : null;
        _filtered = Search(_topics, query);
        TopicList.ItemsSource = _filtered.Select(t => t.Title).ToArray();
        var keep = previous is null ? -1 : Array.IndexOf(_filtered, previous);
        TopicList.SelectedIndex = _filtered.Length == 0 ? -1 : keep >= 0 && query.Length == 0 ? keep : 0;
        ResultCount.Text = query.Length == 0 ? $"{_topics.Length} topics"
            : _filtered.Length == 0 ? "No matches"
            : $"{_filtered.Length} of {_topics.Length} topics";
        ShowArticle();
    }

    private void Topic_Changed(object? sender, SelectionChangedEventArgs e) => ShowArticle();

    private void ReleaseBitmaps()
    {
        foreach (var bitmap in _bitmaps) bitmap.Dispose();
        _bitmaps.Clear();
    }

    private void ShowArticle()
    {
        if (ArticlePanel is null) return;
        ArticlePanel.Children.Clear();
        ReleaseBitmaps();
        _articleText.Clear();
        _figures.Clear();
        if (TopicList.SelectedIndex < 0 || TopicList.SelectedIndex >= _filtered.Length)
        {
            ShowNoMatches();
        }
        else
        {
            var topic = _filtered[TopicList.SelectedIndex];
            AddTitle(topic.Title, topic.Summary);
            foreach (var section in topic.Sections) AddSection(section);
        }
        GuideScroll.Offset = new Vector(0, 0);
    }

    private void AddTitle(string title, string summary)
    {
        var header = new StackPanel { Spacing = 4 };
        header.Children.Add(_visuals.Text(title, "FontSizeHeading", "TextPrimaryBrush", FontWeight.SemiBold));
        header.Children.Add(_visuals.Text(summary, "FontSizeBody", "TextSecondaryBrush"));
        ArticlePanel.Children.Add(header);
        Record(title);
        Record(summary);
    }

    private void ShowNoMatches()
    {
        AddTitle("No matching topics", $"Nothing in the guide mentions “{SearchBox.Text?.Trim()}”.");
        ArticlePanel.Children.Add(Rich("Try a shorter word, or search for one of these:", "TextPrimaryBrush"));
        var chips = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var suggestion in Suggestions)
        {
            var button = new Button
            {
                Content = suggestion, Margin = new Thickness(0, 0, 8, 8),
                Theme = this.FindResource("SecondaryButton") as Avalonia.Styling.ControlTheme,
            };
            AutomationProperties.SetName(button, "Search for " + suggestion);
            button.Click += (_, _) => SearchBox.Text = suggestion;
            chips.Children.Add(button);
        }
        ArticlePanel.Children.Add(chips);
    }

    private void Record(string text) => _articleText.AppendLine(Plain(text));

    private void AddSection(Section section)
    {
        var block = new StackPanel { Spacing = 8 };
        if (!string.IsNullOrWhiteSpace(section.Heading) && section.Kind != "visual")
        {
            var heading = _visuals.Text(section.Heading, "FontSizeSubheading", "TextPrimaryBrush", FontWeight.SemiBold);
            block.Children.Add(heading);
            Record(section.Heading);
        }
        switch (section.Kind)
        {
            case "goal":
                foreach (var item in section.Items) block.Children.Add(Goal(item));
                break;
            case "steps":
                for (var i = 0; i < section.Items.Length; i++) block.Children.Add(Step(i + 1, section.Items[i]));
                break;
            case "see":
                foreach (var item in section.Items) block.Children.Add(Bullet(item));
                break;
            case "fix":
                foreach (var item in section.Items) block.Children.Add(Fix(item));
                break;
            case "keys":
                block.Children.Add(Keys(section.Items));
                break;
            case "note":
                foreach (var item in section.Items) block.Children.Add(Note(item));
                break;
            case "visual":
                block.Children.Add(_visuals.Build(section.Items[0]));
                _figures.Add(section.Items[0]);
                if (section.Items.Length > 1)
                {
                    block.Children.Add(Rich(section.Items[1], "TextSecondaryBrush", "FontSizeBodySm"));
                }
                break;
            default:
                foreach (var item in section.Items) block.Children.Add(Rich(item, "TextPrimaryBrush"));
                break;
        }
        foreach (var item in section.Kind == "visual" ? section.Items.Skip(1) : section.Items) Record(item);
        ArticlePanel.Children.Add(block);
    }

    // ------------------------------------------------------------------ blocks

    private T Res<T>(T target, AvaloniaProperty property, string key) where T : AvaloniaObject
    {
        target.Bind(property, this.GetResourceObservable(key));
        return target;
    }

    /// <summary>A wrapped paragraph with **bold** UI names and `key` caps.</summary>
    private TextBlock Rich(string text, string brush, string size = "FontSizeBody")
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        Res(block, TextBlock.FontSizeProperty, size);
        Res(block, TextBlock.ForegroundProperty, brush);
        var inlines = new InlineCollection();
        var bold = false;
        var parts = text.Split("**");
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) bold = !bold;
            var code = parts[i].Split('`');
            for (var j = 0; j < code.Length; j++)
            {
                if (code[j].Length == 0) continue;
                var run = new Run(j % 2 == 1 ? " " + code[j] + " " : code[j])
                {
                    FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
                };
                if (j % 2 == 1)
                {
                    Res(run, TextElement.FontFamilyProperty, "MonoFont");
                    Res(run, TextElement.BackgroundProperty, "InsetBrush");
                    Res(run, TextElement.ForegroundProperty, "TextPrimaryBrush");
                }
                inlines.Add(run);
            }
        }
        block.Inlines = inlines;
        AutomationProperties.SetName(block, Plain(text));
        return block;
    }

    private Control Goal(string text)
    {
        var stack = new StackPanel { Spacing = 3 };
        var label = _visuals.Text("WHEN TO USE THIS", "FontSizeCaption", "AccentTextBrush", FontWeight.SemiBold);
        label.LetterSpacing = 1.1;
        stack.Children.Add(label);
        stack.Children.Add(Rich(text, "TextPrimaryBrush"));
        var box = new Border { Child = stack, Padding = new Thickness(12, 9), BorderThickness = new Thickness(3, 0, 0, 0) };
        Res(box, Border.BackgroundProperty, "PrimaryLightBrush");
        Res(box, Border.BorderBrushProperty, "PrimaryBrush");
        Res(box, Border.CornerRadiusProperty, "RadiusMd");
        return box;
    }

    private Control Step(int number, string text)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var badge = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock
            {
                Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 11,
                FontWeight = FontWeight.Bold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Res(badge, Border.BackgroundProperty, "PrimaryBrush");
        row.Children.Add(badge);
        var body = Rich(text, "TextPrimaryBrush");
        Grid.SetColumn(body, 1);
        row.Children.Add(body);
        return row;
    }

    private Control Bullet(string text)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(6, 7, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        Res(dot, Shape.FillProperty, "TextMutedBrush");
        row.Children.Add(dot);
        var body = Rich(text, "TextPrimaryBrush");
        Grid.SetColumn(body, 1);
        row.Children.Add(body);
        return row;
    }

    private Control Fix(string item)
    {
        var split = item.Split('|', 2);
        var stack = new StackPanel { Spacing = 3 };
        var problem = Rich("**" + split[0] + "**", "TextPrimaryBrush");
        stack.Children.Add(problem);
        if (split.Length > 1) stack.Children.Add(Rich(split[1], "TextSecondaryBrush"));
        var box = new Border { Child = stack, Padding = new Thickness(12, 9), BorderThickness = new Thickness(3, 0, 0, 0) };
        Res(box, Border.BackgroundProperty, "CardElevatedBrush");
        Res(box, Border.BorderBrushProperty, "WarningBrush");
        Res(box, Border.CornerRadiusProperty, "RadiusMd");
        return box;
    }

    private Control Keys(string[] items)
    {
        // One shared key column, so every shortcut table on the page lines up.
        var grid = new Grid { RowSpacing = 6, ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "GuideKeys" });
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        for (var i = 0; i < items.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var split = items[i].Split('|', 2);
            var caps = new WrapPanel { Orientation = Orientation.Horizontal, MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
            var alternatives = split[0].Split(" / ");
            for (var a = 0; a < alternatives.Length; a++)
            {
                if (a > 0) caps.Children.Add(Small(" or "));
                var keys = alternatives[a].Split('+');
                for (var k = 0; k < keys.Length; k++)
                {
                    if (k > 0) caps.Children.Add(Small("+"));
                    caps.Children.Add(Cap(keys[k]));
                }
            }
            AutomationProperties.SetName(caps, split[0]);
            Grid.SetRow(caps, i);
            grid.Children.Add(caps);
            var action = Rich(split.Length > 1 ? split[1] : "", "TextPrimaryBrush");
            action.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(action, i);
            Grid.SetColumn(action, 1);
            grid.Children.Add(action);
        }
        return grid;

        Control Small(string text)
        {
            var t = _visuals.Text(text, "FontSizeCaption", "TextMutedBrush", wrap: TextWrapping.NoWrap);
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(3, 0);
            return t;
        }

        Control Cap(string key)
        {
            var text = _visuals.Text(key, "FontSizeBodySm", "TextPrimaryBrush", FontWeight.SemiBold, TextWrapping.NoWrap);
            Res(text, TextBlock.FontFamilyProperty, "MonoFont");
            var cap = new Border { Child = text, Padding = new Thickness(7, 2), MinWidth = 26, BorderThickness = new Thickness(1, 1, 1, 2) };
            text.HorizontalAlignment = HorizontalAlignment.Center;
            Res(cap, Border.BackgroundProperty, "InsetBrush");
            Res(cap, Border.BorderBrushProperty, "ThemeBorderBrush");
            Res(cap, Border.CornerRadiusProperty, "RadiusSm");
            return cap;
        }
    }

    private Control Note(string text)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var icon = new Path { Width = 14, Height = 14, Stretch = Stretch.Uniform, Margin = new Thickness(0, 3, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        Res(icon, Path.DataProperty, "InfoIconGeometry");
        Res(icon, Shape.FillProperty, "TextSecondaryBrush");
        row.Children.Add(icon);
        var body = Rich(text, "TextSecondaryBrush", "FontSizeBodySm");
        Grid.SetColumn(body, 1);
        row.Children.Add(body);
        var box = new Border { Child = row, Padding = new Thickness(12, 9) };
        Res(box, Border.BackgroundProperty, "InsetBrush");
        Res(box, Border.CornerRadiusProperty, "RadiusMd");
        return box;
    }

    // ------------------------------------------------------------------ footer actions

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
            var path = System.IO.Path.Combine(EnginePaths.FindRepoRoot(), "docs", "research", "ppj2.70067.pdf");
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
            var directory = new DirectoryInfo(System.IO.Path.Combine(EnginePaths.FindRepoRoot(), "docs"));
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
