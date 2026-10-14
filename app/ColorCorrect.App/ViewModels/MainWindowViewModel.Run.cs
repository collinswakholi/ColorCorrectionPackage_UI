using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ColorCorrect.App.Models;
using ColorCorrect.App.Services;
using ColorCorrect.Engine;
using CommunityToolkit.Mvvm.Input;

namespace ColorCorrect.App.ViewModels;

public partial class MainWindowViewModel
{
    [RelayCommand(CanExecute = nameof(CanDetect))]
    private Task DetectAsync()
    {
        if (Active is not { IsVideo: false } item || !File.Exists(item.Path) || !WorkerReady)
        {
            return Task.CompletedTask;
        }

        return DetectItemAsync(item);
    }

    private async Task DetectItemAsync(SessionItemViewModel item)
    {
        await _run.WaitAsync().ConfigureAwait(false);
        var seq = ++item.DetectGeneration;
        var cancel = BeginJob();
        await OnUi(() =>
        {
            Busy = true;
            BeginPipeline("detect");
            CurrentStage = "Detect";
            Status = $"Detecting {item.Name}…";
            Log("Detect", $"Detect {item.Name}…", LogLevel.Info, [
                $"Crop inset {Crop:0.00}",
                $"Path {item.Path}",
            ]);
        }).ConfigureAwait(false);
        try
        {
            var result = await _worker.DetectAsync(item.Path, Crop, cancellationToken: cancel).ConfigureAwait(false);
            if (item.DetectGeneration != seq)
            {
                return;
            }

            await OnUi(() => ApplyDetect(item, result)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await OnUi(() =>
            {
                Status = "Detect cancelled.";
                Log("Detect", $"{item.Name}: cancelled.", LogLevel.Warning);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await OnUi(() =>
            {
                item.ApplyHandshake(null);
                item.DetectLabel = ex.Message;
                RefreshDetectSummary();
                Status = "Detect failed.";
                Log("Detect", WithWorkerStderr(ex.Message), LogLevel.Error, WorkerErrorDetails());
                RefreshOverlay();
            }).ConfigureAwait(false);
        }
        finally
        {
            await OnUi(() =>
            {
                Busy = false;
                EndPipeline();
                NotifyRunCommands();
            }).ConfigureAwait(false);
            _run.Release();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (Active is null || !CanApply())
        {
            return;
        }

        if (Active.IsVideo)
        {
            await GradeActiveVideoAsync().ConfigureAwait(false);
            return;
        }

        var fit = !WillUseLoadedModel;
        if (fit && Active.Handshake is not { Ok: true })
        {
            Status = "Detect the chart on this image before fitting.";
            return;
        }

        if (!fit && !File.Exists(LoadedModelPath))
        {
            return;
        }

        if (!await EnsureOutputAllowedAsync())
        {
            Status = "Output folder not confirmed.";
            return;
        }

        await _run.WaitAsync().ConfigureAwait(false);
        var item = Active;
        var seq = ++item.ApplyGeneration;
        var handshake = item.Handshake;
        var options = BuildOptions();
        var face = OverlayFaceChoice;
        var useAll = UseAllFaces;
        var crop = Crop;
        var modelPath = LoadedModelPath;
        var cancel = BeginJob();
        await OnUi(() =>
        {
            Busy = true;
            BeginPipeline(fit ? "fit" : "apply");
            CurrentStage = fit ? "CC" : "Apply model";
            Status = fit
                ? $"Applying {options.Summary()} to {item.Name}…"
                : $"Applying saved model to {item.Name}…";
            Log("Apply",
                fit ? $"Apply {options.Summary()} on {item.Name}." : $"Apply saved model on {item.Name}.",
                LogLevel.Info,
                CorrectionSettingsDetails(handshake, applyOnly: !fit));
        }).ConfigureAwait(false);
        try
        {
            var outcome = fit
                ? await _client.FitAsync(item.Path, handshake!, options, face, useAll, crop, cancel).ConfigureAwait(false)
                : await _client.ApplyModelAsync(item.Path, modelPath, options, cancel).ConfigureAwait(false);
            if (item.ApplyGeneration != seq)
            {
                return;
            }

            await OnUi(() => ApplyOutcome(item, outcome, fitted: fit)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await OnUi(() =>
            {
                Status = "Apply cancelled.";
                Log("Apply", $"{item.Name}: cancelled.", LogLevel.Warning);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await OnUi(() =>
            {
                Status = fit ? "Apply failed." : "Model apply failed.";
                Log("Apply", WithWorkerStderr(ex.Message), LogLevel.Error, WorkerErrorDetails());
            }).ConfigureAwait(false);
        }
        finally
        {
            await OnUi(() =>
            {
                Busy = false;
                EndPipeline();
                NotifyRunCommands();
            }).ConfigureAwait(false);
            _run.Release();
        }
    }

    [RelayCommand(CanExecute = nameof(CanBatchFit))]
    private Task BatchFitAsync() => RunBatchAsync(applyModel: false);

    [RelayCommand(CanExecute = nameof(CanBatchApply))]
    private Task BatchApplyAsync() => RunBatchAsync(applyModel: true);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelBatch()
    {
        _jobCts?.Cancel();
        Status = CurrentStage.Contains("Apply", StringComparison.OrdinalIgnoreCase)
            ? "Cancel waits for this batch to finish…"
            : "Cancel after this image…";
        Log("Batch", Status + " The warm worker stays up.", LogLevel.Warning);
        ToastService.ShowWarning(Status);
    }

    private async Task RunBatchAsync(bool applyModel)
    {
        if (applyModel ? !CanBatchApply() : !CanBatchFit())
        {
            return;
        }

        if (!await EnsureOutputAllowedAsync())
        {
            Status = "Output folder not confirmed.";
            return;
        }

        if (applyModel && !File.Exists(LoadedModelPath))
        {
            Status = "Load or fit a model first.";
            return;
        }

        var items = Session.Where(item => item.IncludeInBatch && !item.IsVideo).ToArray();
        if (items.Length == 0)
        {
            Status = Session.Any(item => !item.IsVideo)
                ? "Select at least one still in Batch. FFC whites and corrected outputs start unchecked."
                : "Batch needs stills. Videos are graded with Apply.";
            ToastService.ShowWarning(Status);
            return;
        }

        await _run.WaitAsync().ConfigureAwait(false);
        var cancel = BeginJob();
        var options = BuildOptions();
        options.WriteOutput = false;
        var modelPath = LoadedModelPath;
        var crop = Crop;
        var useAll = UseAllFaces;
        await OnUi(() =>
        {
            Busy = true;
            IsBatching = true;
            BeginPipeline(applyModel ? "apply" : "fit");
            CurrentStage = applyModel ? "Apply model" : "Fit";
            ShowBatchProgress = true;
            ShowJobProgress = true;
            BatchProgress = 0;
            JobProgress = 0;
            BatchLabel = applyModel ? "Apply current model" : "Fit from chart";
            Status = applyModel ? "Batch apply model…" : "Batch fit from chart…";
            Log("Batch", applyModel
                ? $"Apply current model · {items.Length} image(s) · preview only · Save writes full JPEG"
                : $"Fit from chart · {items.Length} image(s) · preview only · {options.Summary()}",
                LogLevel.Info,
                applyModel
                    ? CorrectionSettingsDetails(applyOnly: true).Concat([$"Model file {modelPath}"]).ToList()
                    : CorrectionSettingsDetails());
        }).ConfigureAwait(false);

        var done = 0;
        var failed = 0;
        try
        {
            if (applyModel)
            {
                await OnUi(() =>
                {
                    BatchLabel = $"Apply · {items.Length} image(s) · memory-safe queue";
                    Status = BatchLabel;
                    CurrentStage = "Apply model";
                }).ConfigureAwait(false);

                IReadOnlyList<CorrectionOutcome> outcomes;
                try
                {
                    outcomes = await _client.ApplyManyAsync(
                        items.Select(item => item.Path).ToArray(),
                        modelPath,
                        options,
                        cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await OnUi(() => Log("Batch", "Apply cancelled before a reply arrived.", LogLevel.Warning))
                        .ConfigureAwait(false);
                    outcomes = [];
                }

                if (cancel.IsCancellationRequested && outcomes.Count > 0)
                {
                    await OnUi(() => Log(
                        "Batch",
                        "Cancel waited for this batch. Keeping the results that already finished.",
                        LogLevel.Warning)).ConfigureAwait(false);
                }
                for (var i = 0; i < items.Length; i++)
                {
                    var item = items[i];
                    var outcome = i < outcomes.Count
                        ? outcomes[i]
                        : new CorrectionOutcome { Ok = false, Error = "apply failed" };
                    await OnUi(() =>
                    {
                        BatchLabel = $"{i + 1} / {items.Length} · {item.Name}";
                        BatchProgress = 100.0 * (i + 1) / Math.Max(1, items.Length);
                        JobProgress = BatchProgress;
                        ApplyOutcome(item, outcome, fitted: false, fromBatch: true);
                    }).ConfigureAwait(false);
                    if (outcome.Ok)
                    {
                        done++;
                    }
                    else
                    {
                        failed++;
                    }
                }
            }
            else
            {
                for (var i = 0; i < items.Length; i++)
                {
                    if (cancel.IsCancellationRequested)
                    {
                        await OnUi(() => Log("Batch", $"Stopped after {done} image(s).")).ConfigureAwait(false);
                        break;
                    }

                    var item = items[i];
                    await OnUi(() =>
                    {
                        BeginPipeline("fit");
                        BatchLabel = $"{i + 1} / {items.Length} · {item.Name}";
                        BatchProgress = 100.0 * i / Math.Max(1, items.Length);
                        JobProgress = BatchProgress;
                        Status = BatchLabel;
                    }).ConfigureAwait(false);

                    try
                    {
                        if (item.Handshake is not { Ok: true })
                        {
                            var detectSeq = ++item.DetectGeneration;
                            var detected = await _worker.DetectAsync(item.Path, crop, cancellationToken: cancel).ConfigureAwait(false);
                            if (item.DetectGeneration != detectSeq)
                            {
                                continue;
                            }

                            await OnUi(() => ApplyDetect(item, detected, quiet: true)).ConfigureAwait(false);
                            if (item.Handshake is not { Ok: true })
                            {
                                failed++;
                                continue;
                            }
                        }

                        var outcome = await _client.FitAsync(
                            item.Path,
                            item.Handshake,
                            options,
                            item.FaceChoice,
                            item.UseAllFaces || useAll,
                            crop,
                            cancel).ConfigureAwait(false);

                        await OnUi(() => ApplyOutcome(item, outcome, fitted: true, fromBatch: true)).ConfigureAwait(false);
                        if (outcome.Ok)
                        {
                            done++;
                        }
                        else
                        {
                            failed++;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        await OnUi(() => Log("Batch", $"{item.Name}: cancelled.", LogLevel.Warning)).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        await OnUi(() => Log("Batch", WithWorkerStderr($"{item.Name}: {ex.Message}"), LogLevel.Error, WorkerErrorDetails())).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            await OnUi(() =>
            {
                BatchProgress = 100;
                JobProgress = 100;
                IsBatching = false;
                Busy = false;
                EndPipeline();
                ShowBatchProgress = false;
                Status = cancel.IsCancellationRequested
                    ? $"Batch cancelled · {done} done · {failed} failed."
                    : $"Batch complete · {done} done · {failed} failed.";
                Log("Batch", Status, failed > 0 || cancel.IsCancellationRequested ? LogLevel.Warning : LogLevel.Success);
                if (failed == 0 && !cancel.IsCancellationRequested)
                {
                    ToastService.ShowSuccess(Status);
                }
                NotifyRunCommands();
                RefreshSessionUi();
            }).ConfigureAwait(false);
            _run.Release();
        }
    }

    private void ApplyDetect(SessionItemViewModel item, DetectOutcome result, bool quiet = false)
    {
        if (result.Ok && !string.IsNullOrWhiteSpace(result.HandshakeJson))
        {
            var handshake = new HandshakeDocument(result.HandshakeJson);
            item.ApplyHandshake(handshake);
            var charts = Math.Max(1, result.ChartCount > 0 ? result.ChartCount : handshake.ChartCount);
            var summary = $"{item.DetectLabel} · {charts} chart(s)";
            if (item == Active)
            {
                DetectSummary = summary;
                RebuildFaces(item);
                RefreshOverlay();
            }

            if (!quiet)
            {
                Status = "Detect complete.";
                ToastService.ShowSuccess(summary);
            }

            Log("Detect", $"Detected {item.DetectLabel} on {item.Name}.", LogLevel.Success,
                DetectDetails(item, result, handshake));
        }
        else if (IsNoChart(result))
        {
            item.ApplyHandshake(null);
            item.DetectLabel = "no chart";
            if (item == Active)
            {
                RebuildFaces(item);
                RefreshOverlay();
                RefreshDetectSummary();
            }

            if (!quiet)
            {
                Status = "No chart found.";
                ToastService.ShowWarning($"{item.Name}: no chart found.");
            }

            Log("Detect", $"{item.Name}: no chart found.", LogLevel.Warning, DetectDetails(item, result, null));
        }
        else
        {
            item.ApplyHandshake(null);
            item.DetectLabel = string.IsNullOrWhiteSpace(result.Error) ? "detect failed" : result.Error;
            if (item == Active)
            {
                RebuildFaces(item);
                RefreshOverlay();
                RefreshDetectSummary();
            }

            if (!quiet)
            {
                Status = "Detect failed.";
            }

            var details = new List<string>(DetectDetails(item, result, null));
            var stderr = WorkerErrorDetails();
            if (stderr is not null)
            {
                details.AddRange(stderr);
            }

            Log("Detect", $"{item.Name}: {(string.IsNullOrWhiteSpace(result.Error) ? "failed" : result.Error)}",
                LogLevel.Error, details);
        }

        ScheduleSave();
        NotifyRunCommands();
    }

    private static bool IsNoChart(DetectOutcome result)
    {
        if (result.Ok)
        {
            return false;
        }

        var error = result.Error ?? "";
        if (error.Contains("LibTorch", StringComparison.OrdinalIgnoreCase)
            || error.Contains("image not found", StringComparison.OrdinalIgnoreCase)
            || error.Contains("requires image", StringComparison.OrdinalIgnoreCase)
            || error.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return result.ChartCount <= 0
            && (string.IsNullOrWhiteSpace(error)
                || error.Contains("no lattice", StringComparison.OrdinalIgnoreCase)
                || error.Contains("no chart", StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyOutcome(SessionItemViewModel item, CorrectionOutcome outcome, bool fitted, bool fromBatch = false)
    {
        if (!outcome.Ok)
        {
            var error = string.IsNullOrWhiteSpace(outcome.Error) ? "correction failed" : outcome.Error.Trim();
            if (error.Length > 280)
            {
                error = error[..280] + "…";
            }

            Status = fitted ? "Apply failed." : "Model apply failed.";
            var details = new List<string>(CorrectionSettingsDetails(item.Handshake, applyOnly: !fitted));
            var stderr = WorkerErrorDetails();
            if (stderr is not null)
            {
                details.AddRange(stderr);
            }

            Log(fitted ? "Apply" : "Model", $"{item.Name}: {error}", LogLevel.Error, details);
            return;
        }

        item.CorrectedPath = outcome.OutputPath;
        item.ReportPath = outcome.ReportPath;
        item.PreviewDir = Directory.Exists(outcome.PreviewDir) ? outcome.PreviewDir : item.PreviewDir;
        item.IsCorrected = true;
        item.IncludeInSave = true;
        item.NotifyOutputs();
        if (fitted)
        {
            item.ModelPath = outcome.ModelPath;
            item.Report = outcome.HasChartMetrics ? outcome.Report : null;
            item.FfcPlane = outcome.Report?.FfcPlane ?? FfcPlane.TryLoad(outcome.ModelPath);
            if (item.FfcPlane is { } fittedPlane)
            {
                _loadedModelPlane = fittedPlane;
                EnsureWhiteInSession(activate: false);
                foreach (var sessionItem in Session)
                {
                    if (PathsEqual(sessionItem.Path, WhiteImagePath))
                    {
                        sessionItem.FfcPlane = fittedPlane;
                    }
                }
            }
            if (File.Exists(outcome.ModelPath))
            {
                LoadedModelPath = outcome.ModelPath;
                _loadedModelPlane = item.FfcPlane ?? FfcPlane.TryLoad(outcome.ModelPath);
                ModelIoStatus = $"Fitted {Path.GetFileName(outcome.ModelPath)}";
            }
        }
        else
        {
            item.Report = null;
            item.ReportPath = "";
            item.FfcPlane ??= _loadedModelPlane;
        }

        if (item == Active)
        {
            if (!string.IsNullOrWhiteSpace(outcome.PreviewDir))
            {
                LastPreviewDir = outcome.PreviewDir;
            }

            ApplyReport(item.Report);
            RefreshOverlay();
            AnalysisTab = 1;
            _ = LoadCorrectedPreviewAsync(item);
            if (!string.IsNullOrWhiteSpace(LastPreviewDir))
            {
                StageName = "Corrected";
                ApplyStagePreview();
                NotifyStageRadios();
            }
        }

        var detail = fitted && item.Report?.HasMetrics == true
            ? $"{item.Name} · {BuildOptions().Summary()} · {item.Report.StatusLine()}"
            : fitted
                ? $"{item.Name} · {BuildOptions().Summary()}"
                : $"{item.Name} · apply-only · no chart ΔE";
        if (!fromBatch)
        {
            Status = fitted ? "Apply complete." : "Model applied.";
            ToastService.ShowSuccess(Status);
        }

        RememberJobAccel(outcome.PlaneBackend, outcome.ApplyBackend);
        var extra = new List<string>(CorrectionSettingsDetails(item.Handshake, applyOnly: !fitted));
        extra.AddRange(DeltaEDetails(item.Report));
        if (!string.IsNullOrWhiteSpace(outcome.OutputPath))
        {
            extra.Add($"Output {outcome.OutputPath}");
        }

        if (!string.IsNullOrWhiteSpace(outcome.ApplyBackend) || !string.IsNullOrWhiteSpace(outcome.PlaneBackend))
        {
            extra.Add($"Backends · plane {outcome.PlaneBackend} · apply {outcome.ApplyBackend}");
        }

        if (!string.IsNullOrWhiteSpace(outcome.FfcSkipped))
        {
            extra.Add($"FFC skipped — {outcome.FfcSkipped}");
            Log("FFC", $"{item.Name}: white was set but FFC failed — {outcome.FfcSkipped}", LogLevel.Warning);
            if (!fromBatch)
            {
                ToastService.ShowWarning($"FFC skipped · {item.Name}");
            }
        }

        Log(fitted ? "Apply" : "Model", detail, LogLevel.Success, extra);
        RefreshSessionUi();
        ScheduleSave();
    }
}
