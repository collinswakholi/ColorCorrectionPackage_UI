using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private readonly Dictionary<string, string> _detectionFailures = new(PathIdentity.Comparer);
    private bool _autoDetectPending;
    private int _engineGeneration;

    private string DetectionFailureKey(SessionItemViewModel item) =>
        $"{AssetIdentity.Capture(item.Path).Sha256}:{Crop:R}:{SelectedAccelPolicy.Value}:{_engineGeneration}";

    private async Task AutoDetectIfNeededAsync(SessionItemViewModel item)
    {
        if (_autoDetectPending) return;
        _autoDetectPending = true;
        try
        {
            if (_detectionFailures.TryGetValue(item.Path, out var failed))
            {
                var key = await Task.Run(() => DetectionFailureKey(item));
                if (_disposed || item != Active || !CanDetect() || item.Handshake is not null) return;
                if (failed == key) return;
            }
            await DetectItemAsync(item, retryInference: false);
        }
        catch (IOException) { /* Missing input is shown by session recovery. */ }
        catch (UnauthorizedAccessException) { /* Explicit Detect reports access errors. */ }
        finally { _autoDetectPending = false; }
    }
    private sealed record BatchFitSnapshot(HandshakeDocument? Handshake, CorrectionOptions? Options,
        int FaceChoice, bool UseAllFaces, int ApplyGeneration, string? Error);

    private static BatchFitSnapshot CaptureBatchFit(SessionItemViewModel item, CorrectionOptions options, bool useAll)
    {
        var generation = ++item.ApplyGeneration;
        try
        {
            return new(item.Handshake, WithItemReferences(options, item), item.FaceChoice,
                item.UseAllFaces || useAll, generation, null);
        }
        catch (Exception ex)
        {
            return new(item.Handshake, null, item.FaceChoice, item.UseAllFaces || useAll, generation, ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDetect))]
    private Task DetectAsync()
    {
        if (Active is not { } item || !File.Exists(item.Path) || !WorkerReady)
        {
            return Task.CompletedTask;
        }

        return DetectItemAsync(item);
    }

    private async Task DetectItemAsync(SessionItemViewModel item, bool retryInference = true)
    {
        await _run.WaitAsync().ConfigureAwait(false);
        var seq = ++item.DetectGeneration;
        var cancel = BeginJob();
        var elapsed = Stopwatch.StartNew();
        await OnUi(() =>
        {
            Busy = true;
            BeginPipeline("detect");
            CurrentStage = "Detect";
            Status = $"Detecting {item.Name}…";
            Log("Detect", $"{item.Name}…");
        }).ConfigureAwait(false);
        try
        {
            var requestKey = await Task.Run(() => DetectionFailureKey(item), cancel).ConfigureAwait(false);
            var result = await _worker.DetectAsync(item.Path, Crop, cancellationToken: cancel, retryInference: retryInference).ConfigureAwait(false);
            if (item.DetectGeneration != seq)
            {
                return;
            }

            await OnUi(() =>
            {
                if (result.Ok) _detectionFailures.Remove(item.Path);
                else _detectionFailures[item.Path] = requestKey;
                ApplyDetect(item, result);
                if (result.Ok) Log("Time", $"Detection {elapsed.Elapsed.TotalSeconds:0.00} s");
            }).ConfigureAwait(false);
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
            string? failedKey = null;
            try { failedKey = await Task.Run(() => DetectionFailureKey(item)).ConfigureAwait(false); }
            catch (Exception capture) when (capture is IOException or UnauthorizedAccessException) { }
            await OnUi(() =>
            {
                if (failedKey is not null) _detectionFailures[item.Path] = failedKey;
                if (!Session.Contains(item)) return;
                if (item == Active) OnPropertyChanged(nameof(ReferenceSummary));
                RefreshDetectSummary();
                Status = "Detection failed. Previous results are kept.";
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
        var item = Active;
        if (item is null || !CanApply())
        {
            return;
        }



        var fit = !WillUseLoadedModel;
        if (fit && item.Handshake is not { Ok: true })
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
        var present = false;
        await OnUi(() => present = Session.Contains(item)).ConfigureAwait(false);
        if (!present)
        {
            _run.Release();
            return;
        }
        var seq = ++item.ApplyGeneration;
        var handshake = item.Handshake;
        var options = BuildOptions();
        var face = OverlayFaceChoice;
        var useAll = UseAllFaces;
        var crop = Crop;
        var modelPath = LoadedModelPath;
        var cancel = BeginJob();
        var elapsed = Stopwatch.StartNew();
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
                ? await _client.FitAsync(item.Path, handshake!, WithItemReferences(options, item), face, useAll, crop, cancel).ConfigureAwait(false)
                : await _client.ApplyModelAsync(item.Path, modelPath, options, cancel).ConfigureAwait(false);
            if (item.ApplyGeneration != seq)
            {
                return;
            }

            await OnUi(() =>
            {
                ApplyOutcome(item, outcome, fitted: fit);
                if (outcome.Ok) Log("Time", $"{(fit ? "Fit and apply" : "Apply")} {elapsed.Elapsed.TotalSeconds:0.00} s");
            }).ConfigureAwait(false);
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
        Status = "Cancelling current operation… Completed results are kept.";
        Log("Processing", Status, LogLevel.Warning);
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

        var items = Session.Where(item => item.IncludeInBatch).ToArray();
        if (items.Length == 0)
        {
            Status = Session.Count > 0
                ? "Select at least one still in Batch. FFC whites and corrected outputs start unchecked."
                : "Add images to the session before running a batch.";
            ToastService.ShowWarning(Status);
            return;
        }

        await _run.WaitAsync().ConfigureAwait(false);
        var cancel = BeginJob();
        var options = CaptureBatchOptions(BuildOptions(), BatchPreviewResults);
        var modelPath = LoadedModelPath;
        var crop = Crop;
        var useAll = UseAllFaces;
        Dictionary<SessionItemViewModel, BatchFitSnapshot> fitSnapshots = [];
        // One batch captures one set of assets, even if files are replaced while later
        // images wait in the queue. Per-image results then own their own replay copies.
        await OnUi(() =>
        {
            Busy = true;
            if (!applyModel)
                fitSnapshots = items.ToDictionary(item => item, item => CaptureBatchFit(item, options, useAll));
            BeginCapturedPipeline(applyModel ? "apply" : "fit", options);
            CurrentStage = "Preparing";
            Status = "Preparing batch assets…";
        }).ConfigureAwait(false);
        try
        {
            (options, modelPath) = await CaptureBatchAssetsAsync(_worker.RepoRoot, options, modelPath, applyModel, cancel).ConfigureAwait(false);
            foreach (var snapshot in fitSnapshots.Values)
                if (snapshot.Options is { DoFfc: true } capturedOptions)
                    capturedOptions.WhiteImagePath = options.WhiteImagePath;
        }
        catch (Exception ex)
        {
            _run.Release();
            await OnUi(() =>
            {
                Busy = false;
                EndPipeline();
                Status = ex is OperationCanceledException ? "Batch cancelled. Previous results are kept." : $"Could not prepare batch assets: {ex.Message}";
                Log("Batch", Status, ex is OperationCanceledException ? LogLevel.Warning : LogLevel.Error);
                NotifyRunCommands();
            }).ConfigureAwait(false);
            return;
        }
        await OnUi(() =>
        {
            Busy = true;
            IsBatching = true;
            BeginCapturedPipeline(applyModel ? "apply" : "fit", options);
            foreach (var queued in items)
            {
                queued.HasProcessingProgress = true; queued.IsProcessing = false;
                queued.ProcessingProgress = 0; queued.ProcessingStage = "Queued"; queued.ProcessingStatus = "Queued";
            }
            BatchProgressSummary = $"0 complete · 0 failed · {items.Length} queued";
            CurrentStage = applyModel ? "Apply model" : "Fit";
            ShowBatchProgress = true;
            ShowJobProgress = true;
            BatchProgress = 0;
            JobProgress = 0;
            BatchLabel = applyModel ? "Apply current model" : "Fit from chart";
            Status = applyModel ? "Batch apply model…" : "Batch fit from chart…";
            Log("Batch", applyModel
                ? $"Apply current model · {items.Length} image(s) · {(options.BatchPreviewResults ? "previews on" : "previews off · full images kept for Save")}"
                : $"Fit from chart · {items.Length} image(s) · {(options.BatchPreviewResults ? "previews on" : "previews off · full images kept for Save")} · {options.Summary()}",
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

                for (var i = 0; i < items.Length; i++)
                {
                    if (cancel.IsCancellationRequested) break;
                    var item = items[i];
                    if (!Session.Contains(item)) continue;
                    var generation = item.ApplyGeneration;
                    await OnUi(() =>
                    {
                        BeginBatchImage(item, i, items.Length, options, applyModel: true);
                    }).ConfigureAwait(false);
                    try
                    {
                        var outcome = await _client.ApplyModelAsync(item.Path, modelPath, options, cancel).ConfigureAwait(false);
                        await OnUi(() =>
                        {
                            if (item.ApplyGeneration == generation)
                                ApplyOutcome(item, outcome, fitted: false, fromBatch: true);
                            FinishBatchImage(item, outcome.Ok);
                        }).ConfigureAwait(false);
                        if (outcome.Ok) done++; else failed++;
                    }
                    catch (OperationCanceledException)
                    {
                        await OnUi(() => FinishBatchImage(item, false, "Cancelled")).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        await OnUi(() => { FinishBatchImage(item, false); Log("Batch", $"{item.Name}: {WithWorkerStderr(ex.Message)}", LogLevel.Error); }).ConfigureAwait(false);
                    }
                    await OnUi(() =>
                    {
                        BatchProgressSummary = $"{done} complete · {failed} failed · {items.Length} total";
                        BatchProgress = 100.0 * (i + 1) / items.Length;
                    }).ConfigureAwait(false);
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
                    var snapshot = fitSnapshots[item];
                    var present = false;
                    await OnUi(() =>
                    {
                        present = Session.Contains(item);
                        if (!present) return;
                        BeginBatchImage(item, i, items.Length, snapshot.Options ?? options, applyModel: false);
                        Status = BatchLabel;
                    }).ConfigureAwait(false);
                    if (!present) continue;

                    try
                    {
                        if (snapshot.Error is not null) throw new InvalidOperationException(snapshot.Error);
                        var handshake = snapshot.Handshake;
                        if (handshake is not { Ok: true })
                        {
                            var detectSeq = ++item.DetectGeneration;
                            var detected = await _worker.DetectAsync(item.Path, crop, cancellationToken: cancel).ConfigureAwait(false);
                            if (item.DetectGeneration != detectSeq)
                            {
                                continue;
                            }

                            await OnUi(() =>
                            {
                                present = Session.Contains(item);
                                if (!present) return;
                                ApplyDetect(item, detected, quiet: true);
                                handshake = item.Handshake;
                            }).ConfigureAwait(false);
                            if (!present) continue;
                            if (handshake is not { Ok: true })
                            {
                                failed++;
                                await OnUi(() => FinishBatchImage(item, false)).ConfigureAwait(false);
                                continue;
                            }
                        }

                        var outcome = await _client.FitAsync(
                            item.Path,
                            handshake,
                            snapshot.Options!,
                            snapshot.FaceChoice,
                            snapshot.UseAllFaces,
                            crop,
                            cancel).ConfigureAwait(false);

                        await OnUi(() =>
                        {
                            if (item.ApplyGeneration == snapshot.ApplyGeneration)
                                ApplyOutcome(item, outcome, fitted: true, fromBatch: true);
                            FinishBatchImage(item, outcome.Ok);
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
                    catch (OperationCanceledException)
                    {
                        await OnUi(() => { FinishBatchImage(item, false, "Cancelled"); Log("Batch", $"{item.Name}: cancelled.", LogLevel.Warning); }).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        await OnUi(() => { FinishBatchImage(item, false); Log("Batch", WithWorkerStderr($"{item.Name}: {ex.Message}"), LogLevel.Error, WorkerErrorDetails()); }).ConfigureAwait(false);
                    }
                    await OnUi(() =>
                    {
                        BatchProgressSummary = $"{done} complete · {failed} failed · {items.Length} total";
                        BatchProgress = 100.0 * (i + 1) / items.Length;
                    }).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await OnUi(() =>
            {
                if (!cancel.IsCancellationRequested) BatchProgress = 100;
                foreach (var queued in items.Where(item => item.ProcessingStatus == "Queued"))
                {
                    queued.ProcessingStatus = cancel.IsCancellationRequested ? "Cancelled" : "Removed";
                    queued.ProcessingStage = queued.ProcessingStatus;
                }
                if (_batchCurrentItem is { } unfinished) FinishBatchImage(unfinished, false, cancel.IsCancellationRequested ? "Cancelled" : "Failed");
                BatchProgressSummary = $"{done} complete · {failed} failed · {items.Length} total" + (cancel.IsCancellationRequested ? " · Cancelled" : "");
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
        if (!Session.Contains(item)) return;
        if (!result.Ok && item.Handshake is { Ok: true })
        {
            Status = "Detection did not complete. Previous detection and results are kept.";
            Log("Detect", WithWorkerStderr(result.Error), LogLevel.Warning);
            NotifyRunCommands();
            return;
        }
        if (item.ReferenceAssignments.Count > 0)
        {
            InvalidateCalibration(item);
            item.ReferenceAssignments.Clear();
            item.ReferencesNeedAssignment = true;
            if (item == Active) OnPropertyChanged(nameof(ReferenceSummary));
        }
        if (result.Ok && !string.IsNullOrWhiteSpace(result.HandshakeJson))
        {
            var handshake = new HandshakeDocument(result.HandshakeJson);
            item.ApplyHandshake(handshake);
            _detectFallback = "";
            if (handshake.Inference is { ValueKind: System.Text.Json.JsonValueKind.Object } inference
                && inference.TryGetProperty("detect", out var detection) && detection.ValueKind == System.Text.Json.JsonValueKind.Object
                && detection.TryGetProperty("fallback", out var fallback) && fallback.ValueKind == System.Text.Json.JsonValueKind.Object
                && fallback.TryGetProperty("reason", out var reason) && reason.ValueKind == System.Text.Json.JsonValueKind.String)
                _detectFallback = ConciseProcessingError(reason.GetString() ?? "");
            RememberJobAccel(null, null, handshake.Device);
            var charts = Math.Max(1, result.ChartCount > 0 ? result.ChartCount : handshake.ChartCount);
            var summary = $"{item.DetectLabel} · {charts} chart(s)";
            if (item == Active)
            {
                OnPropertyChanged(nameof(ReferenceSummary));
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
            item.DetectLabel = string.IsNullOrWhiteSpace(result.Error) ? "Detection failed" : ConciseProcessingError(result.Error);
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
        if (error.Contains("inference unavailable", StringComparison.OrdinalIgnoreCase)
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
        if (!Session.Contains(item)) return;
        if (!outcome.Ok)
        {
            var error = string.IsNullOrWhiteSpace(outcome.Error) ? "Correction failed." : outcome.Error.Trim();

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
        item.ModelPath = outcome.ModelPath;
        item.CompletedContext = outcome.CompletedContext;
        item.PreviewSuppressed = outcome.CompletedContext?.Options().PreviewMode == "none";
        item.SourceIdentity = outcome.CompletedContext?.Source ?? item.SourceIdentity;
        item.IncludeInSave = true;
        item.NotifyOutputs();
        if (fitted)
        {
            if (item.Handshake is { Ok: true } handshake)
                item.DetectLabel = ColorSpaces.ChartLabel(handshake.ChartType);
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
            item.FfcPlane = FfcPlane.TryLoad(outcome.ModelPath);
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
            OnPropertyChanged(nameof(IsResultPreviewSuppressed));
            if (item.PreviewSuppressed)
            {
                _previewGeneration++;
                SetPreview(OriginalPreview, null);
                HasCorrected = false; ShowCorrected = false; StageName = "Original";
            }
            else _ = LoadCorrectedPreviewAsync(item);
            if (!item.PreviewSuppressed && !string.IsNullOrWhiteSpace(LastPreviewDir))
            {
                StageName = "Corrected";
                ApplyStagePreview();
                NotifyStageRadios();
            }
        }

        var detail = $"{item.Name} · {(fitted ? "fit complete" : "model applied")}";
        if (!fromBatch)
        {
            Status = fitted ? "Apply complete." : "Model applied.";
            ToastService.ShowSuccess(Status);
        }

        RememberJobAccel(outcome.PlaneBackend, outcome.ApplyBackend, outcome.DetectBackend);
        var extra = new List<string>();
        extra.AddRange(DeltaEDetails(item.Report));
        if (File.Exists(outcome.OutputPath))
        {
            extra.Add($"Saved {Path.GetFileName(outcome.OutputPath)}");
        }

        if (!string.IsNullOrWhiteSpace(outcome.PlaneBackend))
        {
            extra.Add($"Plane inference: {LabelStage(outcome.PlaneBackend)}");
        }
        if (!string.IsNullOrWhiteSpace(outcome.ApplyBackend))
        {
            var backend = outcome.ApplyBackend;
            var label = backend.StartsWith("wgpu:", StringComparison.OrdinalIgnoreCase)
                ? backend.Contains("+cpu", StringComparison.OrdinalIgnoreCase)
                    ? "GPU with CPU recovery"
                    : $"GPU ({backend.Split(':')[1].ToUpperInvariant()})"
                : LabelStage(backend);
            extra.Add($"LUT: {label}");
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
