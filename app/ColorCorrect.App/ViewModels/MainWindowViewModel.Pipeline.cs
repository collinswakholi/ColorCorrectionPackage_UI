using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ColorCorrect.App.Models;
using ColorCorrect.App.Services;
using ColorCorrect.Engine;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ColorCorrect.App.ViewModels;

public partial class MainWindowViewModel
{
    public IReadOnlyList<NamedValue> FfcFitMethods { get; }
    public IReadOnlyList<NamedValue> AccelPolicies { get; } =
    new NamedValue[] {
        new("auto", "Auto (recommended)"),
        new("cpu-inference", "CPU"),
        new("webgpu", "WebGPU"),
        new("directml", "DirectML"),
        new("vulkan", "Vulkan"),
    }.Where(mode => mode.Value != "directml" || OperatingSystem.IsWindows()).ToArray();
    public ObservableCollection<AccelRow> AccelRows { get; } = [];
    public IReadOnlyList<NamedValue> ProcessingDevices { get; } = new NamedValue[] {
        new("auto", "Auto (recommended)"), new("cpu", "CPU"), new("gpu", "GPU")
    };
    [ObservableProperty] private NamedValue _selectedProcessingDevice = new("auto", "Auto (recommended)");

    [ObservableProperty] private bool _doFfc;
    [ObservableProperty] private bool _doGc = true;
    [ObservableProperty] private bool _doWb = true;
    [ObservableProperty] private bool _doCc = true;
    [ObservableProperty] private string _whiteImagePath = "";
    [ObservableProperty] private string _cropRect = "";
    [ObservableProperty] private NamedValue _selectedFfcFitMethod = new("poly", "Polynomial");
    [ObservableProperty] private decimal _ffcBins = 50;
    [ObservableProperty] private decimal _ffcSmoothWindow = 5;
    [ObservableProperty] private decimal _ffcDegree = 3;
    [ObservableProperty] private decimal _ffcNnMaxIter = 10000;
    [ObservableProperty] private decimal _ffcTol = 0.00000001m;
    [ObservableProperty] private decimal _ffcCutOff = 1.3m;
    [ObservableProperty] private decimal _ffcMaxVal = 1.8m;
    [ObservableProperty] private decimal _clipLo = -0.2m;
    [ObservableProperty] private decimal _clipHi = 1.3m;
    [ObservableProperty] private bool _showFfcNn;
    [ObservableProperty] private bool _showFfcIter;
    [ObservableProperty] private string _stageName = "Original";
    [ObservableProperty] private bool _isStagePreviewLoading;
    [ObservableProperty] private string _stagePreviewLoadingLabel = "";
    [ObservableProperty] private string _accelJson = "";
    [ObservableProperty] private string _lutNote = "LUT does not include flat-field.";
    [ObservableProperty] private bool _forceCpu;
    [ObservableProperty] private bool _fastMode = true;
    [ObservableProperty] private NamedValue _selectedAccelPolicy = new("auto", "Auto (recommended)");
    private bool _accelSync;
    [ObservableProperty] private bool _phase2Teacher = true;
    [ObservableProperty] private string _lastPreviewDir = "";
    [ObservableProperty] private string _accelHint = "Checking graphics hardware…";
    private readonly Dictionary<string, Bitmap> _stageBitmaps = new(StringComparer.OrdinalIgnoreCase);
    private string _usedDetect = "";
    private string _detectFallback = "";
    private string _usedPlane = "";
    private string _usedApply = "";
    private int _stageLoadGen;



    public bool IsStageOriginal
    {
        get => StageName == "Original";
        set { if (value) StageName = "Original"; }
    }

    public bool IsStageFfc
    {
        get => StageName == "FFC";
        set { if (value) StageName = "FFC"; }
    }

    public bool IsStageGc
    {
        get => StageName == "GC";
        set { if (value) StageName = "GC"; }
    }

    public bool IsStageWb
    {
        get => StageName == "WB";
        set { if (value) StageName = "WB"; }
    }

    public bool IsStageCc
    {
        get => StageName == "CC";
        set { if (value) StageName = "CC"; }
    }

    public bool IsStageCorrected
    {
        get => StageName == "Corrected";
        set { if (value) StageName = "Corrected"; }
    }

    public bool IsFfcPreviewLoading => IsStagePreviewLoading && StageName == "FFC";
    public bool IsGcPreviewLoading => IsStagePreviewLoading && StageName == "GC";
    public bool IsWbPreviewLoading => IsStagePreviewLoading && StageName == "WB";

    public bool HasStageFfc => ResultStageEnabled(Active, "ffc") && (Active?.IsCorrected == true || StageFileExists("ffc.jpg"));
    public bool HasStageGc => ResultStageEnabled(Active, "gc") && (Active?.IsCorrected == true || StageFileExists("gc.jpg"));
    public bool HasStageWb => ResultStageEnabled(Active, "wb") && (Active?.IsCorrected == true || StageFileExists("wb.jpg"));
    public bool HasStageCc => ResultStageEnabled(Active, "cc") && (Active?.IsCorrected == true || StageFileExists("cc.jpg") || StageFileExists("corrected.jpg"));
    public bool HasStageCorrected => HasCorrected || Active?.IsCorrected == true || StageFileExists("corrected.jpg");

    public string WhiteImageLabel => string.IsNullOrWhiteSpace(WhiteImagePath)
        ? "Choose a white reference to use FFC"
        : Path.GetFileName(WhiteImagePath);

    public bool ShowFfcDegree => SelectedFfcFitMethod.Value != "linear";

    private bool StageFileExists(string file) =>
        ResolvePreviewFile(LastPreviewDir, Path.GetFileNameWithoutExtension(file)) is not null;

    internal static string? ResolvePreviewFile(string? dir, string stem)
    {
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(stem))
        {
            return null;
        }

        return ResolvePreviewFile(dir, stem, forDisplay: true);
    }

    internal static string? ResolvePreviewFile(string? dir, string stem, bool forDisplay)
    {
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(stem))
        {
            return null;
        }

        var order = forDisplay
            ? new[] { ".rgb", ".png", ".jpg" }
            : new[] { ".png", ".jpg", ".rgb" };
        foreach (var ext in order)
        {
            var path = Path.Combine(dir, stem + ext);
            if (File.Exists(path) && new FileInfo(path).Length > 16)
            {
                return path;
            }
        }

        if (forDisplay)
        {
            return null;
        }

        var png = Path.Combine(dir, stem + ".png");
        var until = DateTime.UtcNow.AddMilliseconds(250);
        while (DateTime.UtcNow < until)
        {
            if (File.Exists(png) && new FileInfo(png).Length > 16)
            {
                return png;
            }

            Thread.Sleep(15);
        }

        var rgb = Path.Combine(dir, stem + ".rgb");
        return File.Exists(rgb) ? rgb : null;
    }

    [RelayCommand]
    private async Task BrowseWhiteAsync()
    {
        if (Dialogs is null)
        {
            return;
        }

        var path = await Dialogs.OpenWhiteAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            UseWhitePath(path);
        }
    }

    [RelayCommand]
    private void ClearWhite()
    {
        WhiteImagePath = "";
        HasWhiteRef = false;
        OnPropertyChanged(nameof(WhiteImageLabel));
        ScheduleSave();
    }

    [RelayCommand]
    private async Task AddCatalogFolderAsync()
    {
        if (Dialogs is null)
        {
            return;
        }

        var folder = await Dialogs.PickFolderAsync("Add catalog folder");
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        if (CatalogFolders.Any(item => PathIdentity.Equal(item.Path, folder)))
        {
            Status = "That folder is already in the catalog.";
            return;
        }

        CatalogFolders.Add(new CatalogFolderViewModel(folder));
        HasCatalogFolders = CatalogFolders.Count > 0;
        LoadCatalog();
        ScheduleSave();
        Status = $"Catalog + {Path.GetFileName(folder)}";
        Log("Catalog", $"Added folder {folder}.", LogLevel.Success);
    }

    [RelayCommand]
    private void RemoveCatalogFolder(CatalogFolderViewModel? folder)
    {
        if (folder is null)
        {
            return;
        }

        CatalogFolders.Remove(folder);
        HasCatalogFolders = CatalogFolders.Count > 0;
        LoadCatalog();
        ScheduleSave();
        Status = $"Removed catalog folder {folder.Name}";
        Log("Catalog", Status);
    }

    public void UseWhitePath(string path)
    {
        // Choosing a white image is an explicit override of a loaded model's reference.
        _loadedWhiteIdentity = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !IsImage(path))
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) && !IsImage(path))
            {
                Status = "FFC white must be a still image.";
                Log("FFC", Status, LogLevel.Warning);
            }

            return;
        }

        WhiteImagePath = Path.GetFullPath(path);
        DoFfc = true;
        HasWhiteRef = true;
        OnPropertyChanged(nameof(WhiteImageLabel));
        Status = $"FFC white · {Path.GetFileName(WhiteImagePath)}";
        Log("FFC", Status, LogLevel.Info, [WhiteImagePath]);
        EnsureWhiteInSession(activate: false);
        RefreshOverlay();
        ScheduleSave();
    }

    [RelayCommand]
    private void UseCatalogWhite(CatalogItemViewModel? item)
    {
        if (item is not null)
        {
            UseWhitePath(item.Path);
        }
    }

    [RelayCommand]
    private void UseSessionWhite(SessionItemViewModel? item)
    {
        if (item is not null)
        {
            UseWhitePath(item.Path);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestartWorker))]
    private async Task ForceCpuRestartAsync()
    {
        var cancellation = BeginJob();
        _worker.Policy = SelectedAccelPolicy.Value;
        _detectionFailures.Clear();
        _engineGeneration++;
        _usedDetect = _usedPlane = _usedApply = "";
        await OnUi(() =>
        {
            Busy = true;
            WorkerReady = false;
            WorkerStartupComplete = false;
            WorkerStartupError = "";
            Status = "Restarting worker…";
        }).ConfigureAwait(false);
        try
        {
            await _worker.RestartAsync(cancellation).ConfigureAwait(false);
            await RefreshAccelAsync().ConfigureAwait(false);
            await OnUi(() =>
            {
                WorkerReady = true;
                WorkerStartupComplete = true;
                WorkerStartupError = "";
                WorkerLine = string.IsNullOrWhiteSpace(_worker.Title)
                    ? $"Warmed · {_worker.Device}"
                    : _worker.Title;
                Status = WorkerLine;
            }).ConfigureAwait(false);
        }
        catch (System.Exception ex)
        {
            await OnUi(() =>
            {
                WorkerStartupComplete = true;
                WorkerStartupError = ex.Message;
                Status = ex.Message;
                WorkerLine = "Worker failed";
            }).ConfigureAwait(false);
        }
        finally { await OnUi(() => Busy = false).ConfigureAwait(false); }
    }

    [RelayCommand]
    private async Task RefreshAccelAsync()
    {
        try
        {
            var report = await _worker.DevicesAsync().ConfigureAwait(false);
            var raw = report.GetRawText();
            var rows = ParseAccel(report);
            var hint = AccelHintFrom(report);
            await OnUi(() =>
            {
                AccelJson = raw;
                AccelRows.Clear();
                foreach (var row in rows)
                {
                    AccelRows.Add(row);
                }

                AccelHint = hint;
            }).ConfigureAwait(false);
        }
        catch (System.Exception ex)
        {
            await OnUi(() => AccelJson = ex.Message).ConfigureAwait(false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportCube))]
    private async Task ExportCubeAsync()
    {
        if (!CanExportCube() || Dialogs is null)
        {
            return;
        }

        var dest = await Dialogs.SaveCubeAsync("shot.cube");
        if (string.IsNullOrWhiteSpace(dest))
        {
            return;
        }

        var result = await _worker.SendAsync(new Dictionary<string, object?>
        {
            ["cmd"] = "export_cube",
            ["model"] = LoadedModelPath,
            ["path"] = dest,
            ["size"] = 33,
        }).ConfigureAwait(false);
        var ok = result.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
        await OnUi(() =>
        {
            Status = ok
                ? "Exported .cube (LUT does not include flat-field)."
                : result.TryGetProperty("error", out var err) ? err.GetString() ?? "export failed" : "export failed";
        }).ConfigureAwait(false);
    }

    partial void OnStageNameChanged(string value)
    {
        if (value == "Corrected")
        {
            if (!ShowCorrected)
            {
                ShowCorrected = true;
            }
        }
        else if (ShowCorrected)
        {
            ShowCorrected = false;
        }

        NotifyStageRadios();
        ApplyStagePreview();
        SyncStagePreviewLoading();
        RefreshOverlay();
        _ = EnsureStagePreviewAsync(value);
    }

    partial void OnDoFfcChanged(bool value)
    {
        NotifyStageRadios();
        RefreshValidation();
        UpdateCcSummary();
        if (!_restoring) ScheduleSave();
    }
    partial void OnDoGcChanged(bool value)
    {
        NotifyStageRadios();
        RefreshValidation();
        UpdateCcSummary();
        if (!_restoring) ScheduleSave();
    }
    partial void OnDoWbChanged(bool value)
    {
        NotifyStageRadios();
        RefreshValidation();
        UpdateCcSummary();
        if (!_restoring) ScheduleSave();
    }
    partial void OnDoCcChanged(bool value)
    {
        NotifyStageRadios();
        RefreshValidation();
        UpdateCcSummary();
        if (!_restoring) ScheduleSave();
    }
    partial void OnSelectedFfcFitMethodChanged(NamedValue value)
    {
        OnPropertyChanged(nameof(ShowFfcDegree));
        ShowFfcNn = value.Value == "nn";
        ShowFfcIter = value.Value is "nn" or "pls";
        RefreshValidation();
        UpdateCcSummary();
        if (!_restoring) ScheduleSave();
    }
    partial void OnFfcTolChanged(decimal value)
    {
        UpdateCcSummary();
        if (!_restoring) ScheduleSave();
    }
    partial void OnFfcCutOffChanged(decimal value)
    {
        UpdateCcSummary();
        if (!_restoring) ScheduleSave();
    }
    partial void OnFfcMaxValChanged(decimal value)
    {
        UpdateCcSummary();
        if (!_restoring) ScheduleSave();
    }
    partial void OnClipLoChanged(decimal value)
    {
        UpdateCcSummary();
        if (!_restoring) ScheduleSave();
    }
    partial void OnClipHiChanged(decimal value)
    {
        UpdateCcSummary();
        if (!_restoring) ScheduleSave();
    }
    partial void OnFfcNnMaxIterChanged(decimal value)
    {
        UpdateCcSummary();
        if (!_restoring) ScheduleSave();
    }
    partial void OnFfcBinsChanged(decimal value)
    {
        UpdateCcSummary();
        if (!_restoring) ScheduleSave();
    }
    partial void OnFfcSmoothWindowChanged(decimal value) { if (!_restoring) ScheduleSave(); }
    partial void OnFfcDegreeChanged(decimal value)
    {
        UpdateCcSummary();
        if (!_restoring) ScheduleSave();
    }
    partial void OnForceCpuChanged(bool value)
    {
        if (value) SelectedProcessingDevice = ProcessingDevices[1];
        else if (SelectedProcessingDevice.Value == "cpu") SelectedProcessingDevice = ProcessingDevices[0];
        if (!_restoring) ScheduleSave();
    }

    partial void OnSelectedProcessingDeviceChanged(NamedValue value)
    {
        ForceCpu = value.Value == "cpu";
        RefreshDeviceSummary();
        if (!_restoring) ScheduleSave();
    }

    partial void OnFastModeChanged(bool value)
    {
        if (!_restoring) ScheduleSave();
    }

    partial void OnSelectedAccelPolicyChanged(NamedValue value)
    {
        if (_restoring || _accelSync)
        {
            return;
        }

        _accelSync = true;
        try
        {
            _worker.Policy = value.Value;
            _detectionFailures.Clear();
            _usedDetect = _usedPlane = _detectFallback = "";
            RefreshDeviceSummary();
            if (!_restoring)
            {
                ScheduleSave();
            }
        }
        finally
        {
            _accelSync = false;
        }
    }
    partial void OnPhase2TeacherChanged(bool value) { if (!_restoring) ScheduleSave(); }
    partial void OnLastPreviewDirChanged(string value)
    {
        ClearStageBitmaps();
        NotifyStageRadios();
    }
    partial void OnWhiteImagePathChanged(string value)
    {
        HasWhiteRef = !string.IsNullOrWhiteSpace(value) && File.Exists(value);
        OnPropertyChanged(nameof(WhiteImageLabel));
        RefreshWhiteBadges();
        RefreshOverlay();
        RefreshValidation();
    }

    private void NotifyStageRadios()
    {
        OnPropertyChanged(nameof(IsResultPreviewSuppressed));
        OnPropertyChanged(nameof(IsStageOriginal));
        OnPropertyChanged(nameof(IsStageFfc));
        OnPropertyChanged(nameof(IsStageGc));
        OnPropertyChanged(nameof(IsStageWb));
        OnPropertyChanged(nameof(IsStageCc));
        OnPropertyChanged(nameof(IsStageCorrected));
        OnPropertyChanged(nameof(HasStageFfc));
        OnPropertyChanged(nameof(HasStageGc));
        OnPropertyChanged(nameof(HasStageWb));
        OnPropertyChanged(nameof(HasStageCc));
        OnPropertyChanged(nameof(HasStageCorrected));
        OnPropertyChanged(nameof(IsFfcPreviewLoading));
        OnPropertyChanged(nameof(IsGcPreviewLoading));
        OnPropertyChanged(nameof(IsWbPreviewLoading));
    }

    partial void OnIsStagePreviewLoadingChanged(bool value) => NotifyStageRadios();

    private static bool IsDeferredStage(string stage) =>
        stage is "FFC" or "GC" or "WB";

    private bool HasReadyStageBitmap(string stage)
    {
        var stem = StagePreviewStem(stage, ResolvePreviewFile(LastPreviewDir, "cc") is not null);
        return _stageBitmaps.ContainsKey(stem);
    }

    private void SyncStagePreviewLoading()
    {
        var loading = IsDeferredStage(StageName)
            && Active?.IsCorrected == true
            && !HasReadyStageBitmap(StageName);
        IsStagePreviewLoading = loading;
        StagePreviewLoadingLabel = loading ? $"Loading {StageName}" : "";
    }

    private static string StagePreviewStem(string stage, bool preferCc)
    {
        return stage.ToLowerInvariant() switch
        {
            "ffc" => "ffc",
            "gc" => "gc",
            "wb" => "wb",
            "cc" => preferCc ? "cc" : "corrected",
            "corrected" => "corrected",
            _ => "original",
        };
    }

    private void ClearStageBitmaps()
    {
        _stageLoadGen++;
        var retired = _stageBitmaps.Values.Distinct().ToArray();
        _stageBitmaps.Clear();
        foreach (var bitmap in retired) DisposeIfUnused(bitmap);
        SyncStagePreviewLoading();
    }

    private void ShowStageBitmap(Bitmap next)
    {
        var old = Preview;
        Preview = next;
        DisposeIfUnused(old);

        SyncStagePreviewLoading();
    }

    private void ApplyStagePreview()
    {
        if (Active?.PreviewSuppressed == true && StageName != "Original") return;
        if (StageName == "Original")
        {
            if (OriginalPreview is not null)
            {
                ShowStageBitmap(OriginalPreview);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(LastPreviewDir))
        {
            if (StageName == "Corrected" && CorrectedPreview is not null)
            {
                Preview = CorrectedPreview;
            }
            else if (OriginalPreview is not null)
            {
                Preview = OriginalPreview;
            }

            return;
        }

        var name = StagePreviewStem(StageName, ResolvePreviewFile(LastPreviewDir, "cc") is not null);
        if (_stageBitmaps.TryGetValue(name, out var cached))
        {
            ShowStageBitmap(cached);
            return;
        }

        var path = ResolvePreviewFile(LastPreviewDir, name);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var gen = ++_stageLoadGen;
        var stage = StageName;
        var dir = LastPreviewDir;
        _ = Task.Run(async () =>
        {
            Bitmap? next = null;
            try
            {
                next = DecodePreview(path);
            }
            catch
            {
                return;
            }

            await OnUi(() =>
            {
                if (gen != _stageLoadGen || next is null || LastPreviewDir != dir)
                {
                    next?.Dispose();
                    return;
                }

                if (_stageBitmaps.TryGetValue(name, out var existing))
                {
                    next.Dispose();
                    if (StageName == stage)
                    {
                        ShowStageBitmap(existing);
                    }

                    return;
                }

                _stageBitmaps[name] = next;
                if (StageName == stage)
                {
                    ShowStageBitmap(next);
                }
            }).ConfigureAwait(false);
        });
    }

    private async Task EnsureStagePreviewAsync(string stage)
    {
        if (Active?.PreviewSuppressed == true) return;
        var key = stage.ToLowerInvariant() switch
        {
            "ffc" => "ffc",
            "gc" => "gc",
            "wb" => "wb",
            "cc" => "cc",
            _ => null,
        };
        if (key is null)
        {
            return;
        }

        if (key == "cc" && (StageFileExists("cc") || StageFileExists("corrected")))
        {
            ApplyStagePreview();
            return;
        }

        if (StageFileExists(key))
        {
            return;
        }

        var item = Active;
        if (item is null || !item.IsCorrected)
        {
            return;
        }

        var model = item.CompletedContext?.Model.Path ?? "";
        var previewDir = !string.IsNullOrWhiteSpace(item.PreviewDir) ? item.PreviewDir : LastPreviewDir;
        if (!File.Exists(model) || string.IsNullOrWhiteSpace(previewDir))
        {
            FailStagePreview(stage);
            return;
        }

        var gen = ++_stageLoadGen;
        var ok = false;
        try
        {
            var options = ReplayOptions(item);
            if (options is not null) ok = await _client.PreviewStageAsync(item.Path, model, previewDir, key, options).ConfigureAwait(false);
        }
        finally
        {
            await OnUi(() =>
            {
                if (gen != _stageLoadGen)
                {
                    return;
                }

                if (ok)
                {
                    ApplyStagePreview();
                    NotifyStageRadios();
                }
                else
                {
                    FailStagePreview(stage);
                }

                SyncStagePreviewLoading();
            }).ConfigureAwait(false);
        }
    }

    internal void FailStagePreview(string stage)
    {
        StageName = HasStageCorrected ? "Corrected" : "Original";
        IsStagePreviewLoading = false;
        Log("Preview", $"Could not load {stage} preview. Check that the saved model and its assets are available.", LogLevel.Warning);
    }

    internal void RememberJobAccel(string? plane, string? apply, string? detect = null)
    {
        if (!string.IsNullOrWhiteSpace(detect))
        {
            _usedDetect = detect.Trim();
            OnPropertyChanged(nameof(DetectionStatus));
        }

        if (!string.IsNullOrWhiteSpace(plane))
        {
            _usedPlane = plane.Trim();
        }

        if (!string.IsNullOrWhiteSpace(apply))
        {
            _usedApply = apply.Trim();
        }

        if (string.IsNullOrWhiteSpace(AccelJson))
        {
            RefreshDeviceSummary();
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(AccelJson);
            AccelRows.Clear();
            foreach (var row in ParseAccel(doc.RootElement))
            {
                AccelRows.Add(row);
            }

            AccelHint = AccelHintFrom(doc.RootElement);
        }
        catch
        {
            // Keep the last probe rows if the cached devices JSON is stale.
        }
    }

    private string AccelHintFrom(JsonElement report)
    {
        var lines = new List<string> { DetectionStatus };
        if (!string.IsNullOrWhiteSpace(_detectFallback)) lines.Add($"Fallback: {_detectFallback}");
        if (!string.IsNullOrWhiteSpace(_usedPlane)) lines.Add($"White-plane detection: {LabelStage(_usedPlane)}");
        lines.Add($"Correction: {SelectedProcessingDevice.Label}" + (string.IsNullOrWhiteSpace(_usedApply) ? " — not run" : $" · last used {LabelStage(_usedApply)}"));
        return string.Join("\n", lines);
    }

    public string DetectionStatus => string.IsNullOrWhiteSpace(_usedDetect)
        ? $"Detection: {SelectedAccelPolicy.Label} — not checked"
        : $"Detection: {LabelStage(_usedDetect)}";

    private void RefreshDeviceSummary()
    {
        OnPropertyChanged(nameof(DetectionStatus));
        AccelHint = AccelHintFrom(default);
    }

    private static string LabelStage(string value) => value.Trim().ToLowerInvariant() switch
    {
        "wgpu" => "wgpu (pending execution)",
        "directml" => "DirectML",
        "webgpu" => "WebGPU",
        "vulkan" => "Vulkan",
        "cpu" => "CPU",
        "cpu-inference" => "CPU",
        var backend when backend.StartsWith("mixed:", StringComparison.Ordinal) => backend.Contains("cpu", StringComparison.Ordinal)
            ? "GPU + CPU (mixed detection)" : "GPU (multiple engines)",
        var backend when backend.StartsWith("webgpu:", StringComparison.Ordinal) => "WebGPU",
        var backend when backend.StartsWith("wgpu:", StringComparison.Ordinal) && backend.Contains("cpu", StringComparison.Ordinal) => "GPU + CPU",
        var backend when backend.StartsWith("wgpu:", StringComparison.Ordinal) => "GPU",
        _ => string.IsNullOrWhiteSpace(value) ? "CPU" : value,
    };

    private static string BackendTitle(string kind) => kind switch
    {
        "OrtDirectMl" => "DirectML",
        "OrtWebGpu" => "WebGPU",
        "OrtCpu" => "CPU inference",
        _ => kind,
    };

    private List<AccelRow> ParseAccel(JsonElement report)
    {
        var rows = new List<AccelRow>();
        var detect = "";
        var plane = "";
        var apply = "";
        if (report.TryGetProperty("assignment", out var assign) && assign.ValueKind == JsonValueKind.Object)
        {
            detect = FirstUsed(_usedDetect, Str(assign, "detect"));
            plane = FirstUsed(_usedPlane, Str(assign, "plane"));
            apply = FirstUsed(_usedApply, Str(assign, "apply"));
        }

        if (report.TryGetProperty("cpu", out var cpu))
        {
            var brand = Str(cpu, "brand");
            var simd = Str(cpu, "simd");
            var cpuBusy = _usedDetect == "cpu" || _usedPlane == "cpu" || _usedApply == "cpu" || _usedApply.EndsWith("+cpu", StringComparison.Ordinal);
            rows.Add(new AccelRow
            {
                Name = string.IsNullOrWhiteSpace(brand) ? "CPU" : brand,
                Status = cpuBusy ? "In use" : "Available",
                Detail = string.IsNullOrWhiteSpace(simd)
                    ? "Rayon + SIMD apply fallback"
                    : $"Rayon · {simd.ToUpperInvariant()} · fitting and LUT fallback",
            });
        }

        if (report.TryGetProperty("gpus", out var gpus) && gpus.ValueKind == JsonValueKind.Array)
        {
            foreach (var gpu in gpus.EnumerateArray().GroupBy(g => Str(g, "name"), StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            {
                var name = Str(gpu, "name");
                var vendor = Str(gpu, "vendor");
                var wgpuUsed = apply.StartsWith("wgpu:", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(name) && apply.Contains(name, StringComparison.OrdinalIgnoreCase);
                // Provider execution does not identify a particular GPU by vendor.
                // Only an actual adapter name from the LUT backend can mark this row in use.
                var inUse = wgpuUsed;
                var detailParts = new List<string>();
                if (wgpuUsed) detailParts.Add(apply.EndsWith("+cpu", StringComparison.Ordinal) ? "wgpu LUT + CPU recovery" : "wgpu LUT");
                var detail = detailParts.Count > 0
                    ? string.Join(" · ", detailParts)
                    : "Hardware detected; see provider status below";
                rows.Add(new AccelRow
                {
                    Name = string.IsNullOrWhiteSpace(name) ? vendor : name,
                    Status = inUse ? "In use" : "Seen",
                    Detail = detail,
                });
            }
        }

        if (report.TryGetProperty("backends", out var backends) && backends.ValueKind == JsonValueKind.Array)
        {
            foreach (var backend in backends.EnumerateArray())
            {
                var kind = Str(backend, "kind");

                var status = TitleStatus(Str(backend, "status"));
                if ((kind == "OrtDirectMl" && (_usedDetect == "directml" || _usedPlane == "directml"))
                    || (kind == "OrtWebGpu" && (_usedDetect is "webgpu" or "vulkan" || _usedPlane is "webgpu" or "vulkan")))
                    status = "In use";
                if (status is "Unavailable")
                {
                    continue;
                }

                var used = Str(backend, "used_for");
                var reason = Str(backend, "fail_reason");
                var detail = used is { Length: > 0 } && used != "unused"
                    ? used.Replace(",", " · ")
                    : reason is { Length: > 0 } reasonText
                        ? reasonText.Replace('_', ' ')
                        : "Ready if you pick this policy";
                rows.Add(new AccelRow
                {
                    Name = BackendTitle(kind),
                    Status = status,
                    Detail = detail,
                });
            }
        }

        return rows;
    }

    private static string TitleStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return "Available";
        }

        var s = status.Trim().Replace('_', ' ');
        if (s.Equals("active", StringComparison.OrdinalIgnoreCase)) return "In use";
        if (s.Equals("in use", StringComparison.OrdinalIgnoreCase)) return "In use";
        if (s.Equals("available", StringComparison.OrdinalIgnoreCase)) return "Available";
        if (s.Equals("detected", StringComparison.OrdinalIgnoreCase)) return "Seen";
        if (s.Equals("detected unused", StringComparison.OrdinalIgnoreCase)) return "Seen";
        if (s.Equals("seen", StringComparison.OrdinalIgnoreCase)) return "Seen";
        if (s.Equals("unused", StringComparison.OrdinalIgnoreCase)) return "Unused";
        if (s.Equals("failed", StringComparison.OrdinalIgnoreCase)) return "Failed";
        if (s.Equals("unavailable", StringComparison.OrdinalIgnoreCase)) return "Unavailable";
        if (s.Equals("planned", StringComparison.OrdinalIgnoreCase)) return "Planned";
        return char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
    }

    private static bool ActiveIs(JsonElement report, string kind) =>
        report.TryGetProperty("active", out var active) &&
        string.Equals(active.ToString(), kind, StringComparison.OrdinalIgnoreCase);

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string FirstUsed(string used, string planned) =>
        string.IsNullOrWhiteSpace(used) ? planned : used;
}
