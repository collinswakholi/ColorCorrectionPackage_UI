using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ColorCorrect.App.Controls;
using ColorCorrect.App.ViewModels;
using ColorCorrect.App.Views;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

/// <summary>
/// 0.4.0 kept every spinner and progress sheen animating while hidden, which held the idle
/// app at about 30 % of a CPU core. Looping animations now run only on visible elements.
/// </summary>
public sealed class IdleMotionTests
{
    [AvaloniaFact]
    public void HiddenSpinnersDoNotAnimateAndAVisibleOneDoes()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false) { Job = { Busy = false } };
        var window = new MainWindow { Width = 1440, Height = 900, DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var spinners = window.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Where(p => p.Classes.Contains("spinner")).ToList();
        Assert.NotEmpty(spinners);
        Assert.All(spinners, s => Assert.True(VisibleMotion.GetEnabled(s), "spinner style enables VisibleMotion"));
        Assert.DoesNotContain(spinners, s => s.Classes.Contains(VisibleMotion.MotionClass));

        vm.Job.Busy = true;
        Dispatcher.UIThread.RunJobs();
        var spinning = spinners.Where(s => s.Classes.Contains(VisibleMotion.MotionClass)).ToList();
        Assert.NotEmpty(spinning);
        Assert.All(spinning, s => Assert.True(s.IsEffectivelyVisible));

        vm.Job.Busy = false;
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(spinners, s => s.Classes.Contains(VisibleMotion.MotionClass));
        window.Close();
    }
}
