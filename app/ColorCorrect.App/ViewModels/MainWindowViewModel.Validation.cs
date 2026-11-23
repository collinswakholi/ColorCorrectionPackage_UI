using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ColorCorrect.App.Models;
using ColorCorrect.Engine;
using CommunityToolkit.Mvvm.Input;

namespace ColorCorrect.App.ViewModels;

public partial class MainWindowViewModel
{
    private AssetIdentity? _loadedModelIdentity;
    private AssetIdentity? _loadedWhiteIdentity;

    /// <summary>Whether the loaded model / its white reference no longer match their files.</summary>
    internal readonly record struct LoadedFileState(bool ModelChanged, bool WhiteChanged);

    // Last background comparison, read by the UI-polled validation paths so property getters
    // and CanExecute never hash files on the UI thread.
    private LoadedFileState _cachedFileState;
    private int _fileCheckRunning;
    private int _fileCheckDirty;

    private static LoadedFileState CheckLoadedFiles(AssetIdentity? model, string modelPath, AssetIdentity? white, string whitePath) =>
        new(model is not null && !model.Matches(modelPath), white is not null && !white.Matches(whitePath));

    private LoadedFileState CheckLoadedFilesNow() =>
        CheckLoadedFiles(_loadedModelIdentity, LoadedModelPath, Settings.DoFfc ? _loadedWhiteIdentity : null, Settings.WhiteImagePath);
    /// <summary>Validate the next operation, hashing the loaded model and white reference now.</summary>
    internal string? OperationError(bool fit) => OperationError(fit, fit ? default : CheckLoadedFilesNow());

    /// <summary>Validation for UI polling (getters, CanExecute): uses the last background file check.</summary>
    private string? CachedOperationError(bool fit) => OperationError(fit, _cachedFileState);

    private string? OperationError(bool fit, LoadedFileState files) =>
        Settings.ValidationError(fit, files.ModelChanged, files.WhiteChanged,
            () => File.Exists(Settings.WhiteImagePath), Settings.ShowPls, Settings.ShowCcIterParams, Settings.ShowNn);
    public string ValidationMessage => CachedOperationError(!WillUseLoadedModel) ?? "";
    public bool HasValidationError => !string.IsNullOrEmpty(ValidationMessage);
    private void RefreshValidation()
    {
        NotifyValidation();
        QueueLoadedFileCheck();
    }

    private void NotifyValidation()
    {
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(HasValidationError));
        ApplyCommand.NotifyCanExecuteChanged(); BatchFitCommand.NotifyCanExecuteChanged(); BatchApplyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Re-compare the loaded model and white reference with their files off the UI thread.
    /// Requests that arrive while a check runs are coalesced into one follow-up check; the UI
    /// is notified only when the result changes.
    /// </summary>
    private void QueueLoadedFileCheck()
    {
        if (_loadedModelIdentity is null && _loadedWhiteIdentity is null)
        {
            if (_cachedFileState != default) { _cachedFileState = default; NotifyValidation(); }
            return;
        }

        if (Interlocked.Exchange(ref _fileCheckRunning, 1) == 1)
        {
            Volatile.Write(ref _fileCheckDirty, 1);
            return;
        }

        var model = _loadedModelIdentity;
        var modelPath = LoadedModelPath;
        var white = Settings.DoFfc ? _loadedWhiteIdentity : null;
        var whitePath = Settings.WhiteImagePath;
        _ = Task.Run(() => CheckLoadedFiles(model, modelPath, white, whitePath)).ContinueWith(task => Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _fileCheckRunning, 0);
            if (task.IsCompletedSuccessfully && task.Result != _cachedFileState)
            {
                _cachedFileState = task.Result;
                NotifyValidation();
            }
            if (Interlocked.Exchange(ref _fileCheckDirty, 0) == 1) QueueLoadedFileCheck();
        }), TaskScheduler.Default);
    }

    /// <summary>
    /// Authoritative check before an apply starts: hashes off the UI thread, updates the cached
    /// state and returns the blocking error, if any.
    /// </summary>
    private async Task<string?> VerifyLoadedFilesAsync(bool fit)
    {
        if (fit) return null;
        var model = _loadedModelIdentity;
        var modelPath = LoadedModelPath;
        var white = Settings.DoFfc ? _loadedWhiteIdentity : null;
        var whitePath = Settings.WhiteImagePath;
        var state = await Task.Run(() => CheckLoadedFiles(model, modelPath, white, whitePath));
        if (state != _cachedFileState) { _cachedFileState = state; NotifyValidation(); }
        return OperationError(fit, state);
    }
    private void ClearNumericDrafts()
    {
        Settings.ClearDrafts();
        RefreshValidation();
    }

    internal CorrectionOptions? ReplayOptions(SessionItemViewModel item)
    {
        var context = item.CompletedContext;
        var error = context?.ReplayError(item.Path) ?? (context is null ? "This older result has no saved rendering context. Existing files can be saved; process the image again to regenerate missing stages." : null);
        if (error is not null) { Log("Saved result", error, LogLevel.Warning); return null; }
        return context!.Options();
    }

    private bool ResultStageEnabled(SessionItemViewModel? item, string stage)
    {
        var options = item?.CompletedContext?.Options();
        return stage switch { "ffc" => options?.DoFfc ?? Settings.DoFfc, "gc" => options?.DoGc ?? Settings.DoGc,
            "wb" => options?.DoWb ?? Settings.DoWb, "cc" => options?.DoCc ?? Settings.DoCc, _ => true };
    }

    internal void RestoreLoadedModelSettings(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var model = document.RootElement;
            _loadedModelIdentity = AssetIdentity.Capture(path);
            _loadedWhiteIdentity = null;
            static bool Enabled(JsonElement model, string name, bool fallback) => model.TryGetProperty(name, out var field) && field.ValueKind is JsonValueKind.True or JsonValueKind.False ? field.GetBoolean() : fallback;
            Settings.DoFfc = Enabled(model, "do_ffc", false);
            Settings.DoGc = Enabled(model, "do_gc", model.TryGetProperty("gc_model", out var gc) && gc.ValueKind != JsonValueKind.Null);
            Settings.DoWb = Enabled(model, "do_wb", model.TryGetProperty("wb_diag", out var wb) && wb.ValueKind != JsonValueKind.Null);
            Settings.DoCc = Enabled(model, "do_cc", true);
            if (model.TryGetProperty("ffc_params", out var ffc) && ffc.ValueKind == JsonValueKind.Object)
            {
                decimal Number(string name, decimal fallback) => ffc.TryGetProperty(name, out var value) && value.TryGetDecimal(out var number) ? number : fallback;
                if (ffc.TryGetProperty("method", out var method)) Settings.SelectedFfcFitMethod = Pick(FfcFitMethods, method.GetString() ?? "poly");
                Settings.FfcBins = Number("bins", 50); Settings.FfcSmoothWindow = Number("smooth_window", 5); Settings.FfcDegree = Number("degree", 3);
                Settings.FfcNnMaxIter = Number("nn_max_iter", 10000); Settings.FfcTol = Number("tol", 0.00000001m);
                Settings.FfcCutOff = Number("cut_off", 1.3m); Settings.FfcMaxVal = Number("max_val", 1.8m);
            }
            Stage.CropRect = "";
            if (model.TryGetProperty("python_api", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            {
                if (metadata.TryGetProperty("white", out var white) && white.ValueKind == JsonValueKind.String) Settings.WhiteImagePath = white.GetString() ?? "";
                if (metadata.TryGetProperty("white_sha256", out var hash) && hash.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(Settings.WhiteImagePath))
                    _loadedWhiteIdentity = new(Settings.WhiteImagePath, hash.GetString() ?? "");
                if (metadata.TryGetProperty("crop_rect", out var crop) && crop.ValueKind == JsonValueKind.String) Stage.CropRect = crop.GetString() ?? "";
            }
            ClearNumericDrafts();
        }
        catch (Exception ex) { Log("Model", $"Could not restore model settings: {ex.Message}", LogLevel.Warning); }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task RelinkImageAsync()
    {
        if (Busy || IsSaving || Workspace.Active is not { } item || Dialogs is null) return;
        var paths = await Dialogs.OpenImagesAsync();
        if (paths.Count != 1) { if (paths.Count > 1) Status = "Choose one replacement image."; return; }
        var replacement = paths[0];
        if (!Services.SupportedStillImage.IsSupportedFile(replacement)) { Status = "Choose a readable JPEG, PNG or TIFF image."; return; }
        if (Session.Any(other => other != item && PathsEqual(other.Path, replacement))) { Status = "That image is already in this session."; return; }
        var identity = item.SourceIdentity ?? item.CompletedContext?.Source;
        if (identity?.Matches(replacement) != true && !await Dialogs.ConfirmAsync("Use a different image?", "This file does not match the original. Detection and reference assignments will be cleared; saved output files remain on disk.", "Use image")) return;
        var same = item.Relink(replacement);
        Activate(item); RefreshSessionUi(); NotifyRunCommands(); ScheduleSave();
        Status = same ? "Original image relinked. Saved detection and results retained." : "Image replaced. Detect its chart before fitting.";
    }
}
