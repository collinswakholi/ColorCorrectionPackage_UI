using System.ComponentModel;
using Avalonia.Headless.XUnit;
using ColorCorrect.App.ViewModels;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

/// <summary>The ΔE, color and rendition panels must tell empty, running and failed states apart.</summary>
public sealed class AnalysisPanelStateTests
{
    [AvaloniaFact]
    public async Task PlaceholderDistinguishesEmptyReadyRunningAndFailed()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        try
        {
            vm.HasSession = false;
            Assert.StartsWith("Add a chart still", vm.DeltaEPlaceholder);
            Assert.EndsWith("per-stage ΔE.", vm.DeltaEPlaceholder);

            vm.HasSession = true;
            Assert.StartsWith("Detect the chart", vm.ColorPlaceholder);
            Assert.EndsWith("the color scatter.", vm.ColorPlaceholder);

            vm.IsApplying = true;
            Assert.StartsWith("Fitting…", vm.RenditionPlaceholder);

            vm.IsApplying = false;
            vm.AnalysisFailure = "No chart found.";
            Assert.Equal("The last fit failed: No chart found. See the Log tab for details.", vm.DeltaEPlaceholder);

            vm.AnalysisFailure = "";
            Assert.StartsWith("Detect the chart", vm.DeltaEPlaceholder);
        }
        finally
        {
            await vm.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task PlaceholdersRaiseChangeNotifications()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        try
        {
            var changed = new List<string?>();
            ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            vm.IsApplying = true;
            vm.AnalysisFailure = "x";
            Assert.Contains(nameof(MainWindowViewModel.DeltaEPlaceholder), changed);
            Assert.Contains(nameof(MainWindowViewModel.ColorPlaceholder), changed);
            Assert.Contains(nameof(MainWindowViewModel.RenditionPlaceholder), changed);
        }
        finally
        {
            await vm.DisposeAsync();
        }
    }
}
