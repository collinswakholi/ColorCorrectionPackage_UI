using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ColorCorrect.App.Models;
using ColorCorrect.App.ViewModels;
using ColorCorrect.App.Views;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class ThemeRefreshTests
{
    [AvaloniaFact]
    public void ExistingLogRowsChangeThemeWithoutReplacingTheirEntriesOrControls()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false) { IsLightTheme = true };
        vm.LogEntries.Clear();
        foreach (var level in Enum.GetValues<LogLevel>())
        {
            vm.LogEntries.Add(new LogEntry { Message = $"Existing {level}", Level = level });
            vm.LogEntries.Add(new LogEntry { Message = $"Detail {level}", Level = level, IsDetail = true });
        }
        var entries = vm.LogEntries.ToArray();
        var window = new MainWindow { Width = 1200, Height = 900, DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var log = window.FindControl<ItemsControl>("ActivityLog")!;
            var labels = log.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.Classes.Contains("logLine") && text.IsEffectivelyVisible).ToArray();
            Assert.Equal(entries.Length, labels.Length);
            var initial = labels.Select(text => BrushColor(text.Foreground)).ToArray();
            foreach (var light in new[] { false, true, false })
            {
                vm.IsLightTheme = light;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(entries, vm.LogEntries.Take(entries.Length));
                foreach (var label in labels)
                {
                    var entry = Assert.IsType<LogEntry>(label.DataContext);
                    var resource = entry.Level switch
                    {
                        LogLevel.Debug => "LogTimeBrush",
                        LogLevel.Success => "SuccessBrush",
                        LogLevel.Warning => "WarningBrush",
                        LogLevel.Error => "ErrorBrush",
                        _ => "LogTextBrush",
                    };
                    Assert.Equal(ResourceColor(window, resource), BrushColor(label.Foreground));
                    Assert.Contains(label, log.GetVisualDescendants());
                }
                if (!light)
                    Assert.All(labels.Select((text, i) => (text, i)), row =>
                        Assert.NotEqual(initial[row.i], BrushColor(row.text.Foreground)));
            }
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public void ExistingDeltaECardsRefreshBothForegroundAndBackgroundOnThemeChange()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false)
        {
            IsLightTheme = true, HasMetrics = true,
            MeanAfterValue = 0.5, MedianAfterValue = 2.5, MaxAfterValue = 6,
        };
        foreach (var (value, percent) in new[] { (0.5, 50.0), (1.5, -20.0), (2.5, 0.0), (4.0, 50.0), (6.0, -20.0) })
            vm.StageDeltaERows.Add(new StageDeltaERow(new StageDeltaE("CC", 10, value, 10, value, 10, value, percent, 1), "quality"));
        var rows = vm.StageDeltaERows.ToArray();
        var window = new MainWindow { Width = 1440, Height = 1000, DataContext = vm };
        window.Show();
        window.TabDeltaE.IsSelected = true;
        Dispatcher.UIThread.RunJobs();
        try
        {
            var cards = window.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Classes.Contains("stageDeltaCard")).ToArray();
            Assert.Equal(5, cards.Length);
            var summaries = window.FindControl<UniformGrid>("MetricGrid")!.Children.OfType<Border>().Take(3).ToArray();
            Assert.Equal(3, summaries.Length);
            foreach (var light in new[] { false, true, false })
            {
                vm.IsLightTheme = light;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(rows, vm.StageDeltaERows);
                foreach (var card in cards)
                {
                    var row = Assert.IsType<StageDeltaERow>(card.DataContext);
                    AssertCard(window, card, row.MeanAfter);
                    var change = Assert.Single(card.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == row.ImprovementText);
                    var color = Math.Abs(row.ImprovementPercent) < .05 ? "TextSecondaryBrush"
                        : row.ImprovementPercent > 0 ? "SuccessBrush" : "WarningBrush";
                    Assert.Equal(ResourceColor(window, color), BrushColor(change.Foreground));
                }
                foreach (var (card, value) in summaries.Zip(new[] { 0.5, 2.5, 6.0 }))
                    AssertCard(window, card, value);
            }
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    private static void AssertCard(Window window, Border card, double value)
    {
        var grade = value switch { < 1 => "Excellent", < 2 => "Good", < 3.5 => "Fair", < 5 => "Warning", _ => "Poor" };
        Assert.Equal(ResourceColor(window, $"Quality{grade}BgBrush"), BrushColor(card.Background));
        var label = Assert.Single(card.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text is "Excellent" or "Very good" or "Good" or "Fair" or "Poor");
        Assert.Equal(ResourceColor(window, $"Quality{grade}Brush"), BrushColor(label.Foreground));
    }

    private static Color ResourceColor(Window window, string key)
    {
        Assert.True(window.TryFindResource(key, window.ActualThemeVariant, out var resource));
        return BrushColor(Assert.IsAssignableFrom<IBrush>(resource));
    }

    private static Color BrushColor(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
}
