using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ColorCorrect.App.ViewModels;
using ColorCorrect.App.Views;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class SettingsResizeTests
{
    [AvaloniaFact]
    public void ResizingCorrectionAndFfcDialogsGrowsTheirViewportAndKeepsDoneReachableWhenSmall()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        var owner = new MainWindow { Width = 1200, Height = 1000, DataContext = vm };
        owner.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            foreach (var dialog in new Window[] { new CcSettingsDialog(), new FfcSettingsDialog() })
            {
                dialog.DataContext = vm;
                dialog.Height = 650;
                dialog.Show(owner);
                Dispatcher.UIThread.RunJobs();
                try
                {
                    var scroll = dialog.FindControl<ScrollViewer>("SettingsScroll")!;
                    var startHeight = scroll.Viewport.Height;
                    var startY = scroll.TranslatePoint(default, dialog)!.Value.Y;
                    dialog.Height = 800;
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(scroll.Viewport.Height >= startHeight + 145,
                        $"Larger {dialog.GetType().Name} retained a capped settings viewport.");
                    Assert.InRange(Math.Abs(scroll.TranslatePoint(default, dialog)!.Value.Y - startY), 0, 1);

                    dialog.Height = 320;
                    Dispatcher.UIThread.RunJobs();
                    Assert.InRange(scroll.Viewport.Height, 100, 260);
                    var done = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Done"));
                    var location = done.TranslatePoint(default, dialog)!.Value;
                    Assert.True(done.IsEffectivelyVisible && done.IsEnabled);
                    Assert.InRange(location.Y, 0, dialog.ClientSize.Height);
                    Assert.True(location.Y + done.Bounds.Height <= dialog.ClientSize.Height + .5);
                    var scrollBottom = scroll.TranslatePoint(new Point(0, scroll.Bounds.Height), dialog)!.Value.Y;
                    Assert.True(scrollBottom <= location.Y);
                    scroll.ScrollToEnd();
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(scroll.Offset.Y <= Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height) + .5);
                }
                finally { dialog.Close(); Dispatcher.UIThread.RunJobs(); }
            }
        }
        finally { owner.Close(); Dispatcher.UIThread.RunJobs(); }
    }
}
