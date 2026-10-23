using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ColorCorrect.App.Controls;
using ColorCorrect.App.ViewModels;
using ColorCorrect.Engine;

namespace ColorCorrect.App.Views;

/// <summary>Edits a private draft; only Assign updates the working session.</summary>
public sealed class ReferenceEditorDialog : Window
{
    private sealed record SavedReferenceChoice(ReferenceProfile Profile)
    {
        public string Label => $"{Profile.Name} · r{Profile.Revision} · {Profile.Chart}";
    }
    private readonly MainWindowViewModel _vm;
    private readonly SessionItemViewModel _item;
    private readonly HandshakeDocument _handshake;
    private readonly ComboBox _face = new();
    private readonly ComboBox _chart = new();
    private readonly ComboBox _space = new();
    private readonly ComboBox _white = new();
    private readonly ComboBox _scale = new();
    private readonly ComboBox _order = new();
    private readonly ComboBox _reusable = new();
    private readonly TextBox _name = new() { Text = "Custom reference" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _meaning = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Grid _cells = new();
    private readonly ChartOverlay _overlay = new() { ShowDim = false, ShowSample = false };
    private readonly List<TextBox[]> _inputs = [];
    private readonly List<ReferenceProfile> _establishedProfiles = [];
    private ReferenceProfile _draft = new();
    private readonly Dictionary<string, (ReferenceProfile Profile, string Name, string[][] Text)> _chartDrafts = [];
    private string? _editingChartId;
    private bool _loading;
    private bool _refreshingReusableProfiles;

    public ReferenceEditorDialog(MainWindowViewModel vm)
    {
        _vm = vm;
        _item = vm.Active ?? throw new InvalidOperationException("Select a chart image first.");
        _handshake = _item.Handshake ?? throw new InvalidOperationException("Detect a chart before entering references.");
        Title = "Chart references";
        Width = 1080; Height = 740; MinWidth = 760; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetAutomationId(this, "referenceEditor");
        if (Application.Current!.TryGetResource("SidebarBrush", ActualThemeVariant, out var brush) && brush is IBrush b) Background = b;

        _face.ItemsSource = _handshake.FaceLabels();
        _chart.ItemsSource = new[] { "classic", "passport", "spyder", "digital-sg" };
        _space.ItemsSource = new[] { "lab", "xyz" }.Concat(ColorSpaces.RgbSpaces.Select(s => s.Id)).ToArray();
        _white.ItemsSource = ColorSpaces.WhitePoints.Select(w => w.Id).ToArray();
        _scale.ItemsSource = new[] { "1", "100", "255", "65535" };
        _order.ItemsSource = new[] { "row-major", "column-major" }; _order.SelectedIndex = 0;
        _order.SelectionChanged += (_, _) => _status.Text = "Input order applies to unlabelled paste/CSV rows; the editable grid is always canonical row-major.";

        var root = new DockPanel { Margin = new Thickness(18) };
        var heading = new TextBlock { Text = "Chart references", FontSize = 20, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var footer = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(new TextBlock { Text = "Assign each edited chart before choosing Done. Switching charts keeps their drafts; Done closes the editor.", TextWrapping = TextWrapping.Wrap });
        footer.Children.Add(_status);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(Button("Use built-in for this chart", UseBuiltIn));
        actions.Children.Add(Button("Done", Close));
        actions.Children.Add(Button("Assign reference", Assign)); footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,240"), ColumnSpacing = 16 };
        var controls = new StackPanel { Spacing = 8 };
        controls.Children.Add(Labelled("Detected chart", _face));
        controls.Children.Add(Labelled("Profile name", _name));
        var formats = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 8, RowSpacing = 8 };
        Add(formats, Labelled("Template", _chart), 0); Add(formats, Labelled("Encoding", _space), 1);
        var white = Labelled("White (2°)", _white); Grid.SetRow(white, 1); Add(formats, white, 0);
        var scale = Labelled("Numeric scale", _scale); Grid.SetRow(scale, 1); Add(formats, scale, 1);
        controls.Children.Add(formats); controls.Children.Add(_meaning);
        var tools = new WrapPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(Button("Copy built-in values", CopyBuiltIn));
        tools.Children.Add(Button("Import CSV / JSON", async () => await ImportFile()));
        tools.Children.Add(Button("Paste values", async () => await Paste()));
        tools.Children.Add(Button("Export JSON", async () => await Export()));
        controls.Children.Add(tools); controls.Children.Add(Labelled("Paste / unlabelled CSV order", _order));
        controls.Children.Add(new TextBlock { Text = "Grid: top row A1 → right; then the next row. Focus a cell to locate it in the photo. Values are entered as three channels.", TextWrapping = TextWrapping.Wrap });
        controls.Children.Add(new ScrollViewer { Content = _cells, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        body.Children.Add(new ScrollViewer { Content = controls, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });

        var photo = new StackPanel { Spacing = 10 };
        photo.Children.Add(new TextBlock { Text = "Photographed chart · canonical orientation", TextWrapping = TextWrapping.Wrap });
        var imageWidth = vm.OriginalPreview?.PixelSize.Width ?? 1;
        var imageHeight = vm.OriginalPreview?.PixelSize.Height ?? 1;
        if (ColorCorrect.App.Services.ImageSizeReader.TryRead(_item.Path, out var fullW, out var fullH))
        { _overlay.OverlayScaleX = imageWidth / (double)fullW; _overlay.OverlayScaleY = imageHeight / (double)fullH; }
        var photoGrid = new Grid { Width = imageWidth, Height = imageHeight };
        photoGrid.Children.Add(new Image { Source = vm.OriginalPreview, Stretch = Stretch.Fill });
        _overlay.Handshake = _handshake; photoGrid.Children.Add(_overlay);
        photo.Children.Add(new Viewbox { Child = photoGrid, Stretch = Stretch.Uniform, Height = 220 });
        photo.Children.Add(new TextBlock { Text = "Lab/XYZ values retain their declared white point. RGB input is an encoded reference, not a sampled display swatch. Canonical Lab is calculated by the processing engine.", TextWrapping = TextWrapping.Wrap });
        _reusable.DisplayMemberBinding = new Avalonia.Data.Binding("Label");
        _reusable.PlaceholderText = "Choose a saved session profile";
        RefreshReusableProfiles();
        _reusable.SelectionChanged += (_, _) => { if (!_refreshingReusableProfiles && _reusable.SelectedItem is SavedReferenceChoice choice) LoadProfile(choice.Profile.Snapshot()); };
        photo.Children.Add(_reusable);
        var photoScroll = new ScrollViewer { Content = photo, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetColumn(photoScroll, 1); body.Children.Add(photoScroll); root.Children.Add(body); Content = root;

        _face.SelectionChanged += (_, _) => SelectFace();
        _chart.SelectionChanged += (_, _) => { if (!_loading) ChangeInterpretation(); };
        _space.SelectionChanged += (_, _) => { if (!_loading) ChangeInterpretation(); };
        _white.SelectionChanged += (_, _) => { if (!_loading) ChangeInterpretation(); };
        _scale.SelectionChanged += (_, _) => { if (!_loading) ChangeInterpretation(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        _face.SelectedIndex = Math.Clamp(_item.FaceChoice, 0, _handshake.ChartCount - 1);
    }

    private string CurrentChartId => _handshake.ChartId(Math.Max(0, _face.SelectedIndex));
    private string DetectedType
    {
        get
        {
            var face = _handshake.ActiveFace(Math.Max(0, _face.SelectedIndex));
            return face.TryGetProperty("cli_chart", out var type) ? type.GetString()! : face.GetProperty("chart_type").GetString()!;
        }
    }
    private void SelectFace()
    {
        if (_editingChartId is not null)
            _chartDrafts[_editingChartId] = (_draft.Snapshot(), _name.Text ?? "", _inputs.Select(row => row.Select(input => input.Text ?? "").ToArray()).ToArray());
        _overlay.FaceChoice = Math.Max(0, _face.SelectedIndex); _overlay.HighlightedPatch = -1;
        _editingChartId = CurrentChartId;
        var detected = ReferenceCatalog.CanonicalChart(DetectedType);
        _loading = true;
        _chart.ItemsSource = detected == "classic" ? new[] { "classic", "passport" } : new[] { detected };
        _loading = false;
        if (_chartDrafts.TryGetValue(CurrentChartId, out var draft))
        {
            LoadProfile(draft.Profile);
            _name.Text = draft.Name;
            for (var i = 0; i < Math.Min(_inputs.Count, draft.Text.Length); i++)
                for (var c = 0; c < 3; c++) _inputs[i][c].Text = draft.Text[i][c];
            return;
        }
        var assignment = _item.ReferenceAssignments.FirstOrDefault(a => a.ChartId == CurrentChartId);
        var profile = assignment is null ? null : _item.ReferenceProfiles.FirstOrDefault(p => p.Id == assignment.ProfileId && p.Revision == assignment.ProfileRevision);
        LoadProfile(profile?.Snapshot() ?? new ReferenceProfile { Chart = ReferenceCatalog.CanonicalChart(DetectedType) });
    }
    private void LoadProfile(ReferenceProfile profile)
    {
        if (!ReferenceCatalog.Compatible(DetectedType, profile.Chart)) { _status.Text = "This profile's template does not match the selected chart."; return; }
        try { if (profile.Values is null || profile.Values.Count > 0) profile.Validate(); }
        catch (ArgumentException e) { _status.Text = e.Message; return; }
        _loading = true; _draft = profile; _name.Text = profile.Name;
        _chart.SelectedItem = profile.Chart; _space.SelectedItem = profile.ColorSpace; _white.SelectedItem = profile.WhitePoint;
        _scale.SelectedItem = profile.Scale.ToString(CultureInfo.InvariantCulture); _loading = false;
        BuildGrid(profile.Values is { Count: > 0 } ? profile.RowMajorValues() : null);
        _status.Text = "Edits are a draft until you assign this reference.";
    }
    private void ChangeInterpretation()
    {
        // Never silently reinterpret populated values. Changing interpretation starts an empty draft.
        _draft.Chart = _chart.SelectedItem as string ?? "classic";
        _draft.ColorSpace = _space.SelectedItem as string ?? "lab";
        _draft.WhitePoint = _white.SelectedItem as string ?? "d50";
        _draft.Scale = double.Parse(_scale.SelectedItem as string ?? "1", CultureInfo.InvariantCulture);
        _draft.Values.Clear(); BuildGrid(null);
        _status.Text = "Interpretation changed. The draft grid was cleared; paste/import values with these settings or copy the built-in Lab table.";
    }
    private void BuildGrid(List<double[]>? values)
    {
        _inputs.Clear(); _cells.Children.Clear(); _cells.RowDefinitions.Clear(); _cells.ColumnDefinitions.Clear();
        var template = ReferenceCatalog.Get(_draft.Chart);
        _cells.MinWidth = template.Columns * 96;
        for (var c = 0; c < template.Columns; c++) _cells.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var r = 0; r < template.Rows; r++) _cells.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _meaning.Text = _draft.ColorSpace == "lab" ? "Channels: L*, a*, b* · Lab scale 1" : _draft.ColorSpace == "xyz" ? "Channels: X, Y, Z · select Y=1 or Y=100 scale" : "Channels: R, G, B · explicit RGB encoding and scale";
        for (var i = 0; i < template.Rows * template.Columns; i++)
        {
            var index = i; var box = new StackPanel { Spacing = 3, Margin = new Thickness(3) };
            box.Children.Add(new TextBlock { Text = $"{template.Coordinate(i)} · {i + 1}", FontWeight = FontWeight.SemiBold, FontSize = 12 });
            var inputs = Enumerable.Range(0, 3).Select(channel => new TextBox { Text = values is null ? "" : values[i][channel].ToString("R", CultureInfo.InvariantCulture), MinWidth = 56, Padding = new Thickness(4), FontSize = 11 }).ToArray();
            foreach (var input in inputs)
            {
                input.GotFocus += (_, _) => { _overlay.HighlightedPatch = index; _status.Text = $"{template.Coordinate(index)} · {template.Names[index]}"; };
                AutomationProperties.SetName(input, $"{template.Coordinate(i)} channel {Array.IndexOf(inputs, input) + 1}");
                box.Children.Add(input);
            }
            var swatch = new Border { Height = 8, Background = values is null ? Brushes.Transparent : PreviewBrush(_draft, values[i]) };
            foreach (var input in inputs)
            {
                input.TextChanged += (_, _) =>
                {
                    var triplet = inputs.Select(t => double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
                    swatch.Background = triplet.All(double.IsFinite) ? PreviewBrush(_draft, triplet) : Brushes.Transparent;
                };
            }
            box.Children.Add(swatch); _inputs.Add(inputs);
            ToolTip.SetTip(box, template.Names[i]); Grid.SetRow(box, i / template.Columns); Grid.SetColumn(box, i % template.Columns); _cells.Children.Add(box);
        }
    }
    private ReferenceProfile ReadDraft()
    {
        var profile = _draft.Snapshot(); profile.Name = _name.Text ?? ""; profile.Order = "row-major";
        profile.Values = _inputs.Select((v, i) => v.Select(t => double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : throw new ArgumentException($"Cell {i + 1} needs three valid numbers.")).ToArray()).ToList();
        profile.Validate(); return profile;
    }
    private void CopyBuiltIn()
    {
        var template = ReferenceCatalog.Get(_chart.SelectedItem as string ?? _draft.Chart);
        LoadProfile(new ReferenceProfile { Name = $"Copy of built-in {template.Chart}", Chart = template.Chart, Values = template.Lab.Select(v => v.ToArray()).ToList() });
    }
    private async Task ImportFile()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import chart reference", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Reference data") { Patterns = ["*.csv", "*.tsv", "*.json"] }] });
            var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is null) return;
            var text = await File.ReadAllTextAsync(path);
            var isJson = Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase);
            var profile = isJson ? ReferenceProfile.FromJson(text) : ImportText(text);
            await ReviewImport(profile, establishIdentity: isJson);
        }
        catch (Exception e) { _status.Text = e.Message; }
    }
    private ReferenceProfile ImportText(string text)
    {
        var source = _draft.Snapshot(); source.Order = _order.SelectedItem as string ?? "row-major";
        source.Name = _name.Text ?? "Custom reference";
        return source.ImportText(text);
    }
    private async Task Paste()
    {
        try
        {
            var clipboard = GetTopLevel(this)?.Clipboard;
            var text = clipboard is null ? null : await clipboard.TryGetTextAsync();
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Copy rows of three values from your spreadsheet first.");
            await ReviewImport(ImportText(text));
        }
        catch (Exception e) { _status.Text = e.Message; }
    }
    private async Task ReviewImport(ReferenceProfile profile, bool establishIdentity = false)
    {
        if (!ReferenceCatalog.Compatible(DetectedType, profile.Chart)) throw new ArgumentException("Imported template does not match this chart.");
        var template = ReferenceCatalog.Get(profile.Chart); var values = profile.RowMajorValues();
        var preview = new Window { Title = "Review reference mapping", Width = 600, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Button("Cancel", () => preview.Close(false))); buttons.Children.Add(Button("Use imported values", () => preview.Close(true)));
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer { Content = new TextBlock { Text = $"{profile.Name} · {profile.ColorSpace} / {profile.WhitePoint} / scale {profile.Scale}\nCanonical row-major mapping:\n\n" + string.Join("\n", values.Select((v, i) => $"{template.Coordinate(i),-4} {template.Names[i],-20} {string.Join(", ", v.Select(n => n.ToString("G10", CultureInfo.InvariantCulture)))}")), FontFamily = FontFamily.Default, TextWrapping = TextWrapping.Wrap } });
        preview.Content = panel;
        if (await preview.ShowDialog<bool>(this)) AcceptImportedProfile(profile, establishIdentity);
    }
    private void AcceptImportedProfile(ReferenceProfile profile, bool establishIdentity)
    {
        // A JSON profile already has an external immutable identity. CSV/paste
        // supplies values for the current private draft, without a new identity.
        if (establishIdentity) RememberEstablishedProfile(profile);
        LoadProfile(profile);
    }
    private async Task Export()
    {
        try
        {
            var profile = ReadDraft();
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export reference profile", SuggestedFileName = "reference.json", DefaultExtension = "json" });
            var path = file?.TryGetLocalPath(); if (path is null) return;
            await ExportProfileToPathAsync(path, profile); _status.Text = "Reference profile exported.";
        }
        catch (Exception e) { _status.Text = e.Message; }
    }
    private async Task ExportProfileToPathAsync(string path, ReferenceProfile draft)
    {
        var profile = ResolveDraftRevision(draft);
        await File.WriteAllTextAsync(path, profile.ToJson());
        // Only a completed write establishes this revision. An unchanged next
        // export reuses it; further edits must receive another revision.
        RememberEstablishedProfile(profile);
        _draft = profile.Snapshot();
    }
    private void Assign()
    {
        try
        {
            var profile = ResolveDraftRevision(ReadDraft());
            if (!ReferenceCatalog.Compatible(DetectedType, profile.Chart)) throw new ArgumentException("Template does not match the selected chart.");
            _vm.AssignReference(_item, CurrentChartId, profile);
            _draft = profile.Snapshot();
            RefreshReusableProfiles();
            _status.Text = $"Assigned {profile.Name} · revision {profile.Revision}. Continue with another chart, or choose Done.";
        }
        catch (Exception e) { _status.Text = e.Message; }
    }
    private void RefreshReusableProfiles()
    {
        _refreshingReusableProfiles = true;
        try
        {
            _reusable.ItemsSource = _vm.Session.SelectMany(item => item.ReferenceProfiles)
                .GroupBy(profile => (profile.Id, profile.Revision)).Select(group => new SavedReferenceChoice(group.First())).ToArray();
            // Refreshing choices must not reload a previously selected profile
            // over the current chart's newly assigned draft.
            _reusable.SelectedItem = null;
        }
        finally { _refreshingReusableProfiles = false; }
    }
    private void UseBuiltIn()
    {
        try
        {
            _vm.AssignReference(_item, CurrentChartId, null);
            _status.Text = "Built-in values assigned to this chart. Its custom draft is unassigned; other chart drafts are kept.";
        }
        catch (Exception e) { _status.Text = e.Message; }
    }
    private ReferenceProfile ResolveDraftRevision(ReferenceProfile profile)
    {
        var known = _vm.Session.SelectMany(i => i.ReferenceProfiles).Concat(_establishedProfiles)
            .Where(p => p.Id == profile.Id).ToArray();
        var existing = known.Where(p => p.Revision == profile.Revision).ToArray();
        if (existing.Length == 0) return profile;
        // The grid displays canonical rows even for column-major imports.
        // Preserve the exact established snapshot when no meaning has changed.
        if (existing.All(p => SameCanonicalProfile(p, profile))) return existing[0].Snapshot();
        var changed = profile.Snapshot();
        changed.Revision = checked(known.Max(p => p.Revision) + 1);
        return changed;
    }
    private void RememberEstablishedProfile(ReferenceProfile profile)
    {
        var json = profile.ToJson();
        if (!_establishedProfiles.Any(p => p.Id == profile.Id && p.Revision == profile.Revision && p.ToJson() == json))
            _establishedProfiles.Add(profile.Snapshot());
    }
    private static bool SameCanonicalProfile(ReferenceProfile left, ReferenceProfile right)
    {
        var a = left.Snapshot(); var b = right.Snapshot();
        a.Values = a.RowMajorValues(); b.Values = b.RowMajorValues();
        a.Order = b.Order = "row-major";
        return a.ToJson() == b.ToJson();
    }
    private Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 6) };
        button.Click += (_, _) => { try { action(); } catch (Exception e) { _status.Text = e.Message; } }; return button;
    }
    private static Control Labelled(string label, Control control)
    {
        var panel = new StackPanel { Spacing = 3 }; panel.Children.Add(new TextBlock { Text = label, FontSize = 12 });
        control.HorizontalAlignment = HorizontalAlignment.Stretch; panel.Children.Add(control); return panel;
    }
    private static void Add(Grid grid, Control control, int column) { Grid.SetColumn(control, column); grid.Children.Add(control); }
    private static IBrush PreviewBrush(ReferenceProfile profile, double[] value)
    {
        var rgb = ReferenceColorPreview.DisplaySrgb(profile, value);
        if (rgb.Any(v => !double.IsFinite(v))) return Brushes.Transparent;
        return new SolidColorBrush(Color.FromRgb((byte)Math.Round(rgb[0] * 255), (byte)Math.Round(rgb[1] * 255), (byte)Math.Round(rgb[2] * 255)));
    }
}
