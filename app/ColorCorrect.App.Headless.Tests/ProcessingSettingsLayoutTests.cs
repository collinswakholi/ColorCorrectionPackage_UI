using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

public sealed class ProcessingSettingsLayoutTests
{
    [AvaloniaFact]
    public void ExpandedProcessingSettingsKeepFullWidthControlsAndScrollLongDeviceDetailsAtMinimumSize()
    {
        foreach (var light in new[] { false, true })
        {
            var vm = new MainWindowViewModel(persistenceEnabled: false)
            {
                IsLightTheme = light, Busy = false, WorkerReady = true,
                WorkerStartupComplete = true, WorkerLine = "Ready", Status = "Ready",
                AccelHint = "Synthetic layout fixture; hardware execution is tested separately.",
            };
            for (var index = 0; index < 6; index++)
                vm.AccelRows.Add(new AccelRow
                {
                    Name = $"Example graphics device {index + 1} with a deliberately long adapter name",
                    Status = "Available",
                    Detail = "Synthetic layout fixture. Hardware execution is verified separately in the guest acceptance runs.",
                });
            var window = new MainWindow { Width = 1000, Height = 640, DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                window.AccelMenu.PlacementTarget = window.TopBar;
                window.AccelMenu.IsOpen = true;
                Dispatcher.UIThread.RunJobs();
                Capture(window.ProcessingSettingsPanel, light, "collapsed");
                Assert.False(window.AdvancedAcceleration.IsExpanded);
                var header = window.AdvancedAcceleration.GetVisualDescendants().OfType<ToggleButton>()
                    .First(toggle => toggle.Name == "ExpandCollapseButton");
                Assert.True(header.Bounds.Width >= 320);
                Assert.True(header.Focus(NavigationMethod.Tab));
                Dispatcher.UIThread.RunJobs();
                var focusBorder = Assert.Single(header.GetVisualDescendants().OfType<Border>(), border => border.Name == "DisclosureBorder");
                Assert.Null(header.FocusAdorner);
                Assert.True(focusBorder.CornerRadius.TopLeft >= 4);
                Assert.True(focusBorder.BorderThickness.Left >= 1);
                Assert.True(window.TryFindResource("BorderFocusBrush", window.ActualThemeVariant, out var focusBrush));
                Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(focusBrush).Color,
                    Assert.IsAssignableFrom<ISolidColorBrush>(focusBorder.BorderBrush).Color);
                PressSpace(header);
                Assert.True(window.AdvancedAcceleration.IsExpanded);
                Assert.True(window.DetectionEngine.Bounds.Width >= 300);
                var apply = Assert.Single(window.AdvancedAcceleration.GetVisualDescendants().OfType<Button>(),
                    button => Equals(button.Content, "Apply engine change"));
                Assert.True(apply.Bounds.Width >= 300);
                Assert.True(apply.Bounds.Height >= 32);
                Capture(window.ProcessingSettingsPanel, light, "expanded");

                var details = window.ProcessingDeviceDetails;
                var detailsHeader = details.GetVisualDescendants().OfType<ToggleButton>()
                    .First(toggle => toggle.Name == "ExpandCollapseButton");
                Assert.True(detailsHeader.Bounds.Width >= 300);
                Assert.True(detailsHeader.Focus());
                PressSpace(detailsHeader);
                Assert.True(details.IsExpanded);
                var scroll = window.ProcessingSettingsScroll;
                Assert.InRange(scroll.Viewport.Height, 100, 440);
                Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
                Assert.True(window.ProcessingSettingsPanel.Bounds.Height <= window.ClientSize.Height - 64);
                Assert.True(window.ProcessingSettingsPanel.Bounds.Width <= window.ClientSize.Width);
                Capture(window.ProcessingSettingsPanel, light, "details");
                scroll.ScrollToEnd();
                Dispatcher.UIThread.RunJobs();
                Assert.True(scroll.Offset.Y > 0);
                Assert.True(scroll.Offset.Y <= scroll.Extent.Height - scroll.Viewport.Height + .5);
                Capture(window.ProcessingSettingsPanel, light, "details-end");
            }
            finally { window.AccelMenu.IsOpen = false; window.Close(); Dispatcher.UIThread.RunJobs(); }
        }
    }

    private static void PressSpace(Control control)
    {
        control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
        control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Space });
        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Control panel, bool light, string state)
    {
        var root = TopLevel.GetTopLevel(panel);
        Assert.NotNull(root);
        using var frame = root.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestResults", "UI"));
        Directory.CreateDirectory(folder);
        frame.Save(Path.Combine(folder, $"processing-{(light ? "light" : "dark")}-{state}.png"));
    }
}
