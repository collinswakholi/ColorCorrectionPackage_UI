using Avalonia.Headless.XUnit;
using ColorCorrect.App.ViewModels;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class SessionWorkflowTests
{
    [AvaloniaFact]
    public void LoadWorkingSession_RestoresNeuralNetworkAndPipelineOptions()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        var state = new SessionState
        {
            Options = new CorrectionOptions
            {
                Method = "nn", HiddenLayers = [128, 64, 32], NnOptimizer = "rmsprop",
                NnBatchNormalization = true, NnDropout = 0.2, NnEpochs = 200,
                NnPatience = 50, DoFfc = true, DoGc = true, DoWb = true, DoCc = true,
                WhiteImagePath = "blank-test.jpg", Phase2Teacher = false,
                InputSpace = "adobe-rgb-1998", TargetSpace = "srgb", UseLoadedModel = false,
            },
            Crop = 0.6,
        };
        vm.RestoreWorkingItems(state);
        Assert.Equal("nn", vm.Settings.SelectedMethod.Value);
        Assert.Equal("[128, 64, 32]", vm.Settings.HiddenLayersText);
        Assert.Equal("rmsprop", vm.Settings.SelectedNnOptimizer.Value);
        Assert.True(vm.Settings.NnBatchNormalization);
        Assert.Equal(0.2m, vm.Settings.NnDropout);
        Assert.Equal(200m, vm.Settings.NnEpochs);
        Assert.Equal(50m, vm.Settings.NnPatience);
        Assert.True(vm.Settings.DoFfc && vm.Settings.DoGc && vm.Settings.DoWb && vm.Settings.DoCc);
        Assert.Equal("blank-test.jpg", vm.Settings.WhiteImagePath);
        Assert.False(vm.Compute.Phase2Teacher);
        Assert.False(vm.UseLoadedModel);
        Assert.Equal("adobe-rgb-1998", vm.Settings.SelectedInputSpace.Value);
        Assert.Equal(0.6, vm.Stage.Crop, 6);
        Assert.Equal(vm.Stage.Crop, vm.Stage.OverlayCrop);
    }

    [AvaloniaFact]
    public void LoadWorkingSession_WithoutOptions_PreservesCurrentWorkflow()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false)
        {
            Settings = { HiddenLayersText = "[64, 32]", NnDropout = 0.3m, NnBatchNormalization = true, DoWb = true },
            Stage = { Crop = 0.55 },
        };
        vm.Settings.SelectedMethod = vm.Methods.Single(item => item.Value == "nn");
        vm.Settings.SelectedNnOptimizer = vm.NnOptimizers.Single(item => item.Value == "sgd");
        vm.RestoreWorkingItems(new SessionState());
        Assert.Equal("nn", vm.Settings.SelectedMethod.Value);
        Assert.Equal("[64, 32]", vm.Settings.HiddenLayersText);
        Assert.Equal("sgd", vm.Settings.SelectedNnOptimizer.Value);
        Assert.True(vm.Settings.NnBatchNormalization);
        Assert.Equal(0.3m, vm.Settings.NnDropout);
        Assert.True(vm.Settings.DoWb);
        Assert.Equal(0.55, vm.Stage.Crop, 6);
    }
}
