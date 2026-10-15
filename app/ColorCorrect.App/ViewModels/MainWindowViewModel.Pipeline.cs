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
    [
        new("auto", "Auto — best for this PC"),
        new("cuda", "NVIDIA CUDA"),
        new("directml", "AMD / Intel DirectML"),
        new("openvino", "Intel OpenVINO"),
        new("mlx", "Apple MLX / Metal"),
        new("cpu", "CPU only"),
    ];
    public ObservableCollection<AccelRow> AccelRows { get; } = [];

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
    [ObservableProperty] private NamedValue _selectedAccelPolicy = new("auto", "Auto — best for this PC");
    private bool _accelSync;
    private bool _cudaOfferDismissed;
    private bool _cudaOfferAsked;
    private bool _cudaInstalling;
    [ObservableProperty] private bool _phase2Teacher = true;
    [ObservableProperty] private string _lastPreviewDir = "";
    [ObservableProperty] private bool _showCudaAddon;
    [ObservableProperty] private string _accelHint = "Probing CPU, CUDA, DirectML, OpenVINO, and Apple Metal…";
    private readonly Dictionary<string, Bitmap> _stageBitmaps = new(StringComparer.OrdinalIgnoreCase);
    private string _usedDetect = "";
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

    public bool HasStageFfc => DoFfc && (Active?.IsCorrected == true || StageFileExists("ffc.jpg"));
    public bool HasStageGc => DoGc && (Active?.IsCorrected == true || StageFileExists("gc.jpg"));
    public bool HasStageWb => DoWb && (Active?.IsCorrected == true || StageFileExists("wb.jpg"));
    public bool HasStageCc => DoCc && (Active?.IsCorrected == true || StageFileExists("cc.jpg") || StageFileExists("corrected.jpg"));
    public bool HasStageCorrected => HasCorrected || Active?.IsCorrected == true || StageFileExists("corrected.jpg");

    public string WhiteImageLabel => string.IsNullOrWhiteSpace(WhiteImagePath)
        ? "No white reference (FFC skips)"
        : Path.GetFileName(WhiteImagePath);

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

        if (CatalogFolders.Any(item => string.Equals(item.Path, folder, StringComparison.OrdinalIgnoreCase)))
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
        _worker.Policy = SelectedAccelPolicy.Value;
        await OnUi(() =>
        {
            WorkerReady = false;
            WorkerStartupComplete = false;
            WorkerStartupError = "";
            Status = "Restarting worker…";
        }).ConfigureAwait(false);
        try
        {
            await _worker.RestartAsync().ConfigureAwait(false);
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
    }

    [RelayCommand]
    private async Task RefreshAccelAsync()
    {
        try
        {
            var report = await _worker.DevicesAsync().ConfigureAwait(false);
            var raw = report.GetRawText();
            var rows = ParseAccel(report);
            var cudaOffer = OfferCudaAddon(report);
            var hint = AccelHintFrom(report);
            await OnUi(() =>
            {
                AccelJson = raw;
                AccelRows.Clear();
                foreach (var row in rows)
                {
                    AccelRows.Add(row);
                }

                ShowCudaAddon = cudaOffer;
                AccelHint = hint;
            }).ConfigureAwait(false);
            if (cudaOffer)
            {
                await MaybeOfferCudaDownloadAsync().ConfigureAwait(false);
            }
        }
        catch (System.Exception ex)
        {
            await OnUi(() => AccelJson = ex.Message).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private async Task OpenCudaAddon()
    {
        await InstallCudaLibTorchAsync(prompt: true).ConfigureAwait(false);
    }

    private async Task MaybeOfferCudaDownloadAsync()
    {
        if (_cudaOfferAsked || _cudaOfferDismissed || _cudaInstalling || ForceCpu)
        {
            return;
        }

        if (!string.Equals(SelectedAccelPolicy.Value, "auto", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(SelectedAccelPolicy.Value, "cuda", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _cudaOfferAsked = true;
        if (Dialogs is null)
        {
            return;
        }

        var ok = await Dispatcher.UIThread.InvokeAsync(() => Dialogs.ConfirmAsync(
            "NVIDIA GPU found",
            "ColorCorrect Studio can use CUDA for chart detection and explicitly selected CUDA LUT application. LibTorch 2.9 CUDA is installed separately in %LOCALAPPDATA%\\CCP-New\\libtorch-cuda (a multi-GB download from pytorch.org). NN fitting remains on CPU; Auto still-image application uses CPU. Plane crop uses DirectML when available.\n\nDownload CUDA LibTorch now?",
            "Download CUDA")).ConfigureAwait(false);
        if (!ok)
        {
            _cudaOfferDismissed = true;
            ScheduleSave();
            return;
        }

        await InstallCudaLibTorchAsync(prompt: false).ConfigureAwait(false);
    }

    private async Task InstallCudaLibTorchAsync(bool prompt)
    {
        if (_cudaInstalling)
        {
            return;
        }

        var script = LibTorchCudaInstaller.FindScript(_worker.RepoRoot, _worker.ExePath);
        if (string.IsNullOrWhiteSpace(script))
        {
            await OnUi(() =>
            {
                Status = "CUDA installer script is missing from this build.";
                Log("Accel", "install_libtorch_cuda.ps1 not found.", LogLevel.Error);
            }).ConfigureAwait(false);
            return;
        }

        if (prompt && Dialogs is not null)
        {
            var ok = await Dispatcher.UIThread.InvokeAsync(() => Dialogs.ConfirmAsync(
                "Install CUDA LibTorch",
                "Download official LibTorch 2.9.1 + CUDA 12.6 into %LOCALAPPDATA%\\CCP-New\\libtorch-cuda? The CPU runtime is preserved. CUDA accelerates chart detection and eligible LUT image application, not NN fitting or every correction stage. The worker will restart with the add-on.",
                "Download")).ConfigureAwait(false);
            if (!ok)
            {
                return;
            }
        }

        _cudaInstalling = true;
        try
        {
            await OnUi(() =>
            {
                Busy = true;
                Status = "Downloading CUDA LibTorch…";
            }).ConfigureAwait(false);

            var progress = new Progress<string>(line =>
            {
                _ = OnUi(() => Status = line.Length > 96 ? line[..96] + "…" : line);
            });
            var (ok, log) = await LibTorchCudaInstaller.InstallAsync(script, progress).ConfigureAwait(false);
            if (!ok)
            {
                await OnUi(() =>
                {
                    Status = "CUDA LibTorch download failed. CPU stays in use.";
                    Log("Accel", "CUDA LibTorch install failed.", LogLevel.Error,
                        string.IsNullOrWhiteSpace(log) ? [] : [log]);
                }).ConfigureAwait(false);
                return;
            }

            _cudaOfferDismissed = true;
            // A portable launcher may have inherited the bundled CPU override.
            // Switch only this app process; no machine/user environment changes.
            Environment.SetEnvironmentVariable("CCP_LIBTORCH_ROOT", LibTorchCudaInstaller.DestRoot());
            Environment.SetEnvironmentVariable("LIBTORCH_PATH", LibTorchCudaInstaller.DestRoot());
            await OnUi(() =>
            {
                ShowCudaAddon = false;
                Status = "CUDA LibTorch installed. Restarting worker…";
                Log("Accel", "CUDA LibTorch installed.", LogLevel.Success);
            }).ConfigureAwait(false);
            ScheduleSave();
            await ForceCpuRestartAsync().ConfigureAwait(false);
        }
        finally
        {
            _cudaInstalling = false;
            await OnUi(() => Busy = false).ConfigureAwait(false);
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
        UpdateCcSummary();
        if (!_restoring) ScheduleSave();
    }
    partial void OnDoGcChanged(bool value)
    {
        NotifyStageRadios();
        UpdateCcSummary();
        if (!_restoring) ScheduleSave();
    }
    partial void OnDoWbChanged(bool value)
    {
        NotifyStageRadios();
        UpdateCcSummary();
        if (!_restoring) ScheduleSave();
    }
    partial void OnDoCcChanged(bool value)
    {
        NotifyStageRadios();
        UpdateCcSummary();
        if (!_restoring) ScheduleSave();
    }
    partial void OnSelectedFfcFitMethodChanged(NamedValue value)
    {
        ShowFfcNn = value.Value == "nn";
        ShowFfcIter = value.Value is "nn" or "pls";
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
        if (_restoring || _accelSync)
        {
            return;
        }

        _accelSync = true;
        try
        {
            SelectedAccelPolicy = Pick(AccelPolicies, value ? "cpu" : "auto");
            _worker.Policy = SelectedAccelPolicy.Value;
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

    partial void OnSelectedAccelPolicyChanged(NamedValue value)
    {
        if (_restoring || _accelSync)
        {
            return;
        }

        _accelSync = true;
        try
        {
            ForceCpu = value.Value == "cpu";
            _worker.Policy = value.Value;
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
    }

    private void NotifyStageRadios()
    {
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
        foreach (var bmp in _stageBitmaps.Values)
        {
            if (!ReferenceEquals(bmp, OriginalPreview)
                && !ReferenceEquals(bmp, CorrectedPreview)
                && !ReferenceEquals(bmp, Preview))
            {
                bmp.Dispose();
            }
        }

        _stageBitmaps.Clear();
        SyncStagePreviewLoading();
    }

    private void ShowStageBitmap(Bitmap next)
    {
        var old = Preview;
        Preview = next;
        if (old is not null
            && !ReferenceEquals(old, next)
            && !ReferenceEquals(old, OriginalPreview)
            && !ReferenceEquals(old, CorrectedPreview)
            && !_stageBitmaps.ContainsValue(old))
        {
            old.Dispose();
        }

        SyncStagePreviewLoading();
    }

    private void ApplyStagePreview()
    {
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

        var model = File.Exists(item.ModelPath) ? item.ModelPath : LoadedModelPath;
        var previewDir = !string.IsNullOrWhiteSpace(item.PreviewDir) ? item.PreviewDir : LastPreviewDir;
        if (!File.Exists(model) || string.IsNullOrWhiteSpace(previewDir))
        {
            return;
        }

        var gen = ++_stageLoadGen;
        var ok = false;
        try
        {
            ok = await _client.PreviewStageAsync(item.Path, model, previewDir, key, BuildOptions()).ConfigureAwait(false);
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

                SyncStagePreviewLoading();
            }).ConfigureAwait(false);
        }
    }

    internal void RememberJobAccel(string? plane, string? apply, string? detect = null)
    {
        if (!string.IsNullOrWhiteSpace(detect))
        {
            _usedDetect = detect.Trim();
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
        if (report.TryGetProperty("assignment", out var assign) && assign.ValueKind == JsonValueKind.Object)
        {
            var detect = FirstUsed(_usedDetect, Str(assign, "detect"));
            var plane = FirstUsed(_usedPlane, Str(assign, "plane"));
            var apply = FirstUsed(_usedApply, Str(assign, "apply"));
            var threads = assign.TryGetProperty("threads", out var t) && t.TryGetInt32(out var n) ? n : 0;
            var rec = report.TryGetProperty("recommended", out var recEl) && recEl.ValueKind == JsonValueKind.String
                ? recEl.GetString() ?? ""
                : "";
            var used = "";
            if (!string.IsNullOrWhiteSpace(_usedPlane) || !string.IsNullOrWhiteSpace(_usedApply))
            {
                used = "Last job";
                if (!string.IsNullOrWhiteSpace(_usedPlane))
                {
                    used += $" · Plane {LabelStage(_usedPlane)}";
                }

                if (!string.IsNullOrWhiteSpace(_usedApply))
                {
                    used += $" · Apply {LabelStage(_usedApply)}";
                }

                used += "\n";
            }
            var stages = $"{used}Detect {LabelStage(detect)} · Plane {LabelStage(plane)} · Apply {LabelStage(apply)}";
            if (threads > 0)
            {
                stages += $" · {threads} CPU threads";
            }

            return string.IsNullOrWhiteSpace(rec) ? stages : $"{rec}\n{stages}";
        }

        return report.TryGetProperty("active", out var active)
            ? $"Active backend {active}"
            : "Auto-detect ready.";
    }

    private static string LabelStage(string value) => value.Trim().ToLowerInvariant() switch
    {
        "cuda" => "CUDA",
        "directml" => "DirectML",
        "openvino" => "OpenVINO",
        "mps" => "Metal",
        "coreml" => "CoreML",
        "mlx" => "MLX",
        "cpu" => "CPU",
        _ => string.IsNullOrWhiteSpace(value) ? "CPU" : value,
    };

    private static string BackendTitle(string kind) => kind switch
    {
        "LibTorchCpu" => "LibTorch CPU",
        "LibTorchCuda" => "LibTorch CUDA",
        "LibTorchMps" => "LibTorch Metal",
        "OrtCuda" => "ONNX CUDA",
        "OrtDirectMl" => "ONNX DirectML",
        "OrtOpenVino" => "ONNX OpenVINO",
        "OrtCoreMl" => "ONNX CoreML",
        "Mlx" => "MLX",
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
            var cpuBusy = detect is "cpu" or "" || plane is "cpu" or "" || apply is "cpu" or "";
            rows.Add(new AccelRow
            {
                Name = string.IsNullOrWhiteSpace(brand) ? "CPU" : brand,
                Status = cpuBusy ? "In use" : "Available",
                Detail = string.IsNullOrWhiteSpace(simd)
                    ? "Rayon + SIMD apply fallback"
                    : $"Rayon · {simd.ToUpperInvariant()} · detect/plane/apply fallback",
            });
        }

        if (report.TryGetProperty("gpus", out var gpus) && gpus.ValueKind == JsonValueKind.Array)
        {
            foreach (var gpu in gpus.EnumerateArray())
            {
                var name = Str(gpu, "name");
                var vendor = Str(gpu, "vendor");
                var nvidia = vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                             || name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
                var amd = vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase);
                var intel = vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase);
                var apple = vendor.Contains("Apple", StringComparison.OrdinalIgnoreCase);
                var inUse = (nvidia && (detect == "cuda" || apply == "cuda" || plane == "cuda"))
                            || (amd && (detect == "directml" || apply == "directml" || plane == "directml"))
                            || (intel && (detect is "openvino" or "directml"
                                          || apply is "openvino" or "directml"
                                          || plane is "openvino" or "directml"))
                            || (apple && (detect is "mps" or "mlx" || apply is "mps" or "mlx" || plane == "coreml"));
                var detailParts = new List<string>();
                if (nvidia)
                {
                    if (detect == "cuda") detailParts.Add("LibTorch detect");
                    if (apply == "cuda") detailParts.Add("LibTorch apply");
                    if (plane == "cuda") detailParts.Add("ONNX CUDA plane");
                    else if (plane == "directml") detailParts.Add("ONNX DirectML plane");
                }
                else if (amd || intel)
                {
                    if (detect == "directml") detailParts.Add("ONNX DirectML detect");
                    if (apply == "directml") detailParts.Add("ONNX DirectML apply");
                    if (plane == "directml") detailParts.Add("ONNX DirectML plane");
                    if (plane == "openvino") detailParts.Add("OpenVINO plane");
                }
                else if (apple)
                {
                    detailParts.Add("LibTorch Metal detect/apply · CoreML plane · MLX when linked");
                }
                var detail = detailParts.Count > 0
                    ? string.Join(" · ", detailParts)
                    : "Assigned from the policy above";
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
                if (kind is "LibTorchCpu")
                {
                    continue;
                }

                var status = TitleStatus(Str(backend, "status"));
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

    private static bool OfferCudaAddon(JsonElement report)
    {
        var kind = report.TryGetProperty("libtorch", out var torch) ? Str(torch, "kind") : "";
        if (!kind.Equals("cpu", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!report.TryGetProperty("gpus", out var gpus) || gpus.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return gpus.EnumerateArray().Any(gpu =>
        {
            var blob = Str(gpu, "vendor") + " " + Str(gpu, "name");
            return blob.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool ActiveIs(JsonElement report, string kind) =>
        report.TryGetProperty("active", out var active) &&
        string.Equals(active.ToString(), kind, StringComparison.OrdinalIgnoreCase);

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string FirstUsed(string used, string planned) =>
        string.IsNullOrWhiteSpace(used) ? planned : used;
}
