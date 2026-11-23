using System.ComponentModel;
using Avalonia.Headless.XUnit;
using ColorCorrect.App.ViewModels;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

/// <summary>Settings live on vm.Settings; the shell still reacts exactly as before.</summary>
public sealed class CorrectionSettingsViewModelTests
{
    [AvaloniaFact]
    public async Task ChangingASettingRunsTheShellSideEffectsAndNotifies()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        try
        {
            var shellChanges = new List<string?>();
            ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => shellChanges.Add(e.PropertyName);

            vm.Settings.WhiteImagePath = "";
            vm.Settings.DoFfc = true; // FFC without a white reference is invalid
            Assert.Contains("white reference", vm.ValidationMessage);
            Assert.Contains("DoFfc", shellChanges); // re-raised on the shell under the same name
            Assert.Contains(nameof(MainWindowViewModel.ValidationMessage), shellChanges);

            vm.Settings.DoFfc = false; 
            Assert.False(vm.Settings.DoFfc);
            Assert.DoesNotContain("white reference", vm.ValidationMessage);
        }
        finally
        {
            await vm.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task FfcMethodChangeStillUpdatesTheDerivedToggles()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        try
        {
            var nn = vm.FfcFitMethods.Single(m => m.Value == "nn");
            vm.Settings.SelectedFfcFitMethod = nn;
            Assert.True(vm.Settings.ShowFfcNn);
            Assert.True(vm.Settings.ShowFfcIter);
            vm.Settings.SelectedFfcFitMethod = vm.FfcFitMethods.First(m => m.Value != "nn" && m.Value != "pls");
            Assert.False(vm.Settings.ShowFfcNn);
            Assert.False(vm.Settings.ShowFfcIter);
        }
        finally
        {
            await vm.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public void DefaultsMatchThePreviousShellDefaults()
    {
        var s = new CorrectionSettingsViewModel();
        Assert.False(s.DoFfc);
        Assert.True(s.DoGc && s.DoWb && s.DoCc && s.IncludeCombinations);
        Assert.Equal("poly", s.SelectedFfcFitMethod.Value);
        Assert.Equal((50m, 5m, 3m), (s.FfcBins, s.FfcSmoothWindow, s.FfcDegree));
        Assert.Equal(("linear", "srgb", "d65"), (s.SelectedMethod.Value, s.SelectedInputSpace.Value, s.SelectedInputWhite.Value));
        Assert.Equal("100", s.HiddenLayersText);
        Assert.Equal((0.001m, 10000m, 1500m), (s.NnLr, s.NnEpochs, s.NnPatience));
        Assert.Null(s.SelectedDegree);
    }
}
