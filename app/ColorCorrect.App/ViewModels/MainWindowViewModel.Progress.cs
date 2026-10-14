using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ColorCorrect.App.Models;
using ColorCorrect.App.Services;
using ColorCorrect.Engine;
using CommunityToolkit.Mvvm.Input;

namespace ColorCorrect.App.ViewModels;

public partial class MainWindowViewModel
{
    private static readonly string[] PipelineOrder = ["ffc", "sat", "gc", "wb", "cc", "write"];

    private void HookWorkerProgress()
    {
        _worker.Progress += OnWorkerProgress;
    }

    private void UnhookWorkerProgress()
    {
        _worker.Progress -= OnWorkerProgress;
    }

    private void OnWorkerProgress(string message)
    {
        Dispatcher.UIThread.Post(() => ApplyEngineProgress(message));
    }

    private void BeginPipeline(string mode)
    {
        foreach (var step in PipelineSteps)
        {
            step.Reset();
        }

        PipelineSteps.Clear();
        if (mode is "detect")
        {
            PipelineSteps.Add(new PipelineStepViewModel("detect", "Detect", true));
        }
        else if (mode is "apply")
        {
            AddStep("ffc", "FFC", DoFfc);
            AddStep("gc", "GC", DoGc);
            AddStep("wb", "WB", DoWb);
            AddStep("cc", CcChipLabel(), DoCc);
        }
        else
        {
            AddStep("ffc", "FFC", DoFfc);
            AddStep("sat", "SAT", !SkipSaturated);
            AddStep("gc", "GC", DoGc);
            AddStep("wb", "WB", DoWb);
            AddStep("cc", CcChipLabel(), DoCc);
            AddStep("write", "Write", true);
        }

        ShowPipeline = PipelineSteps.Count > 0;
        ShowJobProgress = true;
        JobProgress = 6;
        CurrentStage = mode switch
        {
            "detect" => "Detect",
            "apply" => "Apply model",
            _ => "Load",
        };
    }

    private void EndPipeline()
    {
        foreach (var step in PipelineSteps.Where(s => s.IsEnabled && !s.IsSkipped))
        {
            step.IsActive = false;
            step.IsDone = true;
        }

        ShowJobProgress = IsBatching || IsSaving;
        if (!IsBatching && !IsSaving)
        {
            JobProgress = 0;
            CurrentStage = "";
            RefreshIdlePipeline();
        }
    }

    private void RefreshIdlePipeline()
    {
        if (Busy || IsBatching || IsSaving)
        {
            return;
        }

        PipelineSteps.Clear();
        AddStep("ffc", "FFC", DoFfc);
        AddStep("sat", "SAT", !SkipSaturated);
        AddStep("gc", "GC", DoGc);
        AddStep("wb", "WB", DoWb);
        AddStep("cc", CcChipLabel(), DoCc);
        ShowPipeline = true;
    }

    private void AddStep(string key, string label, bool enabled) =>
        PipelineSteps.Add(new PipelineStepViewModel(key, label, enabled));

    private string CcChipLabel()
    {
        try
        {
            return $"CC · {BuildOptions().CliModel().ToUpperInvariant()}";
        }
        catch
        {
            return "CC";
        }
    }

    private void ApplyEngineProgress(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (!Busy && !IsSaving && !IsBatching)
        {
            return;
        }

        var key = ClassifyProgress(message);
        CurrentStage = FormatStage(message);
        MarkPipeline(key, skipped: message.Contains("skipped", StringComparison.OrdinalIgnoreCase));
        JobProgress = EstimateProgress(key);
        if (Busy && !IsSaving)
        {
            var name = Active?.Name;
            Status = string.IsNullOrWhiteSpace(name) ? CurrentStage : $"{CurrentStage} · {name}";
        }
    }

    private static string ClassifyProgress(string message)
    {
        var m = message.ToLowerInvariant();
        if (m.Contains("detect")) return "detect";
        if (m.Contains("ffc")) return "ffc";
        if (m.Contains("sat")) return "sat";
        if (m.StartsWith("gc") || m.Contains("gamma")) return "gc";
        if (m.StartsWith("wb") || m.Contains("white balance")) return "wb";
        if (m.StartsWith("cc") || m.Contains("color correct")) return "cc";
        if (m.Contains("writ")) return "write";
        if (m.Contains("apply")) return "apply";
        if (m.Contains("load")) return "load";
        return "";
    }

    private string FormatStage(string message)
    {
        var m = message.Trim();
        if (m.StartsWith("Saturation", StringComparison.OrdinalIgnoreCase)) return "SAT";
        if (m.Equals("CC", StringComparison.OrdinalIgnoreCase)) return CcChipLabel();
        if (m.StartsWith("Writing", StringComparison.OrdinalIgnoreCase)) return "Writing";
        if (m.StartsWith("Loading", StringComparison.OrdinalIgnoreCase)) return "Load";
        if (m.StartsWith("Detecting", StringComparison.OrdinalIgnoreCase)) return "Detect";
        if (m.StartsWith("Applying model", StringComparison.OrdinalIgnoreCase)) return m.Replace("Applying model to", "Apply ·").Replace(" stills", "");
        return m;
    }

    private void MarkPipeline(string key, bool skipped)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (key == "apply")
        {
            foreach (var step in PipelineSteps.Where(s => s.IsEnabled))
            {
                step.IsActive = true;
            }

            return;
        }

        var reached = false;
        foreach (var orderKey in PipelineOrder)
        {
            var step = PipelineSteps.FirstOrDefault(s => s.Key == orderKey);
            if (step is null)
            {
                if (orderKey == key) reached = true;
                continue;
            }

            if (orderKey == key)
            {
                reached = true;
                step.IsActive = step.IsEnabled;
                step.IsDone = false;
                step.IsSkipped = skipped;
                continue;
            }

            if (!reached && step.IsEnabled)
            {
                step.IsActive = false;
                step.IsDone = !step.IsSkipped;
            }
            else if (orderKey != key)
            {
                step.IsActive = false;
            }
        }

        var detect = PipelineSteps.FirstOrDefault(s => s.Key == "detect");
        if (detect is not null && key == "detect")
        {
            detect.IsActive = true;
        }
    }

    private double EstimateProgress(string key)
    {
        if (IsBatching && BatchProgress > 0)
        {
            return Math.Min(99, BatchProgress);
        }

        var enabled = PipelineSteps.Where(s => s.IsEnabled).Select(s => s.Key).ToList();
        if (enabled.Count == 0)
        {
            return Math.Max(JobProgress, 8);
        }

        var idx = enabled.IndexOf(key);
        if (idx < 0)
        {
            return Math.Max(JobProgress, 10);
        }

        return Math.Clamp(10 + (idx + 0.55) * 80.0 / enabled.Count, 8, 96);
    }

    [RelayCommand]
    private async Task SaveSessionAsAsync()
    {
        if (Dialogs is null)
        {
            return;
        }

        var path = await Dialogs.SaveJsonAsync("Save session", "ccp-session.json");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!SessionStore.Write(path, BuildSessionState(includeItems: true)))
        {
            Status = "Session could not be saved. Check folder permissions and free space.";
            Log("Session", Status, LogLevel.Error, [path]);
            ToastService.ShowWarning(Status);
            return;
        }

        Status = $"Session saved · {Path.GetFileName(path)}";
        Log("Session", Status, LogLevel.Success);
        ToastService.ShowSuccess(Status);
    }

    [RelayCommand]
    private async Task LoadSessionAsync()
    {
        if (Dialogs is null)
        {
            return;
        }

        var path = await Dialogs.OpenJsonAsync("Load session");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (Session.Count > 0)
        {
            var ok = await Dialogs.ConfirmAsync(
                "Replace working session?",
                "Loading a session replaces the current working set. Settings stay unless the file includes them.");
            if (!ok)
            {
                return;
            }
        }

        var (state, error) = SessionStore.TryLoadFrom(path);
        if (!string.IsNullOrWhiteSpace(error))
        {
            Status = "Could not load session.";
            Log("Session", error, LogLevel.Error);
            ToastService.ShowWarning(Status);
            return;
        }

        RestoreWorkingItems(state);
        Status = $"Session loaded · {Session.Count} image(s)";
        Log("Session", Status, LogLevel.Success);
        ToastService.ShowSuccess(Status);
    }

    internal void ClearTransientCache()
    {
        try
        {
            var runs = RunPaths.RunsRoot(_worker.RepoRoot);
            if (Directory.Exists(runs))
            {
                foreach (var dir in Directory.EnumerateDirectories(runs))
                {
                    if (string.Equals(Path.GetFileName(dir), "saved", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    TryDeleteDir(dir);
                }
            }

            TryDeleteDir(Path.Combine(SessionStore.AppDataDir(), "video-preview"));
        }
        catch
        {
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch
        {
        }
    }

    private static void CopyOrConvert(string src, string dest, string format)
    {
        var wantExt = format.Equals("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
        var srcExt = Path.GetExtension(src);
        if (string.Equals(srcExt, wantExt, StringComparison.OrdinalIgnoreCase))
        {
            // Lazy full-resolution apply can materialize directly into this
            // destination. Copying that completed file onto itself fails on
            // Windows despite there being no competing process.
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest), comparison))
                File.Copy(src, dest, overwrite: true);
            return;
        }

        // Fast stage previews use the native CCPRGB8 container, not a standard
        // image codec. Decode its pixels directly without reapplying input ICC.
        using var bitmap = PreviewRgb.TryReadHeader(src, out _, out _)
            ? PreviewRgb.TryLoad(src) ?? throw new InvalidDataException("Incomplete RGB stage preview.")
            : new Bitmap(src);
        BitmapExport.Save(bitmap, dest);
    }
}
