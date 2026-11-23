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
    private readonly Dictionary<string, string> _numericText = new();
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
        CheckLoadedFiles(_loadedModelIdentity, LoadedModelPath, DoFfc ? _loadedWhiteIdentity : null, WhiteImagePath);
    private string NumericText(string name, decimal value) => _numericText.GetValueOrDefault(name) ?? value.ToString(CultureInfo.CurrentCulture);
    private void SetNumericText(string name, string text, Action<decimal> set)
    {
        _numericText[name] = text;
        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)) set(value);
        RefreshValidation();
    }
    public string FitSamplesText { get => NumericText(nameof(FitSamples), FitSamples); set => SetNumericText(nameof(FitSamples), value, v => FitSamples = v); }
    public string PlsComponentsText { get => NumericText(nameof(PlsComponents), PlsComponents); set => SetNumericText(nameof(PlsComponents), value, v => PlsComponents = v); }
    public string NnDropoutText { get => NumericText(nameof(NnDropout), NnDropout); set => SetNumericText(nameof(NnDropout), value, v => NnDropout = v); }
    public string NnLrText { get => NumericText(nameof(NnLr), NnLr); set => SetNumericText(nameof(NnLr), value, v => NnLr = v); }
    public string NnEpochsText { get => NumericText(nameof(NnEpochs), NnEpochs); set => SetNumericText(nameof(NnEpochs), value, v => NnEpochs = v); }
    public string NnPatienceText { get => NumericText(nameof(NnPatience), NnPatience); set => SetNumericText(nameof(NnPatience), value, v => NnPatience = v); }
    public string NnTolText { get => NumericText(nameof(NnTol), NnTol); set => SetNumericText(nameof(NnTol), value, v => NnTol = v); }
    public string FfcBinsText { get => NumericText(nameof(FfcBins), FfcBins); set => SetNumericText(nameof(FfcBins), value, v => FfcBins = v); }
    public string FfcSmoothWindowText { get => NumericText(nameof(FfcSmoothWindow), FfcSmoothWindow); set => SetNumericText(nameof(FfcSmoothWindow), value, v => FfcSmoothWindow = v); }
    public string FfcDegreeText { get => NumericText(nameof(FfcDegree), FfcDegree); set => SetNumericText(nameof(FfcDegree), value, v => FfcDegree = v); }
    public string FfcNnMaxIterText { get => NumericText(nameof(FfcNnMaxIter), FfcNnMaxIter); set => SetNumericText(nameof(FfcNnMaxIter), value, v => FfcNnMaxIter = v); }
    public string FfcTolText { get => NumericText(nameof(FfcTol), FfcTol); set => SetNumericText(nameof(FfcTol), value, v => FfcTol = v); }

    private string? NumberError(string name, decimal value, string label, decimal min, decimal max, bool integer = false)
    {
        if (_numericText.TryGetValue(name, out var text) && !decimal.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            return $"{label}: enter a number.";
        if (value < min || value > max || (integer && value != decimal.Truncate(value)))
            return $"{label}: enter {(integer ? "a whole number" : "a number")} from {min} to {max}.";
        return null;
    }

    /// <summary>Validate the next operation, hashing the loaded model and white reference now.</summary>
    internal string? OperationError(bool fit) => OperationError(fit, fit ? default : CheckLoadedFilesNow());

    /// <summary>Validation for UI polling (getters, CanExecute): uses the last background file check.</summary>
    private string? CachedOperationError(bool fit) => OperationError(fit, _cachedFileState);

    private string? OperationError(bool fit, LoadedFileState files)
    {
        if (!fit && files.ModelChanged)
            return "The loaded model changed or moved. Load it again before applying it.";
        if (DoFfc)
        {
            if (!File.Exists(WhiteImagePath)) return "Choose a readable white reference before using FFC.";
            if (!fit && files.WhiteChanged)
                return "The model's white reference changed. Choose the intended white image again before applying it.";
            var error = NumberError(nameof(FfcBins), FfcBins, "FFC bins", 4, 200, true)
                ?? NumberError(nameof(FfcSmoothWindow), FfcSmoothWindow, "FFC smooth window", 1, 51, true);
            if (error is not null) return error;
            if (FfcSmoothWindow % 2 == 0) return "FFC smooth window must be an odd whole number.";
            if (SelectedFfcFitMethod.Value != "linear" && NumberError(nameof(FfcDegree), FfcDegree, "FFC degree", 1, 7, true) is { } degree) return degree;
            if (SelectedFfcFitMethod.Value is "nn" or "pls" && NumberError(nameof(FfcTol), FfcTol, "FFC tolerance", 0.000000000001m, 0.01m) is { } tolerance) return tolerance;
            if (SelectedFfcFitMethod.Value == "nn" && NumberError(nameof(FfcNnMaxIter), FfcNnMaxIter, "FFC iterations", 50, 20000, true) is { } iterations) return iterations;
        }
        if (!fit) return null;
        if ((DoFfc || DoGc || DoWb) && (SelectedInputSpace.Value != "srgb" || SelectedInputWhite.Value != "d65"))
            return "FFC, tone response and white balance require sRGB / D65 input. Convert the image first or turn these stages off.";
        if (NumberError(nameof(FitSamples), FitSamples, "Samples per side", 1, 200, true) is { } samples) return samples;
        if (!DoCc) return null;
        if (ShowPls && NumberError(nameof(PlsComponents), PlsComponents, "PLS components", 0, 64, true) is { } components) return components;
        if (ShowCcIterParams && NumberError(nameof(NnTol), NnTol, "Convergence tolerance", 0.000000000001m, 0.01m) is { } tol) return tol;
        if (!ShowNn) return null;
        if (!NnSettingsValid) return HiddenLayersError;
        return NumberError(nameof(NnDropout), NnDropout, "Dropout rate", 0, 0.8m)
            ?? NumberError(nameof(NnLr), NnLr, "Learning rate", 0.000000000000001m, 1)
            ?? NumberError(nameof(NnEpochs), NnEpochs, "Epochs", 50, 20000, true)
            ?? NumberError(nameof(NnPatience), NnPatience, "Patience", 1, 5000, true);
    }
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
        var white = DoFfc ? _loadedWhiteIdentity : null;
        var whitePath = WhiteImagePath;
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
        var white = DoFfc ? _loadedWhiteIdentity : null;
        var whitePath = WhiteImagePath;
        var state = await Task.Run(() => CheckLoadedFiles(model, modelPath, white, whitePath));
        if (state != _cachedFileState) { _cachedFileState = state; NotifyValidation(); }
        return OperationError(fit, state);
    }
    private void ClearNumericDrafts()
    {
        var names = _numericText.Keys.ToArray(); _numericText.Clear();
        foreach (var name in names) OnPropertyChanged(name + "Text");
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
        return stage switch { "ffc" => options?.DoFfc ?? DoFfc, "gc" => options?.DoGc ?? DoGc,
            "wb" => options?.DoWb ?? DoWb, "cc" => options?.DoCc ?? DoCc, _ => true };
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
            DoFfc = Enabled(model, "do_ffc", false);
            DoGc = Enabled(model, "do_gc", model.TryGetProperty("gc_model", out var gc) && gc.ValueKind != JsonValueKind.Null);
            DoWb = Enabled(model, "do_wb", model.TryGetProperty("wb_diag", out var wb) && wb.ValueKind != JsonValueKind.Null);
            DoCc = Enabled(model, "do_cc", true);
            if (model.TryGetProperty("ffc_params", out var ffc) && ffc.ValueKind == JsonValueKind.Object)
            {
                decimal Number(string name, decimal fallback) => ffc.TryGetProperty(name, out var value) && value.TryGetDecimal(out var number) ? number : fallback;
                if (ffc.TryGetProperty("method", out var method)) SelectedFfcFitMethod = Pick(FfcFitMethods, method.GetString() ?? "poly");
                FfcBins = Number("bins", 50); FfcSmoothWindow = Number("smooth_window", 5); FfcDegree = Number("degree", 3);
                FfcNnMaxIter = Number("nn_max_iter", 10000); FfcTol = Number("tol", 0.00000001m);
                FfcCutOff = Number("cut_off", 1.3m); FfcMaxVal = Number("max_val", 1.8m);
            }
            CropRect = "";
            if (model.TryGetProperty("python_api", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            {
                if (metadata.TryGetProperty("white", out var white) && white.ValueKind == JsonValueKind.String) WhiteImagePath = white.GetString() ?? "";
                if (metadata.TryGetProperty("white_sha256", out var hash) && hash.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(WhiteImagePath))
                    _loadedWhiteIdentity = new(WhiteImagePath, hash.GetString() ?? "");
                if (metadata.TryGetProperty("crop_rect", out var crop) && crop.ValueKind == JsonValueKind.String) CropRect = crop.GetString() ?? "";
            }
            ClearNumericDrafts();
        }
        catch (Exception ex) { Log("Model", $"Could not restore model settings: {ex.Message}", LogLevel.Warning); }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task RelinkImageAsync()
    {
        if (Busy || IsSaving || Active is not { } item || Dialogs is null) return;
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
