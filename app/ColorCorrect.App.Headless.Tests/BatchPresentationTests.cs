using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ColorCorrect.App.ViewModels;
using ColorCorrect.App.Views;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class BatchPresentationTests
{
    [AvaloniaFact]
    public void ActiveStageLabelsMeetTextContrastInBothRealizedThemes()
    {
        foreach (var light in new[] { false, true })
        {
            var vm = new MainWindowViewModel(persistenceEnabled: false)
            {
                Busy = true, IsBatching = true, IsLightTheme = light,
                DoFfc = true, DoGc = true, DoWb = true, DoCc = true,
            };
            var window = new MainWindow { Width = 1000, Height = 640, DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                typeof(MainWindowViewModel).GetMethod("BeginPipeline", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(vm, ["apply"]);
                foreach (var step in vm.PipelineSteps) step.IsActive = true;
                Dispatcher.UIThread.RunJobs();
                var chips = window.PipelineChips.GetVisualDescendants().OfType<Border>()
                    .Where(border => border.Classes.Contains("pipelineChip") && border.IsEffectivelyVisible).ToArray();
                Assert.Equal(new[] { "FFC", "GC", "WB", "CC", "Save result" },
                    chips.Select(chip => Assert.Single(chip.GetVisualDescendants().OfType<TextBlock>()).Text));
                foreach (var chip in chips)
                {
                    Assert.Contains("Active", chip.Classes);
                    var label = Assert.Single(chip.GetVisualDescendants().OfType<TextBlock>());
                    var text = Assert.IsAssignableFrom<ISolidColorBrush>(label.Foreground);
                    var background = Assert.IsAssignableFrom<ISolidColorBrush>(chip.Background);
                    // These opaque realized brushes let the calculation measure
                    // the colors actually used by the active style, not tokens.
                    Assert.Equal(255, text.Color.A); Assert.Equal(255, background.Color.A);
                    Assert.Equal(1, text.Opacity); Assert.Equal(1, background.Opacity);
                    Assert.Equal(1, label.Opacity); Assert.Equal(1, chip.Opacity);
                    var ratio = Contrast(text.Color, background.Color);
                    Assert.True(ratio >= 4.5,
                        $"{(light ? "Light" : "Dark")} active {label.Text}: {ratio:F2}:1 contrast ({text.Color} on {background.Color}); expected at least 4.5:1.");
                }
                Assert.Empty(window.LoadedThumbs.GetVisualDescendants().OfType<ProgressBar>());
                var evidence = Environment.GetEnvironmentVariable("CCP_BATCH_UI_EVIDENCE");
                if (!string.IsNullOrWhiteSpace(evidence))
                {
                    Directory.CreateDirectory(evidence);
                    using var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    frame.Save(Path.Combine(evidence, $"batch-active-{(light ? "light" : "dark")}.png"));
                }
            }
            finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
        }
    }

    private static double Contrast(Color foreground, Color background)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color color) =>
            0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        var a = Luminance(foreground); var b = Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    [AvaloniaFact]
    public void BatchControlsRemainUsableAtMinimumSizeInBothThemes()
    {
        foreach (var light in new[] { false, true })
        {
            var vm = new MainWindowViewModel(persistenceEnabled: false)
            {
                Busy = false, IsLightTheme = light, DoFfc = true,
            };
            for (var i = 0; i < 4; i++)
                vm.Session.Add(new SessionItemViewModel($"batch-{i + 1}.jpg")
                {
                    HasProcessingProgress = true, ProcessingProgress = 65,
                    ProcessingStage = "GC + WB + CC", ProcessingStatus = "Color correction running",
                });
            // Direct collection setup bypasses AddPaths' normal UI refresh.
            vm.PrepareSaveMenu();
            Assert.True(vm.HasSession);
            Assert.All(vm.Session, item => Assert.True(item.HasProcessingProgress));
            var window = new MainWindow { Width = 1000, Height = 640, DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                typeof(MainWindow).GetMethod("ShowBatchMenu", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(window, [true]);
                Dispatcher.UIThread.RunJobs();
                var choice = window.FindControl<CheckBox>("BatchPreviewResults")!;
                Assert.True(choice.IsEffectivelyVisible);
                Assert.True(choice.IsChecked);
                Assert.True(choice.Focus());
                // A light-dismiss popup has its own input root; target the focused
                // checkbox rather than delivering the key to the owner window.
                choice.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
                choice.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Space });
                Dispatcher.UIThread.RunJobs();
                Assert.False(vm.BatchPreviewResults);
                Assert.True(window.BatchMenuScroll.Bounds.Width <= window.ClientSize.Width);
                Assert.True(window.BatchMenuScroll.Bounds.Height <= window.ClientSize.Height - 48);
                window.BatchMenu.IsOpen = false;

                vm.Busy = true;
                vm.IsBatching = true;
                vm.ShowBatchProgress = true;
                vm.BatchCurrentImageLabel = "Image 80 of 80 · " + new string('W', 180) + ".jpg";
                typeof(MainWindowViewModel).GetMethod("BeginPipeline", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(vm, ["apply"]);
                Dispatcher.UIThread.RunJobs();
                var heading = window.FindControl<TextBlock>("BatchCurrentImageLabel")!;
                Assert.True(heading.IsEffectivelyVisible);
                Assert.True(heading.Bounds.Width <= window.Sidebar.Bounds.Width);
                foreach (var control in new Control[] { window.RunButton, window.BatchButton, window.BatchProgress })
                {
                    Assert.True(control.IsEffectivelyVisible);
                    var point = control.TranslatePoint(new Point(0, 0), window);
                    Assert.NotNull(point);
                    Assert.True(point.Value.Y >= 0);
                    Assert.True(point.Value.Y + control.Bounds.Height <= window.ClientSize.Height);
                }
                Assert.True(window.RunButton.Bounds.Width >= 80);
                Assert.True(window.BatchButton.Bounds.Height >= 32);
                var chips = window.PipelineChips.GetVisualDescendants().OfType<Border>()
                    .Where(border => border.Classes.Contains("pipelineChip") && border.IsEffectivelyVisible).ToArray();
                Assert.Equal(new[] { "FFC", "GC", "WB", "CC", "Save result" },
                    chips.Select(chip => Assert.Single(chip.GetVisualDescendants().OfType<TextBlock>()).Text));
                Assert.Empty(window.LoadedThumbs.GetVisualDescendants().OfType<ProgressBar>());
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                var evidence = Environment.GetEnvironmentVariable("CCP_BATCH_UI_EVIDENCE");
                if (!string.IsNullOrWhiteSpace(evidence))
                {
                    Directory.CreateDirectory(evidence);
                    frame.Save(Path.Combine(evidence, $"batch-min-{(light ? "light" : "dark")}.png"));
                }
            }
            finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
        }
    }
}
