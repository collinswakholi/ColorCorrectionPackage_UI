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
        Assert.Equal("nn", vm.SelectedMethod.Value);
        Assert.Equal("[128, 64, 32]", vm.HiddenLayersText);
        Assert.Equal("rmsprop", vm.SelectedNnOptimizer.Value);
        Assert.True(vm.NnBatchNormalization);
        Assert.Equal(0.2m, vm.NnDropout);
        Assert.Equal(200m, vm.NnEpochs);
        Assert.Equal(50m, vm.NnPatience);
        Assert.True(vm.DoFfc && vm.DoGc && vm.DoWb && vm.DoCc);
        Assert.Equal("blank-test.jpg", vm.WhiteImagePath);
        Assert.False(vm.Phase2Teacher);
        Assert.False(vm.UseLoadedModel);
        Assert.Equal("adobe-rgb-1998", vm.SelectedInputSpace.Value);
        Assert.Equal(0.6, vm.Crop, 6);
        Assert.Equal(vm.Crop, vm.OverlayCrop);
    }

    [AvaloniaFact]
    public void LoadWorkingSession_WithoutOptions_PreservesCurrentWorkflow()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false)
        {
            HiddenLayersText = "[64, 32]", NnDropout = 0.3m,
            NnBatchNormalization = true, DoWb = true, Crop = 0.55,
        };
        vm.SelectedMethod = vm.Methods.Single(item => item.Value == "nn");
        vm.SelectedNnOptimizer = vm.NnOptimizers.Single(item => item.Value == "sgd");
        vm.RestoreWorkingItems(new SessionState());
        Assert.Equal("nn", vm.SelectedMethod.Value);
        Assert.Equal("[64, 32]", vm.HiddenLayersText);
        Assert.Equal("sgd", vm.SelectedNnOptimizer.Value);
        Assert.True(vm.NnBatchNormalization);
        Assert.Equal(0.3m, vm.NnDropout);
        Assert.True(vm.DoWb);
        Assert.Equal(0.55, vm.Crop, 6);
    }
}
