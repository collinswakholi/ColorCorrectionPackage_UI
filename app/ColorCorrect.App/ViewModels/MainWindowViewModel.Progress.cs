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
        var generation = _batchProgressGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (IsBatching && generation != _batchProgressGeneration) return;
            ApplyEngineProgress(message);
        });
    }

    private void BeginPipeline(string mode) => BeginCapturedPipeline(mode, BuildOptions());

    private void BeginCapturedPipeline(string mode, CorrectionOptions options)
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
            AddStep("ffc", "FFC", options.DoFfc);
            AddStep("gc", "GC", options.DoGc);
            AddStep("wb", "WB", options.DoWb);
            AddStep("cc", "CC", options.DoCc);
            AddStep("write", "Save result", true);
        }
        else
        {
            AddStep("ffc", "FFC", options.DoFfc);
            AddStep("sat", "SAT", !options.SkipSaturated);
            AddStep("gc", "GC", options.DoGc);
            AddStep("wb", "WB", options.DoWb);
            AddStep("cc", "CC", options.DoCc);
            AddStep("write", "Save result", true);
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
        var completed = message.TrimEnd().EndsWith(" complete", StringComparison.OrdinalIgnoreCase);
        CurrentStage = FormatStage(message);
        if (key == "combined" && !completed)
            _combinedProgressKeys = new[] { "gc", "wb", "cc" }.Where(stage => message.Contains(stage, StringComparison.OrdinalIgnoreCase)).ToArray();
        MarkPipeline(key, skipped: message.Contains("skipped", StringComparison.OrdinalIgnoreCase), completed: completed);
        JobProgress = Math.Max(JobProgress, EstimateProgress(key, completed));
        if (IsBatching) UpdateBatchStageProgress();
        if (Busy && !IsSaving)
        {
            var name = IsBatching ? BatchLabel : Active?.Name;
            Status = string.IsNullOrWhiteSpace(name) ? CurrentStage : $"{CurrentStage} · {name}";
        }
    }

    private static string ClassifyProgress(string message)
    {
        var m = message.ToLowerInvariant();
        if (m.StartsWith("combined lut")) return "combined";
        // Apply messages include arbitrary user filenames; those names must not
        // be interpreted as FFC/CC events or completion confirmations.
        if (m.StartsWith("apply") || m.StartsWith("applying")) return "apply";
        if (m.Contains("writ")) return "write";
        if (m.Contains("detect")) return "detect";
        if (m.Contains("ffc")) return "ffc";
        if (m.Contains("sat")) return "sat";
        if (m.StartsWith("gc") || m.Contains("gamma")) return "gc";
        if (m.StartsWith("wb") || m.Contains("white balance")) return "wb";
        if (m.StartsWith("cc") || m.Contains("color correct")) return "cc";
        if (m.Contains("load")) return "load";
        return "";
    }

    private string FormatStage(string message)
    {
        var m = message.Trim();
        if (m.StartsWith("Combined LUT", StringComparison.OrdinalIgnoreCase)) return m.Replace("Combined LUT", "Color correction", StringComparison.OrdinalIgnoreCase);
        if (m.StartsWith("Saturation", StringComparison.OrdinalIgnoreCase)) return "SAT";
        if (m.Equals("CC", StringComparison.OrdinalIgnoreCase)) return IsBatching ? "CC" : CcChipLabel();
        if (m.StartsWith("Writing", StringComparison.OrdinalIgnoreCase)) return "Writing";
        if (m.StartsWith("Loading", StringComparison.OrdinalIgnoreCase)) return "Load";
        if (m.StartsWith("Detecting", StringComparison.OrdinalIgnoreCase)) return "Detect";
        if (m.StartsWith("Applying model", StringComparison.OrdinalIgnoreCase)) return m.Replace("Applying model to", "Apply ·").Replace(" stills", "");
        return m;
    }

    private void MarkPipeline(string key, bool skipped, bool completed)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (key == "combined")
        {
            foreach (var step in PipelineSteps.Where(s => s.IsEnabled))
            {
                if (_combinedProgressKeys.Contains(step.Key))
                {
                    step.IsActive = !completed;
                    step.IsDone = completed;
                }
            }
            return;
        }
        if (key == "apply") return;

        var current = PipelineSteps.FirstOrDefault(s => s.Key == key);
        if (current is not null)
        {
            current.IsActive = current.IsEnabled && !completed && !skipped;
            current.IsDone = current.IsEnabled && completed && !skipped;
            current.IsSkipped = skipped;
        }
    }

    private double EstimateProgress(string key, bool completed = false)
    {
        var enabled = PipelineSteps.Where(s => s.IsEnabled).Select(s => s.Key).ToList();
        if (enabled.Count == 0)
        {
            return Math.Max(JobProgress, 8);
        }

        var idx = key == "combined"
            ? enabled.FindIndex(stage => _combinedProgressKeys.Contains(stage))
            : enabled.IndexOf(key);
        if (idx < 0)
        {
            return Math.Max(JobProgress, 10);
        }

        var end = key == "combined" ? enabled.FindLastIndex(stage => _combinedProgressKeys.Contains(stage)) + 1 : idx + 1;
        return Math.Clamp(8 + (completed ? end : idx + 0.15) * 88.0 / enabled.Count, 8, 96);
    }

    private bool CanEditSession() => !Busy && !IsSaving;

    [RelayCommand(CanExecute = nameof(CanEditSession))]
    private async Task SaveSessionAsAsync()
    {
        if (Dialogs is null || !CanEditSession())
        {
            return;
        }

        var path = await Dialogs.SaveJsonAsync("Save session", "ccp-session.json");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var state = BuildSessionState(includeItems: true);
        IsSaving = true;
        Status = "Saving session…";
        bool saved;
        try { saved = await Task.Run(() => SessionStore.Write(path, state)); }
        finally { IsSaving = false; }
        if (!saved)
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

    [RelayCommand(CanExecute = nameof(CanEditSession))]
    private async Task LoadSessionAsync()
    {
        if (Dialogs is null || !CanEditSession())
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

        IsSaving = true;
        Status = "Loading session…";
        SessionState state;
        string? error;
        try { (state, error) = await Task.Run(() => SessionStore.TryLoadFrom(path)); }
        finally { IsSaving = false; }
        if (!string.IsNullOrWhiteSpace(error))
        {
            Status = "Could not load session.";
            Log("Session", error, LogLevel.Error);
            ToastService.ShowWarning(Status);
            return;
        }

        RestoreWorkingItems(state);
        var missing = Session.Count(item => item.IsMissing);
        Status = $"Session loaded · {Session.Count} image(s)" + (missing > 0 ? $" · {missing} unavailable — select an item and Relink image" : "");
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
