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
    public void CpuInferenceSelectionDoesNotForceCpuCorrectionAndBatchReasonsAreVisible()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        vm.SelectedAccelPolicy = vm.AccelPolicies.Single(mode => mode.Value == "cpu-inference");
        Assert.False(vm.ForceCpu);
        Assert.Contains("CPU", vm.DetectionStatus);
        Assert.Contains("not checked", vm.DetectionStatus);
        Assert.Contains("Load or fit a model", vm.BatchApplyHint);
        vm.RememberJobAccel(null, null, "cpu");
        Assert.Equal("Detection: CPU", vm.DetectionStatus);
    }

    [AvaloniaFact]
    public void SetupChecksUseSelectedModeAndCustomComponents()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        var configure = typeof(MainWindowViewModel).GetMethod("AddSetupSelection", BindingFlags.NonPublic | BindingFlags.Instance)!;
        vm.SelectedSetupMode = vm.SetupModes.Single(mode => mode.Value == "cpu");
        var cpu = new System.Diagnostics.ProcessStartInfo();
        configure.Invoke(vm, [cpu]);
        Assert.Equal(OperatingSystem.IsWindows() ? new[] { "-Mode", "cpu" } : new[] { "--mode", "cpu" }, cpu.ArgumentList);
        vm.SelectedSetupMode = vm.SetupModes.Single(mode => mode.Value == "custom");
        vm.SetupWebGpu = true; vm.SetupDirectMl = false;
        var custom = new System.Diagnostics.ProcessStartInfo();
        configure.Invoke(vm, [custom]);
        Assert.Equal(OperatingSystem.IsWindows() ? new[] { "-Mode", "custom", "-Components", "webgpu" }
            : new[] { "--mode", "custom", "--components", "webgpu" }, custom.ArgumentList);
    }

    [AvaloniaFact]
    public void DetectionFailureKeysChangeWithContentCropAndBackend()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
        try
        {
            File.WriteAllText(file, "aaaa");
            // This key test replaces file bytes; keep thumbnail decoding on a
            // separate missing path so its background read cannot lock the fixture.
            using var item = new SessionItemViewModel(file + ".missing");
            typeof(SessionItemViewModel).GetProperty(nameof(SessionItemViewModel.Path))!.SetValue(item, file);
            var key = typeof(MainWindowViewModel).GetMethod("DetectionFailureKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var first = key.Invoke(vm, [item]);
            var stamp = File.GetLastWriteTimeUtc(file);
            File.WriteAllText(file, "bbbb"); File.SetLastWriteTimeUtc(file, stamp);
            var replaced = key.Invoke(vm, [item]);
            Assert.NotEqual(first, replaced);
            vm.Crop = .5;
            var cropped = key.Invoke(vm, [item]);
            Assert.NotEqual(replaced, cropped);
            vm.SelectedAccelPolicy = vm.AccelPolicies.Single(mode => mode.Value == "cpu-inference");
            Assert.NotEqual(cropped, key.Invoke(vm, [item]));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void SetupSummaryKeepsFailureAndUncheckedReasons()
    {
        using var report = JsonDocument.Parse("""{"inventory_status":"not_checked","probes":[{"policy":"cpu-inference","status":"verified"},{"policy":"webgpu","status":"failed","reason":"Driver unavailable"}]}""");
        var text = MainWindowViewModel.FormatSetupReport(report.RootElement);
        Assert.Contains("not_checked", text);
        Assert.Contains("CPU inference: verified", text);
        Assert.Contains("Driver unavailable", text);
    }

    [Fact]
    public void SetupSummaryDistinguishesInferenceSuccessFromCorrectionRecovery()
    {
        using var report = JsonDocument.Parse("""{"probes":[{"policy":"webgpu","status":"verified","result":{"inference":{"warmed":true},"correction":{"status":"verified","backend":"cpu","fallback":"Adapter initialization failed"}}},{"policy":"cpu-inference","status":"verified","output":"{\"inference\":{\"warmed\":true},\"correction\":{\"status\":\"verified\",\"backend\":\"cpu\",\"fallback\":null}}\n{\"ok\":true}"}]}""");
        var text = MainWindowViewModel.FormatSetupReport(report.RootElement);
        Assert.Contains("WebGPU inference: verified", text);
        Assert.Contains("Correction: CPU · verified · CPU recovery: Adapter initialization failed", text);
        Assert.Contains("CPU inference: verified", text);
    }

    [Fact]
    public void SetupSummaryDoesNotPromoteAFailedProbeFromAWarmSession()
    {
        using var report = JsonDocument.Parse("""{"probes":[{"policy":"cpu-inference","status":"failed","exit_code":3,"reason":"Worker exited with code 3.","result":{"inference":{"warmed":true},"correction":{"status":"verified","backend":"cpu"}}}]}""");
        var text = MainWindowViewModel.FormatSetupReport(report.RootElement);
        Assert.Contains("CPU inference: failed", text);
        Assert.Contains("Worker exited with code 3", text);
        Assert.Contains("Correction: CPU · not verified", text);
        Assert.DoesNotContain("CPU inference: verified", text);
    }

    [Fact]
    public void SetupSummaryIncludesInferenceFailureInsideLinuxProbeOutput()
    {
        using var report = JsonDocument.Parse("""{"probes":[{"policy":"webgpu","status":"failed","output":"{\"inference\":{\"warmed\":false,\"error\":\"Vulkan driver unavailable. Use CPU or check GPU setup.\"},\"correction\":{\"status\":\"verified\",\"backend\":\"cpu\"}}\n{\"ok\":true}"}]}""");
        var text = MainWindowViewModel.FormatSetupReport(report.RootElement);
        Assert.Contains("WebGPU inference: failed", text);
        Assert.Contains("Vulkan driver unavailable. Use CPU or check GPU setup.", text);
        Assert.DoesNotContain("WebGPU inference: verified", text);
    }

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
        Assert.Equal(OperatingSystem.IsWindows() ? new[] { "Auto (recommended)", "CPU", "WebGPU", "DirectML", "Vulkan" }
            : new[] { "Auto (recommended)", "CPU", "WebGPU", "Vulkan" }, vm.AccelPolicies.Select(x => x.Label));
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
