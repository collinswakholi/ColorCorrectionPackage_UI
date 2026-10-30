using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ColorCorrect.App.ViewModels;

public partial class MainWindowViewModel
{
    public bool ShowFirstRunSetup { get; private set; }
    public IReadOnlyList<NamedValue> SetupModes { get; } = new NamedValue[] {
        new("recommended", "Recommended"), new("cpu", "CPU only"), new("custom", "Custom")
    };
    [ObservableProperty] private NamedValue _selectedSetupMode = new("recommended", "Recommended");
    [ObservableProperty] private bool _setupWebGpu = true;
    [ObservableProperty] private bool _setupDirectMl = true;
    public bool IsCustomSetup => SelectedSetupMode.Value == "custom";
    public bool IsWindowsSetup => OperatingSystem.IsWindows();
    partial void OnSelectedSetupModeChanged(NamedValue value) => OnPropertyChanged(nameof(IsCustomSetup));
    [ObservableProperty] private string _setupSummary = "";
    public bool HasSetupSummary => !string.IsNullOrWhiteSpace(SetupSummary);
    private readonly HashSet<SessionItemViewModel> _batchSubscriptions = [];

    partial void OnSetupSummaryChanged(string value) => OnPropertyChanged(nameof(HasSetupSummary));

    private void RefreshBatchSubscriptions()
    {
        foreach (var item in _batchSubscriptions.Where(item => !Session.Contains(item)).ToArray())
        {
            item.PropertyChanged -= OnBatchItemChanged;
            _batchSubscriptions.Remove(item);
        }
        foreach (var item in Session)
            if (_batchSubscriptions.Add(item)) item.PropertyChanged += OnBatchItemChanged;
        NotifyRunCommands();
    }

    private void OnBatchItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionItemViewModel.IncludeInBatch)) NotifyRunCommands();
    }

    private void ApplyInstallationDefaults()
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(ColorCorrect.Engine.InstalledSetup.StatePath));
            if (json.RootElement.ValueKind != JsonValueKind.Object) return;
            if (json.RootElement.TryGetProperty("detection_policy", out var detection)
                && detection.GetString() == "cpu-inference") SelectedAccelPolicy = Pick(AccelPolicies, "cpu-inference");
            if (json.RootElement.TryGetProperty("correction_policy", out var correction)
                && correction.GetString() == "cpu") SelectedProcessingDevice = ProcessingDevices[1];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { /* Absent or incomplete setup preferences use the application's defaults. */ }
    }

    private void LoadSetupSelection()
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(ColorCorrect.Engine.InstalledSetup.StatePath));
            if (json.RootElement.ValueKind != JsonValueKind.Object) return;
            if (json.RootElement.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String)
                SelectedSetupMode = Pick(SetupModes, mode.GetString() ?? "recommended");
            if (json.RootElement.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Array)
            {
                var values = components.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()).ToArray();
                SetupWebGpu = values.Contains("webgpu");
                SetupDirectMl = values.Contains("directml");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private void AddSetupSelection(ProcessStartInfo start)
    {
        start.ArgumentList.Add(OperatingSystem.IsWindows() ? "-Mode" : "--mode");
        start.ArgumentList.Add(SelectedSetupMode.Value);
        if (!IsCustomSetup) return;
        var components = new List<string>();
        if (SetupWebGpu) components.Add("webgpu");
        if (OperatingSystem.IsWindows() && SetupDirectMl) components.Add("directml");
        if (components.Count == 0) return;
        start.ArgumentList.Add(OperatingSystem.IsWindows() ? "-Components" : "--components");
        start.ArgumentList.Add(string.Join(",", components));
    }

    private string SetupHelperPath => Path.Combine(Path.GetDirectoryName(_worker.ExePath) ?? AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "setup.ps1" : "setup");

    [RelayCommand(CanExecute = nameof(CanRestartWorker))]
    private async Task CheckSetupAsync()
    {
        var helper = SetupHelperPath;
        if (!File.Exists(helper))
        {
            SetupSummary = "Setup tools are missing. Repair using the original installer or portable archive.";
            return;
        }
        var cancel = BeginJob();
        Busy = true;
        Status = "Checking setup and devices…";
        try
        {
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(helper)!,
            };
            if (OperatingSystem.IsWindows())
                foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", helper, "-Diagnose", "-Verify" }) start.ArgumentList.Add(arg);
            else
                foreach (var arg in new[] { helper, "--diagnose", "--verify" }) start.ArgumentList.Add(arg);
            AddSetupSelection(start);
            using var process = Process.Start(start) ?? throw new IOException("Setup check could not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(cancel).WaitAsync(TimeSpan.FromMinutes(7), cancel); }
            catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } throw; }
            var raw = await output;
            var stderr = await errors;
            if (process.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(stderr) ? "Setup check failed. Repair using the original package." : stderr);
            using var report = JsonDocument.Parse(raw);
            SetupSummary = FormatSetupReport(report.RootElement);
            _detectionFailures.Clear();
            _engineGeneration++;
            await RefreshAccelAsync();
            Status = "Setup check complete.";
        }
        catch (OperationCanceledException) { Status = "Setup check cancelled."; }
        catch (Exception ex) { SetupSummary = ConciseProcessingError(ex.Message); Status = "Setup needs attention."; }
        finally { Busy = false; NotifyRunCommands(); }
    }

    internal static string FormatSetupReport(JsonElement report)
    {
        var lines = new List<string>();
        if (report.TryGetProperty("inventory_status", out var inventory)) lines.Add($"Device inventory: {inventory.GetString()}");
        if (report.TryGetProperty("probes", out var probes) && probes.ValueKind == JsonValueKind.Array)
            foreach (var probe in probes.EnumerateArray())
            {
                var name = probe.TryGetProperty("policy", out var policy) ? LabelStage(policy.GetString() ?? "") : "Runtime";
                var status = probe.TryGetProperty("status", out var state) ? state.GetString() : "not checked";
                var reason = probe.TryGetProperty("reason", out var detail) ? detail.GetString() : null;
                JsonElement? execution = probe.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object ? result : null;
                if (execution is null && probe.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.String)
                    foreach (var line in (output.GetString() ?? "").Split('\n'))
                        try
                        {
                            using var json = JsonDocument.Parse(line);
                            if (json.RootElement.TryGetProperty("correction", out _)) execution = json.RootElement.Clone();
                        }
                        catch (JsonException) { }
                if (execution is { } run && run.TryGetProperty("inference", out var inference)
                    && inference.ValueKind == JsonValueKind.Object)
                {
                    // A warm session cannot override a failed probe or unclean worker exit.
                    if (inference.TryGetProperty("warmed", out var warmed) && warmed.ValueKind != JsonValueKind.True)
                        status = "failed";
                    if (string.IsNullOrWhiteSpace(reason) && inference.TryGetProperty("error", out var inferenceError)
                        && inferenceError.ValueKind == JsonValueKind.String)
                        reason = inferenceError.GetString();
                }
                lines.Add($"{name} inference: {status}" + (string.IsNullOrWhiteSpace(reason) ? "" : $" — {reason}"));
                if (execution is { } executed && executed.TryGetProperty("correction", out var correction) && correction.ValueKind == JsonValueKind.Object)
                {
                    var backend = correction.TryGetProperty("backend", out var actual) && actual.ValueKind == JsonValueKind.String ? LabelStage(actual.GetString() ?? "") : "not checked";
                    var correctionStatus = correction.TryGetProperty("status", out var checkedState) ? checkedState.GetString() : "not checked";
                    if (status == "failed" && correctionStatus == "verified") correctionStatus = "not verified";
                    var fallback = correction.TryGetProperty("fallback", out var recovery) && recovery.ValueKind == JsonValueKind.String ? recovery.GetString() : null;
                    var error = correction.TryGetProperty("error", out var failed) && failed.ValueKind == JsonValueKind.String ? failed.GetString() : null;
                    lines.Add($"Correction: {backend} · {correctionStatus}"
                        + (string.IsNullOrWhiteSpace(fallback) ? "" : $" · CPU recovery: {ConciseProcessingError(fallback)}")
                        + (string.IsNullOrWhiteSpace(error) ? "" : $" — {ConciseProcessingError(error)}"));
                }
                else lines.Add("Correction: not checked");
            }
        lines.Add("GPU drivers are managed through your operating system or hardware vendor.");
        return string.Join("\n", lines);
    }

    [RelayCommand(CanExecute = nameof(CanRestartWorker))]
    private void InstallSupport()
    {
        var helper = SetupHelperPath;
        if (!File.Exists(helper)) { SetupSummary = "Setup tools are missing. Repair using the original package."; return; }
        try
        {
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "x-terminal-emulator")
            { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(helper)! };
            var args = OperatingSystem.IsWindows()
                ? new[] { "-NoProfile", "-NoExit", "-ExecutionPolicy", "Bypass", "-File", helper, "-Install" }
                : new[] { "-e", "/bin/sh", helper, "--install" };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            AddSetupSelection(start);
            Process.Start(start);
            SetupSummary = "Review the selection in setup. When it finishes, choose Check setup and Apply engine change. Use CPU selects CPU detection and correction.";
        }
        catch (Exception ex)
        {
            SetupSummary = $"Could not open setup: {ConciseProcessingError(ex.Message)}. Run {Path.GetFileName(helper)} from the application folder.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestartWorker))]
    private async Task UseCpuAsync()
    {
        SelectedAccelPolicy = Pick(AccelPolicies, "cpu-inference");
        SelectedProcessingDevice = ProcessingDevices[1];
        await ForceCpuRestartAsync();
    }
}
