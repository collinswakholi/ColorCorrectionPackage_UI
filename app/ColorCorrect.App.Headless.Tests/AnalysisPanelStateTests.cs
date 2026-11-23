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
            Assert.StartsWith("Add a chart still", vm.Analysis.DeltaEPlaceholder);
            Assert.EndsWith("per-stage ΔE.", vm.Analysis.DeltaEPlaceholder);

            vm.HasSession = true;
            Assert.StartsWith("Detect the chart", vm.Analysis.ColorPlaceholder);
            Assert.EndsWith("the color scatter.", vm.Analysis.ColorPlaceholder);

            vm.Analysis.IsApplying = true;
            Assert.StartsWith("Fitting…", vm.Analysis.RenditionPlaceholder);

            vm.Analysis.IsApplying = false;
            vm.Analysis.Failure = "No chart found.";
            Assert.Equal("The last fit failed: No chart found. See the Log tab for details.", vm.Analysis.DeltaEPlaceholder);

            vm.Analysis.Failure = "";
            Assert.StartsWith("Detect the chart", vm.Analysis.DeltaEPlaceholder);
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
            ((INotifyPropertyChanged)vm.Analysis).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            vm.Analysis.IsApplying = true;
            vm.Analysis.Failure = "x";
            Assert.Contains(nameof(AnalysisViewModel.DeltaEPlaceholder), changed);
            Assert.Contains(nameof(AnalysisViewModel.ColorPlaceholder), changed);
            Assert.Contains(nameof(AnalysisViewModel.RenditionPlaceholder), changed);
        }
        finally
        {
            await vm.DisposeAsync();
        }
    }
}
