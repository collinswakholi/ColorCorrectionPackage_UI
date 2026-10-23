using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using ColorCorrect.App.ViewModels;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class PortableComputeTests
{
    [AvaloniaFact]
    public void PreviewOnlyOutcomeDoesNotClaimASavedFileAndRetainsCpuRecovery()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        using var item = new SessionItemViewModel("log-preview.png");
        vm.Session.Add(item);
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "corrected.jpg");
        typeof(MainWindowViewModel).GetMethod("ApplyOutcome", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [item, new CorrectionOutcome
            {
                Ok = true, OutputPath = missing, ApplyBackend = "wgpu:dx12:NVIDIA Test+cpu",
            }, true, true]);
        var messages = string.Join("\n", vm.LogEntries.Select(row => row.Message));
        Assert.DoesNotContain(missing, messages);
        Assert.DoesNotContain("Saved ", messages);
        Assert.DoesNotContain("wgpu:", messages);
        Assert.DoesNotContain("plane ", messages);
        Assert.Contains("LUT: GPU with CPU recovery", messages);
    }

    [AvaloniaFact]
    public async Task MissingPreviewModelRecoversWithoutAnEndlessLoadingIndicator()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        using var item = new SessionItemViewModel("missing-preview-test.png")
        {
            IsCorrected = true, ModelPath = "missing-model.json", PreviewDir = "missing-previews",
        };
        vm.Session.Add(item); vm.Active = item; vm.StageName = "GC";
        var method = typeof(MainWindowViewModel).GetMethod("EnsureStagePreviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await Assert.IsAssignableFrom<Task>(method.Invoke(vm, ["GC"]));
        Assert.False(vm.IsStagePreviewLoading);
        Assert.NotEqual("GC", vm.StageName);
        Assert.Contains(vm.LogEntries, row => row.Message.Contains("Could not load GC preview"));
    }

    [AvaloniaFact]
    public void RepeatedInferenceAndLutChangesRemainIndependentAndPersist()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        Assert.True(vm.FastMode);
        Assert.True(new CorrectionOptions().FastMode);
        Assert.Equal(OperatingSystem.IsWindows() ? new[] { "Automatic (recommended)", "WebGPU", "DirectML", "Vulkan" }
            : new[] { "Automatic (recommended)", "WebGPU", "Vulkan" }, vm.AccelPolicies.Select(x => x.Label));
        var build = typeof(MainWindowViewModel).GetMethod("BuildOptions", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (var cycle = 0; cycle < 100; cycle++)
        {
            foreach (var mode in vm.AccelPolicies)
            {
                vm.SelectedAccelPolicy = mode;
                vm.ForceCpu = true;
                vm.FastMode = cycle % 2 == 0;
                Assert.Equal(mode, vm.SelectedAccelPolicy);
                var options = Assert.IsType<CorrectionOptions>(build.Invoke(vm, null));
                Assert.True(options.CpuLut);
                Assert.Equal(cycle % 2 == 0, options.FastMode);
                vm.ForceCpu = false;
                Assert.False(Assert.IsType<CorrectionOptions>(build.Invoke(vm, null)).CpuLut);
                Assert.Equal(mode, vm.SelectedAccelPolicy);
            }
        }
        vm.RestoreWorkingItems(new SessionState { Options = new CorrectionOptions { CpuLut = true, FastMode = true } });
        Assert.True(vm.ForceCpu);
        Assert.True(vm.FastMode);
        vm.RestoreWorkingItems(new SessionState { Options = new CorrectionOptions { FastMode = false } });
        Assert.False(vm.FastMode); // An explicit saved preference remains respected.
        vm.RestoreWorkingItems(new SessionState { Options = new CorrectionOptions { LutDevice = "gpu" } });
        Assert.Equal("gpu", vm.SelectedProcessingDevice.Value);
        Assert.False(vm.ForceCpu);
        Assert.True(vm.FastMode);
    }

    [Theory]
    [InlineData("no detect execution provider; vulkan: vulkan GPU-only inference unavailable: E:\\build\\src.cc:137 internal error", "Vulkan inference could not start.")]
    [InlineData("No hardware Backends(VULKAN) adapter available", "No compatible GPU is available.")]
    [InlineData("missing reference cell r1c1", "missing reference cell r1c1")]
    public void ProcessingErrorsKeepActionableInformationWithoutRuntimeStackNoise(string raw, string expected)
    {
        var message = MainWindowViewModel.ConciseProcessingError(raw);
        Assert.StartsWith(expected, message);
        Assert.DoesNotContain("E:\\build", message);
    }

    [AvaloniaFact]
    public void SuccessfulDetectionUpdatesObservedProviderStatus()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        using var item = new SessionItemViewModel("provider-status-test.png");
        vm.Session.Add(item);
        var outcome = new DetectOutcome(true,
            """{"device":"directml","chart_type":"classic","n_charts":1,"charts":[]}""",
            "", "classic", 1, "");
        typeof(MainWindowViewModel).GetMethod("ApplyDetect", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [item, outcome, true]);
        using var report = JsonDocument.Parse("""
            {"backends":[{"kind":"OrtDirectMl","status":"planned","used_for":"detect,plane"}]}
            """);
        var rows = Assert.IsType<List<AccelRow>>(typeof(MainWindowViewModel)
            .GetMethod("ParseAccel", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [report.RootElement]));
        Assert.Equal("In use", rows.Single(row => row.Name == "DirectML").Status);
    }

    [AvaloniaFact]
    public void AdapterIsMarkedInUseOnlyAfterActualWgpuExecution()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        using var report = JsonDocument.Parse("""
            {"assignment":{"detect":"cpu","plane":"cpu","apply":"wgpu"},
             "cpu":{"brand":"Test CPU"},
             "gpus":[{"name":"NVIDIA Test","vendor":"NVIDIA"},{"name":"Intel Test","vendor":"Intel"}]}
            """);
        var parse = typeof(MainWindowViewModel).GetMethod("ParseAccel", BindingFlags.Instance | BindingFlags.NonPublic)!;
        List<AccelRow> Rows() => Assert.IsType<List<AccelRow>>(parse.Invoke(vm, [report.RootElement]));
        Assert.Equal("Seen", Rows().Single(r => r.Name == "NVIDIA Test").Status);
        vm.RememberJobAccel("cpu", "wgpu:dx12:NVIDIA Test+cpu", "cpu");
        var rows = Rows();
        Assert.Equal("In use", rows.Single(r => r.Name == "NVIDIA Test").Status);
        Assert.Contains("CPU recovery", rows.Single(r => r.Name == "NVIDIA Test").Detail);
        Assert.Equal("Seen", rows.Single(r => r.Name == "Intel Test").Status);
        vm.RememberJobAccel("cpu", "cpu", "cpu");
        Assert.Equal("Seen", Rows().Single(r => r.Name == "NVIDIA Test").Status);
    }
}
