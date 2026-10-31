using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using ColorCorrect.App.Controls;
using ColorCorrect.App.Models;
using ColorCorrect.App.Services;
using ColorCorrect.Engine;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ColorCorrect.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    public const int MaxWorking = 80;

    private readonly SpeedyWorker _worker = new();
    private readonly CorrectionClient _client;
    private readonly SemaphoreSlim _run = new(1, 1);
    private readonly HashSet<string> _trusted = new(PathIdentity.Comparer);
    private readonly List<CatalogItemViewModel> _catalog = [];
    private CancellationTokenSource? _jobCts;
    private CancellationTokenSource? _saveCts;
    private CancellationTokenSource? _cropCts;
    private SessionItemViewModel? _rangeAnchor;
    private PickedColor? _lastPick;
    private int _peekX;
    private int _peekY;
    private int _previewGeneration;
    private int _sourceWidth = 1;
    private int _sourceHeight = 1;
    private int _plotHoverIndex = -1;
    private bool _pickerPinned;
    private bool _started;
    private bool _restoring;
    private readonly bool _persistenceEnabled;
    private readonly Dictionary<string, CachedOriginal> _originalCache = new(PathIdentity.Comparer);
    // A background decode can lend a cached original to a queued UI update.
    // Cache eviction must not release that bitmap before the update consumes it.
    private readonly Dictionary<Bitmap, int> _originalLeases = new();
    private readonly object _originalCacheLock = new();
    private bool _disposed;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    private const int OriginalCacheLimit = 8;

    private sealed class CachedOriginal
    {
        public required Bitmap Bitmap { get; init; }
        public long Length { get; init; }
        public long MtimeTicks { get; init; }
    }

    public MainWindowViewModel() : this(persistenceEnabled: true)
    {
    }

    internal MainWindowViewModel(bool persistenceEnabled)
    {
        _persistenceEnabled = persistenceEnabled;
        _client = new CorrectionClient(_worker);
        Session.CollectionChanged += (_, _) => RefreshBatchSubscriptions();
        Methods = Map(ColorSpaces.Methods);
        RgbSpaces = Map(ColorSpaces.RgbSpaces);
        WhitePoints = Map(ColorSpaces.WhitePoints);
        FfcFitMethods = Map(ColorSpaces.FfcFitMethods);
        NnOptimizers =
        [
            new NamedValue("adam", "Adam"),
            new NamedValue("sgd", "SGD · momentum"),
            new NamedValue("rmsprop", "RMSProp"),
        ];
        OutputFormats = [new NamedValue("png", "PNG"), new NamedValue("jpg", "JPEG")];
        SelectedMethod = Methods[0];
        SelectedFfcFitMethod = Pick(FfcFitMethods, "poly");
        SelectedNnOptimizer = NnOptimizers[0];
        SelectedInputSpace = Pick(RgbSpaces, "srgb");
        SelectedInputWhite = Pick(WhitePoints, "d65");
        SelectedTargetSpace = Pick(RgbSpaces, "srgb");
        SelectedTargetWhite = Pick(WhitePoints, "d65");
        SelectedOutputFormat = OutputFormats[1];
        SyncDegreeAndAdvanced();
        HookWorkerProgress();
        if (_persistenceEnabled)
        {
            var hasSavedSettings = File.Exists(SessionStore.SettingsPath()) || File.Exists(SessionStore.FilePath());
            RestoreSession();
            if (!hasSavedSettings)
            {
                ApplyInstallationDefaults();
                ShowFirstRunSetup = File.Exists(SetupHelperPath) && !File.Exists(InstalledSetup.StatePath);
                if (ShowFirstRunSetup) SetupSummary = "Choose Recommended, CPU only, or Custom. Check setup verifies installed support; installation shows the proposed changes before applying them.";
            }
            LoadSetupSelection();
        }
        else
        {
            _restoring = true;
            try
            {
                LoadCatalog();
                RefreshSessionUi();
            }
            finally
            {
                _restoring = false;
            }
        }
        Log("Ready", "Add images to begin.");
    }

    public IAppDialogs? Dialogs { get; set; }

    public IReadOnlyList<NamedValue> Methods { get; }
    public IReadOnlyList<NamedValue> RgbSpaces { get; }
    public IReadOnlyList<NamedValue> WhitePoints { get; }
    public IReadOnlyList<NamedValue> OutputFormats { get; }
    public IReadOnlyList<NamedValue> NnOptimizers { get; }
    public ObservableCollection<NamedValue> DegreeOptions { get; } = [];
    public ObservableCollection<SessionItemViewModel> Session { get; } = [];
    public ObservableCollection<CatalogItemViewModel> FilteredSamples { get; } = [];
    public ObservableCollection<CatalogFolderViewModel> CatalogFolders { get; } = [];
    public ObservableCollection<string> SampleGroups { get; } = [];
    public ObservableCollection<PatchRowViewModel> Patches { get; } = [];
    public ObservableCollection<StageDeltaERow> StageDeltaERows { get; } = [];
    public ObservableCollection<LogEntry> LogEntries { get; } = [];
    public ObservableCollection<PipelineStepViewModel> PipelineSteps { get; } = [];

    [ObservableProperty] private string _status = "Starting ColorCorrect engine…";
    [ObservableProperty] private string _statusDeltaE = "";
    [ObservableProperty] private string _workerLine = "Worker: starting";
    [ObservableProperty] private bool _workerStartupComplete;
    [ObservableProperty] private string _workerStartupError = "";
    [ObservableProperty] private string _detectSummary = "Waiting for detect";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _currentStage = "";
    [ObservableProperty] private string _ccSummary = "Linear · degree 2";
    [ObservableProperty] private string _processSummary = "FFC off · GC 5 · WB · CC Root polynomial";
    [ObservableProperty] private string _savedModelSummary = "";
    [ObservableProperty] private bool _hasSavedModelSummary;
    [ObservableProperty] private string _degreeLabel = "Polynomial degree";
    [ObservableProperty] private string _modelLabel = "No model loaded";
    [ObservableProperty] private string _modelIoStatus = "";
    [ObservableProperty] private string _loadedModelPath = "";
    [ObservableProperty] private bool _useLoadedModel;
    [ObservableProperty] private string _outputDir = "";
    [ObservableProperty] private string _sessionCountLabel = "0";
    [ObservableProperty] private string _sampleCountLabel = "0";
    [ObservableProperty] private string _sampleQuery = "";
    [ObservableProperty] private string _selectedGroup = "All";
    [ObservableProperty] private string _imageMeta = "";
    [ObservableProperty] private string _pickerHint = "Hover for RGB / Lab · scroll to zoom · drag to pan";
    [ObservableProperty] private string _pickerPixel = "";
    [ObservableProperty] private string _pickerRgb = "";
    [ObservableProperty] private string _pickerLab = "";
    [ObservableProperty] private string _pickerR = "";
    [ObservableProperty] private string _pickerG = "";
    [ObservableProperty] private string _pickerB = "";
    [ObservableProperty] private string _pickerHex = "";
    [ObservableProperty] private string _pickerL = "";
    [ObservableProperty] private string _pickerA = "";
    [ObservableProperty] private string _pickerLabB = "";
    [ObservableProperty] private string _meanAfter = "--";
    [ObservableProperty] private string _meanBefore = "--";
    [ObservableProperty] private string _meanQuality = "";
    [ObservableProperty] private string _medianAfter = "--";
    [ObservableProperty] private string _medianBefore = "--";
    [ObservableProperty] private string _medianQuality = "";
    [ObservableProperty] private string _maxAfter = "--";
    [ObservableProperty] private string _maxBefore = "--";
    [ObservableProperty] private string _maxQuality = "";
    [ObservableProperty] private string _modelMetric = "--";
    [ObservableProperty] private string _featureDetail = "--";
    [ObservableProperty] private string _validationSummary = "";
    [ObservableProperty] private string _patchSummary = "";
    [ObservableProperty] private string _batchLabel = "";
    [ObservableProperty] private string _batchProgressPct = "0%";
    [ObservableProperty] private string _overlayBadge = "";
    [ObservableProperty] private string _themeLabel = "Light";
    [ObservableProperty] private string _themeHint = "Switch to light theme";
    [ObservableProperty] private IBrush? _pickerBrush;
    [ObservableProperty] private Bitmap? _originalPreview;
    [ObservableProperty] private Bitmap? _correctedPreview;
    [ObservableProperty] private Bitmap? _preview;
    [ObservableProperty] private HandshakeDocument? _overlayHandshake;
    [ObservableProperty] private FfcPlane? _overlayPlane;
    [ObservableProperty] private Rect _overlayPlaneRect;
    [ObservableProperty] private bool _showPlane = true;
    [ObservableProperty] private CorrectionReport? _report;
    private FfcPlane? _loadedModelPlane;
    [ObservableProperty] private double _overlayCrop = 0.75;
    [ObservableProperty] private double _meanAfterValue = double.NaN;
    [ObservableProperty] private double _medianAfterValue = double.NaN;
    [ObservableProperty] private double _maxAfterValue = double.NaN;
    [ObservableProperty] private SessionItemViewModel? _active;
    [ObservableProperty] private NamedValue _selectedMethod = new("linear", "Linear");
    [ObservableProperty] private NamedValue? _selectedDegree;
    [ObservableProperty] private NamedValue _selectedInputSpace = new("srgb", "sRGB");
    [ObservableProperty] private NamedValue _selectedInputWhite = new("d65", "D65");
    [ObservableProperty] private NamedValue _selectedTargetSpace = new("srgb", "sRGB");
    [ObservableProperty] private NamedValue _selectedTargetWhite = new("d65", "D65");
    [ObservableProperty] private NamedValue _selectedOutputFormat = new("png", "PNG");
    [ObservableProperty] private double _crop = 0.75;
    [ObservableProperty] private double _naturalWidth = 1;
    [ObservableProperty] private double _naturalHeight = 1;
    [ObservableProperty] private double _overlayScaleX = 1;
    [ObservableProperty] private double _overlayScaleY = 1;
    [ObservableProperty] private double _loupeLeft;
    [ObservableProperty] private double _loupeTop;
    [ObservableProperty] private double _hoverCardLeft;
    [ObservableProperty] private double _hoverCardTop;
    [ObservableProperty] private double _pinX;
    [ObservableProperty] private double _pinY;
    [ObservableProperty] private int _loupePixelX;
    [ObservableProperty] private int _loupePixelY;
    [ObservableProperty] private double _batchProgress;
    [ObservableProperty] private decimal _fitSamples = 1;
    [ObservableProperty] private decimal _plsComponents;
    [ObservableProperty] private string _hiddenLayersText = "100";
    [ObservableProperty] private NamedValue _selectedNnOptimizer = new("adam", "Adam");
    [ObservableProperty] private bool _nnBatchNormalization;
    [ObservableProperty] private decimal _nnDropout;
    [ObservableProperty] private decimal _nnLr = 0.001m;
    [ObservableProperty] private decimal _nnEpochs = 10000;
    [ObservableProperty] private decimal _nnPatience = 1500;
    [ObservableProperty] private decimal _nnTol = 0.000001m;
    [ObservableProperty] private int _faceIndex;
    [ObservableProperty] private int _overlayFaceChoice;
    [ObservableProperty] private int _analysisTab;
    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private bool _workerReady;
    [ObservableProperty] private bool _isBatching;
    [ObservableProperty] private bool _showBatchProgress;
    [ObservableProperty] private bool _showPipeline;
    [ObservableProperty] private bool _showJobProgress;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private bool _showSaveProgress;
    [ObservableProperty] private double _jobProgress;
    [ObservableProperty] private double _saveProgress;
    [ObservableProperty] private string _jobProgressPct = "0%";
    [ObservableProperty] private string _saveProgressLabel = "";
    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private bool _hasImage;
    [ObservableProperty] private bool _hasCorrected;
    [ObservableProperty] private bool _hasMetrics;
    [ObservableProperty] private bool _hasSaveItems;
    [ObservableProperty] private bool _hasCatalogFolders;
    [ObservableProperty] private bool _hasWhiteRef;
    [ObservableProperty] private bool _saveCorrected = true;
    [ObservableProperty] private bool _saveFfc = true;
    [ObservableProperty] private bool _saveGc = true;
    [ObservableProperty] private bool _saveWb = true;
    [ObservableProperty] private bool _saveCc = true;
    [ObservableProperty] private bool _saveReport = true;
    [ObservableProperty] private bool _canSaveCorrected;
    [ObservableProperty] private bool _canSaveFfc;
    [ObservableProperty] private bool _canSaveGc;
    [ObservableProperty] private bool _canSaveWb;
    [ObservableProperty] private bool _canSaveCc;
    [ObservableProperty] private bool _canSaveReport;
    [ObservableProperty] private bool _hasSelected;
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private bool _hasPicker;
    [ObservableProperty] private bool _showLoupe;
    [ObservableProperty] private bool _showPin;
    [ObservableProperty] private bool _showCorrected;
    [ObservableProperty] private bool _showFacePicker;
    [ObservableProperty] private bool _useAllFaces;
    [ObservableProperty] private bool _showLattice = true;
    [ObservableProperty] private bool _showSample = true;
    [ObservableProperty] private bool _showDim = true;
    [ObservableProperty] private bool _showDegree = true;
    [ObservableProperty] private bool _showPolyCombinations = true;
    [ObservableProperty] private bool _includeCombinations = true;
    [ObservableProperty] private bool _showPls;
    [ObservableProperty] private bool _showNn;
    [ObservableProperty] private bool _showCcIterParams;
    [ObservableProperty] private bool _skipSaturated;
    [ObservableProperty] private bool _isLightTheme;
    [ObservableProperty] private bool _libraryExpanded;
    [ObservableProperty] private bool _processExpanded = true;
    [ObservableProperty] private bool _modelExpanded;
    [ObservableProperty] private bool _settingsExpanded;

    public ObservableCollection<string> FaceOptions { get; } = [];
    public string? SelectedFace
    {
        get => FaceIndex >= 0 && FaceIndex < FaceOptions.Count ? FaceOptions[FaceIndex] : null;
        set
        {
            if (value is null)
            {
                return;
            }

            var index = FaceOptions.IndexOf(value);
            if (index >= 0 && index != FaceIndex)
            {
                FaceIndex = index;
            }
        }
    }

    public string WorkerChip => ShortenWorkerLine(WorkerLine);
    public bool NnSettingsValid => !ShowNn || CorrectionOptions.TryParseHiddenLayers(HiddenLayersText, out _, out _);
    public bool HasHiddenLayersError => ShowNn && !CorrectionOptions.TryParseHiddenLayers(HiddenLayersText, out _, out _);
    public string HiddenLayersError => CorrectionOptions.TryParseHiddenLayers(HiddenLayersText, out _, out var error) ? "" : error;
    public bool ShowSessionNavigation => Session.Count > 1;
    public bool CanNavigatePrevious => Active is not null && Session.IndexOf(Active) > 0;
    public bool CanNavigateNext => Active is not null && Session.IndexOf(Active) >= 0 && Session.IndexOf(Active) < Session.Count - 1;
    public string ActivePositionLabel
    {
        get
        {
            var index = Active is null ? -1 : Session.IndexOf(Active);
            return index < 0 ? "" : $"{index + 1} of {Session.Count} · {Active!.DisplayName}";
        }
    }
    public string PreviousSessionHint => CanNavigatePrevious
        ? $"Previous image · {Session[Session.IndexOf(Active!) - 1].DisplayName} (Alt+Left)"
        : "No previous image";
    public string NextSessionHint => CanNavigateNext
        ? $"Next image · {Session[Session.IndexOf(Active!) + 1].DisplayName} (Alt+Right)"
        : "No next image";
    public ColorHoverState StageHover { get; } = new();
    public ColorHoverState PlotHover { get; } = new();
    public string CropLabel => Crop.ToString("0.00");
    public bool HasLoadedModel => File.Exists(LoadedModelPath);
    public bool WillUseLoadedModel => UseLoadedModel && HasLoadedModel;
    public bool HasWorkerError => WorkerStartupComplete && !WorkerReady;
    public bool CanOpenBatch => WorkerReady && !Busy && !IsSaving && Session.Count > 0;
    public string DetectActionLabel => Active is not null && _detectionFailures.ContainsKey(Active.Path) ? "Retry" : "Detect";
    public string RunActionHint => !WorkerReady ? "Wait for the processing engine, or check setup if it could not start."
        : Busy || IsSaving ? "Wait for the current operation, or cancel processing."
        : Active is null ? "Add an image to start."
        : !File.Exists(Active.Path) ? "Relink the missing image to continue."
        : OperationError(!WillUseLoadedModel) is { } error ? error
        : !WillUseLoadedModel && Active.Handshake is not { Ok: true } ? "Detect the chart, or retry detection, before fitting."
        : "Run the active workflow (F5).";
    public string BatchFitHint => !WorkerReady ? "The processing engine is not ready."
        : Busy || IsSaving ? "Wait for the current operation."
        : !Session.Any(item => item.IncludeInBatch) ? "Select at least one image for the batch."
        : OperationError(true) ?? "Detect a chart and fit each selected image.";
    public string BatchApplyHint => !File.Exists(LoadedModelPath) ? "Load or fit a model before applying it to a batch."
        : !Session.Any(item => item.IncludeInBatch) ? "Select at least one image for the batch."
        : OperationError(false) ?? "Apply the current model to each selected image.";
    public string StatusStateLabel => HasWorkerError
        ? "Engine error"
        : Busy
            ? "Working"
            : WorkerReady
                ? "Ready"
                : "Starting";
    public bool IsUseLoadedMode
    {
        get => WillUseLoadedModel;
        set
        {
            if (value)
            {
                if (HasLoadedModel)
                {
                    UseLoadedModel = true;
                }

                return;
            }

            UseLoadedModel = false;
        }
    }

    public bool IsFitFromImage
    {
        get => !WillUseLoadedModel;
        set
        {
            if (value)
            {
                UseLoadedModel = false;
            }
        }
    }

    public string ApplyLabel => WillUseLoadedModel ? "Apply model" : "Fit + apply";
    public string ModelModeHint => WillUseLoadedModel
        ? "Run applies the loaded GC/WB/CC. Method and degree are ignored. FFC (if on) is recomputed from the white still."
        : Active?.Handshake is { Ok: true }
            ? "Run fits a new model from this image and replaces the loaded model."
            : "Detect the chart on this image, then Run to fit a new model.";
    public string WorkflowHint => Active is null ? "Add a still, then Fit + apply."
        : WillUseLoadedModel ? "Using the loaded model. Run applies it to this still."
        : Active.Handshake is { Ok: true }
            ? Active.IsCorrected ? "Corrected. Fit + apply again retrains from this still."
                : "Chart detected. Fit + apply trains a new model from this still."
            : "Detect the chart, then Fit + apply.";
    public bool ShowStillPreview => HasImage;
    public bool ShowChartSection => HasImage;
    public bool ShowChartOverlay => OverlayHandshake is { Ok: true };
    public bool ShowOverlayLegend => ShowChartOverlay;
    public bool ShowPlaneOverlay => OverlayPlane is not null;
    public bool ShowOverlayChrome => ShowChartOverlay || ShowPlaneOverlay;
    public bool ShowPlaneToggle => ShowPlaneOverlay;
    public string PlaneBadge => OverlayPlane?.Badge ?? "";
    public bool ShowOriginal
    {
        get => !ShowCorrected;
        set
        {
            if (value)
            {
                ShowCorrected = false;
            }
        }
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        WorkerStartupComplete = false;
        WorkerStartupError = "";
        _ = StartWorkerAsync();
    }

    public Task LoadImageAsync(string path)
    {
        AddPaths([path]);
        return Task.CompletedTask;
    }

    public void AddPaths(IEnumerable<string> paths, bool collapseLibrary = true)
    {
        var incoming = paths.ToArray();
        if (!string.IsNullOrWhiteSpace(WhiteImagePath) && !File.Exists(WhiteImagePath))
        {
            var staleName = Path.GetFileName(WhiteImagePath);
            var replacements = incoming
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path) && IsImage(path))
                .Select(Path.GetFullPath)
                .Where(path => string.Equals(Path.GetFileName(path), staleName, StringComparison.OrdinalIgnoreCase))
                .Distinct(PathIdentity.Comparer)
                .ToArray();
            if (replacements.Length == 1)
            {
                UseWhitePath(replacements[0]);
                Log("FFC", $"Relinked moved white reference · {staleName}", LogLevel.Success, replacements);
            }
        }

        SessionItemViewModel? last = null;
        var added = false;
        var skipped = 0;
        var names = new List<string>();
        foreach (var path in incoming)
        {
            if (Session.Count >= MaxWorking)
            {
                Log("Session", $"Working set capped at {MaxWorking} images.", LogLevel.Warning);
                break;
            }

            if (!SupportedStillImage.IsSupportedFile(path))
            {
                skipped++;
                continue;
            }

            var full = Path.GetFullPath(path);
            if (Session.Any(item => PathIdentity.Equal(item.Path, full)))
            {
                last = Session.First(item => PathIdentity.Equal(item.Path, full));
                continue;
            }

            last = new SessionItemViewModel(full);
            if (!string.IsNullOrWhiteSpace(WhiteImagePath) && PathsEqual(full, WhiteImagePath))
            {
                last.IncludeInBatch = false;
            }
            Session.Add(last);
            names.Add(last.Name);
            added = true;
        }

        if (last is not null)
        {
            Activate(last);
        }

        RefreshSessionUi();
        ScheduleSave();
        ClearCatalogSelection();
        if (added)
        {
            if (collapseLibrary)
            {
                LibraryExpanded = false;
            }

            Log("Session", $"Added {names.Count} file(s).", LogLevel.Info, names);

        }
        if (skipped > 0)
        {
            Status = $"Skipped {skipped} unsupported or unreadable file(s); video is not supported.";
            Log("Import", Status, LogLevel.Warning);
        }
    }

    public void ClearCatalogSelection()
    {
        foreach (var sample in _catalog)
        {
            sample.IsSelected = false;
        }
    }

    public void HandleSessionClick(SessionItemViewModel item, bool toggle, bool range)
    {
        if (range && _rangeAnchor is not null)
        {
            var from = Session.IndexOf(_rangeAnchor);
            var to = Session.IndexOf(item);
            if (from >= 0 && to >= 0)
            {
                var lo = Math.Min(from, to);
                var hi = Math.Max(from, to);
                for (var i = 0; i < Session.Count; i++)
                {
                    Session[i].IsSelected = i >= lo && i <= hi;
                }
            }
        }
        else if (toggle)
        {
            item.IsSelected = !item.IsSelected;
        }
        else
        {
            foreach (var sessionItem in Session)
            {
                sessionItem.IsSelected = sessionItem == item;
            }
        }

        _rangeAnchor = item;
        Activate(item);
        HasSelected = Session.Any(sessionItem => sessionItem.IsSelected);
        RemoveSelectedCommand.NotifyCanExecuteChanged();
    }

    public void HandleCatalogClick(CatalogItemViewModel item, bool toggle)
    {
        if (toggle)
        {
            item.IsSelected = !item.IsSelected;
        }
        else
        {
            foreach (var sample in _catalog)
            {
                sample.IsSelected = sample == item;
            }

            AddPaths([item.Path], collapseLibrary: false);
        }
    }

    public IReadOnlyList<string> CatalogDragPaths(CatalogItemViewModel item)
    {
        var selected = _catalog.Where(sample => sample.IsSelected).Select(sample => sample.Path).ToList();
        if (selected.Count == 0)
        {
            return [item.Path];
        }

        if (!selected.Contains(item.Path, PathIdentity.Comparer))
        {
            selected.Add(item.Path);
        }

        return selected;
    }

    public IReadOnlyList<string> SessionDragPaths(SessionItemViewModel item)
    {
        var selected = Session.Where(sample => sample.IsSelected).Select(sample => sample.Path).ToList();
        if (selected.Count > 0 && selected.Contains(item.Path, PathIdentity.Comparer))
        {
            return selected;
        }

        return [item.Path];
    }

    public void PeekColor(int x, int y, bool pin, bool showLoupe = true)
    {
        var bitmap = Preview;
        if (bitmap is null || !TryBitmapSize(bitmap, out var previewWidth, out var previewHeight))
        {
            if (showLoupe)
            {
                ShowLoupe = false;
            }

            return;
        }

        var previewX = Math.Clamp(x, 0, previewWidth - 1);
        var previewY = Math.Clamp(y, 0, previewHeight - 1);
        if (_pickerPinned && !pin)
        {
            return;
        }

        if (!pin && HasPicker && previewX == _peekX && previewY == _peekY)
        {
            return;
        }

        if (!PixelSampler.TrySample(bitmap, previewX, previewY, out var sampled))
        {
            if (showLoupe)
            {
                ShowLoupe = false;
            }

            return;
        }

        var sourceX = MapPreviewToSource(previewX, previewWidth, _sourceWidth);
        var sourceY = MapPreviewToSource(previewY, previewHeight, _sourceHeight);
        var picked = sampled with { X = sourceX, Y = sourceY };
        _lastPick = picked;
        _peekX = previewX;
        _peekY = previewY;
        HasPicker = true;
        PickerHint = "";
        PickerPixel = picked.Pixel;
        PickerRgb = picked.Rgb;
        PickerLab = picked.Lab;
        PickerR = picked.R.ToString();
        PickerG = picked.G.ToString();
        PickerB = picked.B.ToString();
        PickerHex = picked.Hex;
        PickerL = picked.L.ToString("0.00");
        PickerA = picked.A.ToString("0.00");
        PickerLabB = picked.LabB.ToString("0.00");
        if (PickerBrush is SolidColorBrush brush)
        {
            brush.Color = picked.Color;
        }
        else
        {
            PickerBrush = new SolidColorBrush(picked.Color);
        }

        LoupePixelX = previewX;
        LoupePixelY = previewY;
        ShowLoupe = showLoupe;
        if (pin)
        {
            _pickerPinned = true;
            ShowPin = true;
            PinX = x;
            PinY = y;
        }
    }

    public void PlaceLoupe(double pointerX, double pointerY, double hostW, double hostH)
    {
        const double size = 88;
        const double offset = 18;
        var left = pointerX + offset;
        var top = pointerY - size - 8;
        if (left + size > hostW - 8)
        {
            left = pointerX - size - offset;
        }

        if (top < 8)
        {
            top = pointerY + offset;
        }

        LoupeLeft = Math.Max(0, left);
        LoupeTop = Math.Max(0, top);

        const double cardW = 252;
        const double cardH = 118;
        const double gap = 10;
        var cardLeft = LoupeLeft + size + gap;
        var cardTop = LoupeTop + (size - cardH) / 2;
        if (cardLeft + cardW > hostW - 8)
        {
            cardLeft = LoupeLeft - cardW - gap;
        }

        if (cardLeft < 8)
        {
            cardLeft = Math.Clamp(pointerX + offset, 8, Math.Max(8, hostW - cardW - 8));
            cardTop = LoupeTop + size + gap;
            if (cardTop + cardH > hostH - 8)
            {
                cardTop = LoupeTop - cardH - gap;
            }
        }

        HoverCardLeft = Math.Clamp(cardLeft, 8, Math.Max(8, hostW - cardW - 8));
        HoverCardTop = Math.Clamp(cardTop, 8, Math.Max(8, hostH - cardH - 8));
    }

    public void HideLoupe() => ShowLoupe = false;

    public void PeekPlot(PatchRow row, int index, bool corrected, double x, double y, double hostW, double hostH)
    {
        if (index != _plotHoverIndex || !PlotHover.IsVisible)
        {
            PlotHover.ShowPatch(row, corrected);
            _plotHoverIndex = index;
        }

        PlotHover.Place(x, y, hostW, hostH, 392, 206);
    }

    public void HidePlotHover()
    {
        _plotHoverIndex = -1;
        PlotHover.Hide();
    }

    public void ClearPicker()
    {
        _lastPick = null;
        _pickerPinned = false;
        HasPicker = false;
        ShowLoupe = false;
        ShowPin = false;
        StageHover.Hide();
        HidePlotHover();
        PickerHint = "Hover for RGB / Lab. Right-click a point to copy. Esc clears the pin.";
        PickerPixel = PickerRgb = PickerLab = "";
        PickerR = PickerG = PickerB = PickerHex = "";
        PickerL = PickerA = PickerLabB = "";
        PickerBrush = null;
    }

    [RelayCommand]
    private async Task OpenImagesAsync()
    {
        if (Dialogs is null)
        {
            return;
        }

        AddPaths(await Dialogs.OpenImagesAsync());
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        if (Dialogs is null)
        {
            return;
        }

        AddPaths(await Dialogs.OpenFolderImagesAsync());
    }

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void ClearSession()
    {
        foreach (var item in Session)
        {
            item.Dispose();
        }

        Session.Clear();
        Active = null;
        _rangeAnchor = null;
        LastPreviewDir = "";
        SetPreview(null, null);
        OverlayHandshake = null;
        ApplyReport(null);
        DetectSummary = "Waiting for detect";
        HasImage = false;
        HasCorrected = false;
        ShowFacePicker = false;
        FaceOptions.Clear();
        ClearPicker();
        Log("Session", "Cleared session images.", LogLevel.Warning);
        ClearCatalogSelection();
        RefreshSessionUi();
        ScheduleSave();
    }

    [RelayCommand]
    private async Task DeletePreviousSessionsAsync()
    {
        var root = RunPaths.RunsRoot(_worker.RepoRoot);
        var dirs = Directory.Exists(root)
            ? Directory.GetDirectories(root)
                .Where(dir => !string.Equals(Path.GetFileName(dir), "saved", StringComparison.OrdinalIgnoreCase))
                .ToList()
            : [];
        var sessionCount = Session.Count;
        if (dirs.Count == 0 && sessionCount == 0)
        {
            Status = "No previous session to delete.";
            return;
        }

        if (Dialogs is null)
        {
            return;
        }

        var ok = await Dialogs.ConfirmAsync(
            "Delete previous sessions?",
            $"Remove the working set ({sessionCount} image(s)) and {dirs.Count} run folder(s) under assets/runs.\n\nFFC, GC, WB, CC, theme, and overlay settings stay. The saved/ folder is kept.",
            "Delete",
            true);
        if (!ok)
        {
            return;
        }

        var deleted = 0;
        foreach (var dir in dirs)
        {
            try
            {
                Directory.Delete(dir, true);
                deleted++;
            }
            catch (Exception ex)
            {
                Log("Session", $"Could not delete {Path.GetFileName(dir)}.", LogLevel.Warning, [ex.Message]);
            }
        }

        if (!string.IsNullOrWhiteSpace(LastPreviewDir) && !Directory.Exists(LastPreviewDir))
        {
            LastPreviewDir = "";
        }

        if (!string.IsNullOrWhiteSpace(LoadedModelPath) && !File.Exists(LoadedModelPath))
        {
            LoadedModelPath = "";
            _loadedModelPlane = null;
        }

        ClearSession();
        Status = $"Deleted {deleted} run folder(s). Settings kept.";
        Log("Session", Status, LogLevel.Warning, [$"{deleted} of {dirs.Count} folders removed"]);
        FlushPersist();
    }

    public void FlushPersist()
    {
        _saveCts?.Cancel();
        if (_persistenceEnabled && !_restoring)
        {
            Persist();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void RemoveSelected()
    {
        var victims = Session.Where(item => item.IsSelected).ToList();
        if (victims.Count == 0 && Active is not null)
        {
            victims.Add(Active);
        }

        RemoveItems(victims);
    }

    [RelayCommand]
    private void RemoveItem(SessionItemViewModel? item)
    {
        if (item is not null)
        {
            RemoveItems([item]);
        }
    }

    private void RemoveItems(IReadOnlyList<SessionItemViewModel> victims)
    {
        if (victims.Count == 0)
        {
            return;
        }

        var names = victims.Select(item => item.Name).ToList();
        foreach (var item in victims)
        {
            Session.Remove(item);
            item.Dispose();
        }

        Log("Session", $"Removed {names.Count} image(s).", LogLevel.Info, names);

        if (Active is not null && !Session.Contains(Active))
        {
            if (Session.Count > 0)
            {
                Activate(Session[0]);
            }
            else
            {
                ClearSession();
                return;
            }
        }

        RefreshSessionUi();
        ScheduleSave();
    }

    [RelayCommand(CanExecute = nameof(CanOpenReport))]
    private void OpenReport()
    {
        var path = Active?.ReportPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Status = "No report for the active image.";
            return;
        }

        OpenOsPath(path);
        Log("Report", path);
    }

    [RelayCommand]
    private void OpenOutput()
    {
        var dir = ResolvedOutputDir();
        Directory.CreateDirectory(dir);
        OpenOsPath(dir);
        Log("Output", $"Opened {dir}.");
    }

    [RelayCommand]
    private void SelectItem(SessionItemViewModel? item)
    {
        if (item is not null)
        {
            HandleSessionClick(item, toggle: false, range: false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanNavigatePrevious))]
    private void PreviousSession() => NavigateSession(-1);

    [RelayCommand(CanExecute = nameof(CanNavigateNext))]
    private void NextSession() => NavigateSession(1);

    private void NavigateSession(int delta)
    {
        if (Active is null)
        {
            return;
        }

        var index = Session.IndexOf(Active);
        var next = index + delta;
        if (next >= 0 && next < Session.Count)
        {
            HandleSessionClick(Session[next], toggle: false, range: false);
        }
    }

    [RelayCommand]
    private void AddSample(CatalogItemViewModel? item)
    {
        if (item is not null)
        {
            AddPaths([item.Path], collapseLibrary: false);
        }
    }

    [RelayCommand]
    private void ToggleTheme() => IsLightTheme = !IsLightTheme;

    [RelayCommand]
    private Task CopyPickerAsync() => CopyPickerTextAsync(null);

    [RelayCommand]
    private Task CopyPickerRgbAsync() => CopyPickerTextAsync("rgb");

    [RelayCommand]
    private Task CopyPickerLabAsync() => CopyPickerTextAsync("lab");

    [RelayCommand]
    private Task CopyPickerHexAsync() => CopyPickerTextAsync("hex");

    private async Task CopyPickerTextAsync(string? part)
    {
        if (_lastPick is not { } pick || Dialogs is null)
        {
            Status = "Right-click a point on the image first.";
            return;
        }

        var text = part switch
        {
            "rgb" => $"{pick.R}, {pick.G}, {pick.B}",
            "lab" => $"{pick.L:0.00}, {pick.A:0.00}, {pick.LabB:0.00}",
            "hex" => pick.Hex,
            _ => pick.CopyLine,
        };
        await Dialogs.SetClipboardAsync(text);
        Status = part switch
        {
            "rgb" => "Copied RGB.",
            "lab" => "Copied Lab.",
            "hex" => "Copied hex.",
            _ => "Copied pixel color.",
        };
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        if (Dialogs is null)
        {
            return;
        }

        var folder = await Dialogs.PickFolderAsync("Output folder for corrected images");
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        OutputDir = folder;
        if (!await EnsureOutputAllowedAsync())
        {
            OutputDir = "";
            Log("Output", "Folder not confirmed.", LogLevel.Warning);
            return;
        }

        Log("Output", $"Folder set to {folder}.");
        ScheduleSave();
    }

    [RelayCommand]
    private async Task LoadModelAsync()
    {
        if (Dialogs is null)
        {
            return;
        }

        var path = await Dialogs.OpenJsonAsync("Load correction model");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        LoadedModelPath = path;
        RestoreLoadedModelSettings(path);
        UseLoadedModel = true;
        _loadedModelPlane = FfcPlane.TryLoad(path);
        ModelIoStatus = $"Loaded {Path.GetFileName(path)}";
        Log("Model", $"Loaded {Path.GetFileName(path)}.", LogLevel.Success);
        RefreshOverlay();
        NotifyRunCommands();
        ScheduleSave();
    }

    [RelayCommand(CanExecute = nameof(CanSaveModel))]
    private async Task SaveModelAsync()
    {
        if (Dialogs is null || !File.Exists(LoadedModelPath))
        {
            return;
        }

        var dest = await Dialogs.SaveJsonAsync("Save correction model", Path.GetFileName(LoadedModelPath));
        if (string.IsNullOrWhiteSpace(dest))
        {
            return;
        }

        File.Copy(LoadedModelPath, dest, overwrite: true);
        ModelIoStatus = $"Saved {Path.GetFileName(dest)}";
        Log("Model", $"Saved {Path.GetFileName(dest)}.", LogLevel.Success, [dest]);
    }

    [RelayCommand]
    private async Task SaveItemAsync(SessionItemViewModel? item)
    {
        if (item is null || Dialogs is null)
        {
            return;
        }

        if (!File.Exists(item.CorrectedPath) && !await MaterializeCorrectedAsync(item))
        {
            Status = "Could not write a full-resolution corrected still.";
            ToastService.ShowWarning(Status);
            return;
        }

        var dest = await Dialogs.SaveImageAsync(Path.GetFileNameWithoutExtension(item.Path) + "_corrected" + Path.GetExtension(item.CorrectedPath));
        if (string.IsNullOrWhiteSpace(dest))
        {
            return;
        }

        File.Copy(item.CorrectedPath, dest, overwrite: true);
        Status = $"Saved {Path.GetFileName(dest)}";
        Log("Save", dest);
    }

    [RelayCommand]
    private async Task ExecuteSaveAsync()
    {
        var items = Session.Where(item => item.IncludeInSave && item.HasSaveable).ToList();
        if (items.Count == 0)
        {
            Status = "Nothing selected to save.";
            ToastService.ShowWarning(Status);
            return;
        }

        if (!SaveCorrected && !(SaveFfc && CanSaveFfc) && !(SaveGc && CanSaveGc)
            && !(SaveWb && CanSaveWb) && !(SaveCc && CanSaveCc) && !SaveReport)
        {
            Status = "Choose at least one output to save.";
            ToastService.ShowWarning(Status);
            return;
        }

        // Keep the Avalonia synchronization context while validating the selected
        // destination and, below, confirming replacement of existing files.
        if (!await EnsureOutputAllowedAsync())
        {
            return;
        }

        var destDir = string.IsNullOrWhiteSpace(OutputDir)
            ? Path.Combine(RunPaths.RunsRoot(_worker.RepoRoot), "saved")
            : Path.IsPathRooted(OutputDir) ? OutputDir : Path.GetFullPath(Path.Combine(_worker.RepoRoot, OutputDir));
        var format = SelectedOutputFormat?.Value ?? "jpg";
        var ext = format.Equals("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
        IsSaving = true;
        ShowSaveProgress = true;
        SaveProgress = 0;
        SaveProgressLabel = $"Preparing {items.Count} image(s)…";
        CurrentStage = "Save";
        var jobs = new List<(string Src, string Dest)>();
        var failures = new ConcurrentQueue<string>();
        var artifactNames = CorrectionClient.BuildArtifactBases(items.Select(item => item.Path).ToArray());
        try
        {
            Directory.CreateDirectory(destDir);
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                SaveProgress = 40.0 * i / Math.Max(1, items.Count);
                SaveProgressLabel = $"Preparing {item.FileName}…";
                var stem = artifactNames[i];
                if (SaveCorrected)
                {
                    if (!File.Exists(item.CorrectedPath))
                    {
                        await MaterializeCorrectedAsync(item);
                    }

                    if (File.Exists(item.CorrectedPath))
                    {
                        jobs.Add((item.CorrectedPath, Path.Combine(destDir, stem + "_corrected" + ext)));
                    }
                    else
                    {
                        failures.Enqueue($"{item.FileName}: corrected image is unavailable; check the saved model and reference assets.");
                    }
                }

                if (SaveFfc && ResultStageEnabled(item, "ffc"))
                {
                    await EnsureItemStageAsync(item, "ffc");
                    if (!QueueStageCopy(jobs, item, destDir, stem, "ffc", ext)) failures.Enqueue($"{item.FileName}: requested FFC stage is unavailable.");
                }

                if (SaveGc && ResultStageEnabled(item, "gc"))
                {
                    await EnsureItemStageAsync(item, "gc");
                    if (!QueueStageCopy(jobs, item, destDir, stem, "gc", ext)) failures.Enqueue($"{item.FileName}: requested GC stage is unavailable.");
                }

                if (SaveWb && ResultStageEnabled(item, "wb"))
                {
                    await EnsureItemStageAsync(item, "wb");
                    if (!QueueStageCopy(jobs, item, destDir, stem, "wb", ext)) failures.Enqueue($"{item.FileName}: requested WB stage is unavailable.");
                }

                if (SaveCc && ResultStageEnabled(item, "cc"))
                {
                    await EnsureItemStageAsync(item, "cc");
                    if (!QueueStageCopy(jobs, item, destDir, stem, "cc", ext))
                    {
                        if (!QueueStageCopy(jobs, item, destDir, stem, "cc", ext, sourceStage: "corrected")) failures.Enqueue($"{item.FileName}: requested CC stage is unavailable.");
                    }
                }

                if (SaveReport && File.Exists(item.ReportPath))
                {
                    jobs.Add((item.ReportPath, Path.Combine(destDir, stem + "_report.json")));
                }
            }

            var collisions = jobs.Where(job => File.Exists(job.Dest) && !PathsEqual(job.Src, job.Dest))
                .Select(job => job.Dest).Distinct(PathIdentity.Comparer).ToArray();
            if (collisions.Length > 0 && (Dialogs is null || !await Dialogs.ConfirmAsync(
                "Replace existing files?",
                $"{collisions.Length} file(s) already exist in the selected folder:\n\n{string.Join("\n", collisions.Take(8).Select(Path.GetFileName))}"
                + (collisions.Length > 8 ? "\n…" : "") + "\n\nReplace them with the selected exports?", "Replace files", danger: true)))
            {
                Status = "Save cancelled. Existing files were kept.";
                return;
            }

            var copied = 0;
            var degree = Math.Clamp(Environment.ProcessorCount, 2, 6);
            await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = degree }, async (job, _) =>
            {
                try
                {
                    if (PathsEqual(job.Src, job.Dest))
                    {
                        // The requested artifact is already at its destination.
                    }
                    else if (string.Equals(Path.GetExtension(job.Dest), ".json", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(job.Src, job.Dest, overwrite: true);
                    }
                    else
                    {
                        CopyOrConvert(job.Src, job.Dest, format);
                    }

                    var n = Interlocked.Increment(ref copied);
                    await OnUi(() =>
                    {
                        SaveProgress = 40 + 60.0 * n / Math.Max(1, jobs.Count);
                        SaveProgressLabel = $"Saving {n} / {jobs.Count}";
                    }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failures.Enqueue($"{Path.GetFileName(job.Dest)}: {ex.Message}");
                }
            });

            Status = failures.IsEmpty
                ? copied == 0
                    ? "No matching stage files to copy."
                    : $"Saved {copied} file(s) as {format.ToUpperInvariant()} to {destDir}"
                : copied == 0
                    ? $"Save failed: {failures.Count} file(s) could not be written."
                    : $"Saved {copied} file(s); {failures.Count} file(s) failed. Check the save log.";
            Log("Save", Status, failures.IsEmpty && copied > 0 ? LogLevel.Success : LogLevel.Warning,
                failures.IsEmpty ? null : failures.ToArray());
            if (copied > 0 && failures.IsEmpty)
            {
                ToastService.ShowSuccess(Status);
            }
            else
            {
                ToastService.ShowWarning(Status);
            }
        }
        catch (Exception ex)
        {
            Status = $"Save failed: {ex.Message}";
            Log("Save", Status, LogLevel.Error);
            ToastService.ShowError(Status);
        }
        finally
        {
            IsSaving = false;
            ShowSaveProgress = false;
            SaveProgress = 0;
            SaveProgressLabel = "";
            CurrentStage = "";
        }
    }

    private static bool QueueStageCopy(
        List<(string Src, string Dest)> jobs,
        SessionItemViewModel item,
        string destDir,
        string stem,
        string stage,
        string ext,
        string? sourceStage = null)
    {
        var src = ResolvePreviewFile(item.PreviewDir, sourceStage ?? stage, forDisplay: false);
        if (string.IsNullOrWhiteSpace(src))
        {
            return false;
        }

        // All images honor the selected encoding. CC's corrected-source fallback
        // keeps its own filename so parallel copies never share a destination.
        jobs.Add((src, Path.Combine(destDir, $"{stem}_{stage}{ext}")));
        return true;
    }

    private static int CopyStagePreview(SessionItemViewModel item, string destDir, string stem, string stage)
    {
        var src = ResolvePreviewFile(item.PreviewDir, stage, forDisplay: false);
        if (string.IsNullOrWhiteSpace(src))
        {
            return 0;
        }

        var destExt = Path.GetExtension(src);
        File.Copy(src, Path.Combine(destDir, $"{stem}_{stage}{destExt}"), overwrite: true);
        return 1;
    }

    private static string ResolveCorrectedViewPath(SessionItemViewModel item)
    {
        var preview = ResolvePreviewFile(item.PreviewDir, "corrected");
        if (!string.IsNullOrWhiteSpace(preview))
        {
            return preview;
        }

        return File.Exists(item.CorrectedPath) ? item.CorrectedPath : "";
    }

    private async Task<bool> MaterializeCorrectedAsync(SessionItemViewModel item)
    {
        if (File.Exists(item.CorrectedPath))
        {
            return true;
        }

        var options = ReplayOptions(item);
        var model = item.CompletedContext?.Model.Path ?? "";
        if (options is null) return false;
        options.WriteOutput = true;
        options.PreviewMode = "none";
        options.OutputDir = "";
        // Updating this session item's outputs also notifies UI bindings.
        var outcome = await _client.ApplyModelAsync(item.Path, model, options);
        if (!outcome.Ok || !File.Exists(outcome.OutputPath))
        {
            return false;
        }

        item.CorrectedPath = outcome.OutputPath;
        item.NotifyOutputs();
        return true;
    }

    private async Task EnsureItemStageAsync(SessionItemViewModel item, string stage)
    {
        if (stage == "cc" && (ResolvePreviewFile(item.PreviewDir, "cc") is not null
            || ResolvePreviewFile(item.PreviewDir, "corrected") is not null))
        {
            return;
        }

        if (ResolvePreviewFile(item.PreviewDir, stage) is not null)
        {
            return;
        }

        var model = item.CompletedContext?.Model.Path ?? "";
        var previewDir = item.PreviewDir;
        if (!File.Exists(model) || string.IsNullOrWhiteSpace(previewDir))
        {
            return;
        }

        var options = ReplayOptions(item);
        if (options is not null) await _client.PreviewStageAsync(item.Path, model, previewDir, stage, options).ConfigureAwait(false);
    }

    [RelayCommand]
    private void ResetSettings()
    {
        SelectedMethod = Methods[0];
        SyncDegreeAndAdvanced();
        SelectedDegree = DegreeOptions.FirstOrDefault(x => x.Value == "2") ?? DegreeOptions.FirstOrDefault();
        SelectedInputSpace = Pick(RgbSpaces, "srgb");
        SelectedInputWhite = Pick(WhitePoints, "d65");
        SelectedTargetSpace = Pick(RgbSpaces, "srgb");
        SelectedTargetWhite = Pick(WhitePoints, "d65");
        SelectedOutputFormat = OutputFormats[1];
        FitSamples = 1;
        PlsComponents = 0;
        HiddenLayersText = "100";
        SelectedNnOptimizer = NnOptimizers[0];
        NnBatchNormalization = false;
        NnDropout = 0;
        NnLr = 0.001m;
        NnEpochs = 10000;
        NnPatience = 1500;
        NnTol = 0.000001m;
        SkipSaturated = false;
        IncludeCombinations = true;
        DoFfc = false;
        DoGc = true;
        DoWb = true;
        DoCc = true;
        SelectedFfcFitMethod = Pick(FfcFitMethods, "poly");
        FfcBins = 50;
        FfcSmoothWindow = 5;
        FfcDegree = 3;
        FfcNnMaxIter = 10000;
        FfcTol = 0.00000001m;
        FfcCutOff = 1.3m;
        FfcMaxVal = 1.8m;
        ClipLo = -0.2m;
        ClipHi = 1.3m;
        Phase2Teacher = true;
        ClearNumericDrafts();
        UpdateCcSummary();
        Log("Settings", "Reset correction settings to defaults.", LogLevel.Info, CorrectionSettingsDetails());
        ScheduleSave();
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        _previewGeneration++;
        Exception? failure = null;
        try
        {
            UnhookWorkerProgress();
            _saveCts?.Cancel();
            _jobCts?.Cancel();
            _cropCts?.Cancel();
            FlushPersist();
        }
        catch (Exception exception) { failure = exception; }

        // Stop/cancel every worker request, including deferred previews without
        // the current job token, before disposing data an operation may still use.
        try { await _worker.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { failure ??= exception; }
        await _run.WaitAsync().ConfigureAwait(false);
        try { await OnUi(DisposeUiResources).ConfigureAwait(false); }
        catch (Exception exception) { failure ??= exception; }
        finally { _run.Dispose(); }
        try { ClearTransientCache(); }
        catch (Exception exception) { failure ??= exception; }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void DisposeUiResources()
    {
        var original = OriginalPreview;
        var corrected = CorrectedPreview;
        var displayed = Preview;
        Preview = null;
        OriginalPreview = null;
        CorrectedPreview = null;
        ClearStageBitmaps();
        foreach (var item in Session)
        {
            item.Dispose();
        }

        foreach (var item in _catalog)
        {
            item.Dispose();
        }

        Bitmap[] cachedOriginals;
        lock (_originalCacheLock)
        {
            cachedOriginals = _originalCache.Values.Select(cached => cached.Bitmap).ToArray();
            _originalCache.Clear();
        }
        foreach (var bitmap in cachedOriginals.Concat(new[] { original, corrected, displayed }).Distinct()) DisposeIfUnused(bitmap);
    }

    private void Activate(SessionItemViewModel item)
    {
        foreach (var sessionItem in Session)
        {
            sessionItem.IsActive = sessionItem == item;
        }

        var gen = ++_previewGeneration;
        LastPreviewDir = Directory.Exists(item.PreviewDir) ? item.PreviewDir : "";
        if (StageName != "Original")
        {
            StageName = "Original";
        }

        ShowCorrected = false;
        HasCorrected = false;
        ClearPicker();
        SetPreview(null, null);
        ApplyReport(null);

        Active = item;
        NotifyStageRadios();
        HasImage = true;
        RebuildFaces(item);
        if (ImageSizeReader.TryRead(item.Path, out var width, out var height))
        {
            SetSourceSize(width, height);
        }

        ApplyReport(item.Report);
        RefreshOverlay();
        HasReport = File.Exists(item.ReportPath);
        OpenReportCommand.NotifyCanExecuteChanged();
        NotifyRunCommands();
        _ = LoadPreviewsAsync(item, gen);

        MaybeAutoDetect();
        OnPropertyChanged(nameof(IsResultPreviewSuppressed));
        if (!item.PreviewSuppressed && !string.IsNullOrWhiteSpace(LastPreviewDir) && item.IsCorrected)
        {
            StageName = "Corrected";
            ApplyStagePreview();
        }
    }

    private void RebuildFaces(SessionItemViewModel item)
    {
        FaceOptions.Clear();
        var handshake = item.Handshake;
        if (handshake is null || handshake.ChartCount <= 1)
        {
            ShowFacePicker = false;
            UseAllFaces = false;
            FaceIndex = 0;
            OverlayFaceChoice = 0;
            return;
        }

        foreach (var label in handshake.FaceLabels())
        {
            var parts = label.Split(". ", 2);
            FaceOptions.Add(parts.Length == 2 ? $"{parts[0]}. {ColorSpaces.ChartLabel(parts[1])}" : label);
        }

        FaceOptions.Add("All charts");
        ShowFacePicker = true;
        FaceIndex = item.UseAllFaces
            ? FaceOptions.Count - 1
            : Math.Clamp(item.FaceChoice, 0, Math.Max(0, FaceOptions.Count - 2));
        OnPropertyChanged(nameof(SelectedFace));
        SyncFaceChoice();
    }

    private void SyncFaceChoice()
    {
        UseAllFaces = ShowFacePicker && FaceOptions.Count > 0 && FaceIndex == FaceOptions.Count - 1 && FaceOptions[^1] == "All charts";
        OverlayFaceChoice = UseAllFaces ? 0 : Math.Max(0, FaceIndex);
        if (Active is not null)
        {
            Active.UseAllFaces = UseAllFaces;
            if (!UseAllFaces)
            {
                Active.FaceChoice = OverlayFaceChoice;
            }
        }
    }

    private void SetPreview(Bitmap? original, Bitmap? corrected)
    {
        var oldOriginal = OriginalPreview;
        var oldCorrected = CorrectedPreview;
        var oldPreview = Preview;
        OriginalPreview = original;
        CorrectedPreview = corrected;
        Preview = null;
        UpdateDisplayedPreview();
        foreach (var bitmap in new[] { oldOriginal, oldCorrected, oldPreview }.Distinct()) DisposeIfUnused(bitmap);
    }

    private void DisposeIfUnused(Bitmap? candidate)
    {
        if (candidate is null)
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => DisposeIfUnused(candidate));
            return;
        }
        // Display properties and both caches share bitmap instances. Releasing a
        // display slot does not release a stage still owned by the same result.
        if (ReferenceEquals(candidate, Preview) || ReferenceEquals(candidate, OriginalPreview)
            || ReferenceEquals(candidate, CorrectedPreview) || _stageBitmaps.ContainsValue(candidate))
        {
            return;
        }

        lock (_originalCacheLock)
        {
            if (_originalCache.Values.Any(item => ReferenceEquals(item.Bitmap, candidate))
                || _originalLeases.ContainsKey(candidate))
            {
                return;
            }
        }

        candidate.Dispose();
    }

    private static bool TryBitmapSize(Bitmap bitmap, out int width, out int height)
    {
        try
        {
            var size = bitmap.PixelSize;
            width = size.Width;
            height = size.Height;
            return width > 0 && height > 0;
        }
        catch (ObjectDisposedException)
        {
            width = 0;
            height = 0;
            return false;
        }
    }

    private void UpdateDisplayedPreview()
    {
        if (!string.IsNullOrWhiteSpace(LastPreviewDir))
        {
            ApplyStagePreview();
        }

        if (Preview is null || !TryBitmapSize(Preview, out _, out _))
        {
            Preview = ShowCorrected && CorrectedPreview is not null ? CorrectedPreview : OriginalPreview;
        }
        if (_sourceWidth > 1 && _sourceHeight > 1)
        {
            ImageMeta = $"{_sourceWidth} × {_sourceHeight}";
        }
        else if (Preview is not null && TryBitmapSize(Preview, out var width, out var height))
        {
            SetSourceSize(width, height);
        }
        else
        {
            ImageMeta = "";
        }

        SyncDisplaySize();

        RefreshOverlay();
    }

    private bool ActiveIsWhiteRef()
    {
        if (Active is null || string.IsNullOrWhiteSpace(WhiteImagePath) || !File.Exists(WhiteImagePath))
        {
            return false;
        }

        return PathsEqual(Active.Path, WhiteImagePath);
    }

    private void EnsureWhiteInSession(bool activate)
    {
        RefreshWhiteBadges();
        if (string.IsNullOrWhiteSpace(WhiteImagePath) || !File.Exists(WhiteImagePath))
        {
            RefreshOverlay();
            return;
        }

        var existing = Session.FirstOrDefault(item => PathsEqual(item.Path, WhiteImagePath));
        if (existing is not null)
        {
            // A flat-field capture is calibration input, never a correction target unless
            // the user explicitly opts it back into the batch.
            existing.IncludeInBatch = false;
            if (existing.FfcPlane is null)
            {
                existing.FfcPlane = _loadedModelPlane;
            }
        }

        if (activate && existing is not null)
        {
            Activate(existing);
        }

        RefreshOverlay();
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return PathIdentity.Equal(Path.GetFullPath(a), Path.GetFullPath(b));
        }
        catch
        {
            return PathIdentity.Equal(a, b);
        }
    }

    private void RefreshOverlay()
    {
        var original = StageName == "Original";
        var handshake = original ? Active?.Handshake : null;
        OverlayHandshake = handshake is { Ok: true } ? handshake : null;
        // Plane is detected on the white card. Draw it only on that still.
        var plane = original && ActiveIsWhiteRef()
            ? _loadedModelPlane ?? Active?.FfcPlane ?? Session.Select(item => item.FfcPlane).FirstOrDefault(p => p is { IsValid: true })
            : null;
        OverlayPlane = plane is { IsValid: true } ? plane : null;
        OverlayBadge = OverlayHandshake is { Ok: true }
            ? ColorSpaces.ChartLabel(OverlayHandshake.ChartType)
            : OverlayPlane?.Badge ?? "";
        if (OverlayPlane is { } p)
        {
            var (x, y, w, h) = p.MapToSource(Math.Max(1, _sourceWidth), Math.Max(1, _sourceHeight));
            OverlayPlaneRect = new Rect(x, y, w, h);
        }
        else
        {
            OverlayPlaneRect = default;
        }

        OnPropertyChanged(nameof(ShowChartOverlay));
        OnPropertyChanged(nameof(ShowOverlayLegend));
        OnPropertyChanged(nameof(ShowPlaneOverlay));
        OnPropertyChanged(nameof(ShowOverlayChrome));
        OnPropertyChanged(nameof(ShowPlaneToggle));
        OnPropertyChanged(nameof(PlaneBadge));
    }

    private void ApplyReport(CorrectionReport? report)
    {
        Report = report;
        HasMetrics = report?.HasMetrics == true;
        Patches.Clear();
        StageDeltaERows.Clear();

        if (!HasMetrics || report is null)
        {
            MeanAfter = MedianAfter = MaxAfter = "--";
            MeanBefore = MedianBefore = MaxBefore = "--";
            MeanQuality = MedianQuality = MaxQuality = "";
            MeanAfterValue = MedianAfterValue = MaxAfterValue = double.NaN;
            ModelMetric = "--";
            FeatureDetail = HasCorrected && report is null
                ? "Model apply · no chart ΔE"
                : string.IsNullOrWhiteSpace(report?.Process) ? "--" : report.Process;
            PatchSummary = "";
            ValidationSummary = "";
            StatusDeltaE = "";
            return;
        }

        MeanAfter = report.MeanAfter.ToString("0.00");
        MedianAfter = report.MedianAfter.ToString("0.00");
        MaxAfter = report.MaxAfter.ToString("0.00");
        MeanAfterValue = report.MeanAfter;
        MedianAfterValue = report.MedianAfter;
        MaxAfterValue = report.MaxAfter;
        MeanBefore = $"was {report.MeanBefore:0.00}";
        MedianBefore = $"was {report.MedianBefore:0.00}";
        MaxBefore = $"was {report.MaxBefore:0.00}";
        MeanQuality = report.QualityLabel(report.MeanAfter);
        MedianQuality = report.QualityLabel(report.MedianAfter);
        MaxQuality = report.QualityLabel(report.MaxAfter);
        ModelMetric = string.IsNullOrWhiteSpace(report.Model) ? BuildOptions().CliModel() : report.Model;
        FeatureDetail = string.IsNullOrWhiteSpace(report.Process)
            ? (report.FeatureCount > 0 ? $"{report.FeatureCount} features" : "")
            : report.FeatureCount > 0
                ? $"{report.Process} · {report.FeatureCount} feat"
                : report.Process;
        ValidationSummary = double.IsFinite(report.ValidationMean)
            ? $"CC-only validation · {report.ValidationFolds}-fold mean {report.ValidationMean:0.00} · median {report.ValidationMedian:0.00} · max {report.ValidationMax:0.00}"
            : "Training fit shown · validation unavailable";
        PatchSummary = $"{report.Patches.Count} patches";
        StatusDeltaE = report.StatusLine();
        foreach (var row in report.Patches)
        {
            Patches.Add(new PatchRowViewModel(row));
        }

        foreach (var stage in report.EffectiveStages)
        {
            StageDeltaERows.Add(new StageDeltaERow(stage, report.QualityLabel(stage.MeanAfter)));
        }
    }

    public void PrepareSaveMenu() => RefreshSessionUi();

    private void RefreshSessionUi()
    {
        HasSession = Session.Count > 0;
        SessionCountLabel = Session.Count.ToString();
        HasSelected = Session.Any(item => item.IsSelected);
        HasSaveItems = Session.Any(item => item.HasSaveable);
        CanSaveCorrected = Session.Any(item => item.IncludeInSave && (item.IsCorrected || File.Exists(item.CorrectedPath)));
        CanSaveFfc = Session.Any(item => item.IncludeInSave && item.IsCorrected && ResultStageEnabled(item, "ffc"));
        CanSaveGc = Session.Any(item => item.IncludeInSave && item.IsCorrected && ResultStageEnabled(item, "gc"));
        CanSaveWb = Session.Any(item => item.IncludeInSave && item.IsCorrected && ResultStageEnabled(item, "wb"));
        CanSaveCc = Session.Any(item => item.IncludeInSave && item.IsCorrected && ResultStageEnabled(item, "cc"));
        CanSaveReport = Session.Any(item => item.IncludeInSave && File.Exists(item.ReportPath));
        HasWhiteRef = !string.IsNullOrWhiteSpace(WhiteImagePath) && File.Exists(WhiteImagePath);
        RefreshWhiteBadges();
        HasReport = Active is not null && File.Exists(Active.ReportPath);
        RefreshSessionNavigation();
        ClearSessionCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        OpenReportCommand.NotifyCanExecuteChanged();
        NotifyRunCommands();
    }

    private void RefreshWhiteBadges()
    {
        foreach (var item in Session)
        {
            item.IsWhiteRef = !string.IsNullOrWhiteSpace(WhiteImagePath)
                && PathsEqual(item.Path, WhiteImagePath);
        }
    }

    private void NotifyRunCommands()
    {
        SaveSessionAsCommand.NotifyCanExecuteChanged();
        LoadSessionCommand.NotifyCanExecuteChanged();
        DetectCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        BatchFitCommand.NotifyCanExecuteChanged();
        BatchApplyCommand.NotifyCanExecuteChanged();
        CancelBatchCommand.NotifyCanExecuteChanged();
        ForceCpuRestartCommand.NotifyCanExecuteChanged();
        CheckSetupCommand.NotifyCanExecuteChanged();
        InstallSupportCommand.NotifyCanExecuteChanged();
        UseCpuCommand.NotifyCanExecuteChanged();
        SaveModelCommand.NotifyCanExecuteChanged();
        ExportCubeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ApplyLabel));
        OnPropertyChanged(nameof(WorkflowHint));
        OnPropertyChanged(nameof(ModelModeHint));
        OnPropertyChanged(nameof(HasLoadedModel));
        OnPropertyChanged(nameof(WillUseLoadedModel));
        OnPropertyChanged(nameof(IsUseLoadedMode));
        OnPropertyChanged(nameof(IsFitFromImage));
        OnPropertyChanged(nameof(ShowStillPreview));
        OnPropertyChanged(nameof(ShowChartSection));
        OnPropertyChanged(nameof(CanOpenBatch));
        OnPropertyChanged(nameof(RunActionHint));
        OnPropertyChanged(nameof(DetectActionLabel));
        OnPropertyChanged(nameof(BatchFitHint));
        OnPropertyChanged(nameof(BatchApplyHint));
        OnPropertyChanged(nameof(HasWorkerError));
        OnPropertyChanged(nameof(StatusStateLabel));
        RefreshDetectSummary();
        RefreshValidation();
    }

    private void RefreshSessionNavigation()
    {
        OnPropertyChanged(nameof(ShowSessionNavigation));
        OnPropertyChanged(nameof(CanNavigatePrevious));
        OnPropertyChanged(nameof(CanNavigateNext));
        OnPropertyChanged(nameof(ActivePositionLabel));
        OnPropertyChanged(nameof(PreviousSessionHint));
        OnPropertyChanged(nameof(NextSessionHint));
        PreviousSessionCommand.NotifyCanExecuteChanged();
        NextSessionCommand.NotifyCanExecuteChanged();
    }

    private bool CanClear() => Session.Count > 0;
    private bool CanRemove() => HasSelected;
    private bool CanOpenReport() => HasReport;
    private bool CanDetect() =>
        WorkerReady && !Busy && !IsSaving && Active is not null && File.Exists(Active.Path);
    private bool CanApply() =>
        WorkerReady && !Busy && !IsSaving && Active is not null && File.Exists(Active.Path) && OperationError(!WillUseLoadedModel) is null &&
        (WillUseLoadedModel
            || Active.Handshake is { Ok: true });
    private bool CanBatchFit() => WorkerReady && !Busy && !IsSaving && OperationError(true) is null && Session.Any(item => item.IncludeInBatch);
    private bool CanBatchApply() =>
        WorkerReady && !Busy && !IsSaving && OperationError(false) is null && Session.Any(item => item.IncludeInBatch) && File.Exists(LoadedModelPath);
    private bool CanRestartWorker() => !Busy && !IsSaving;
    private bool CanCancel() => Busy;
    private bool CanSaveModel() => !Busy && File.Exists(LoadedModelPath);
    private bool CanExportCube() => !Busy && File.Exists(LoadedModelPath);

    private CorrectionOptions BuildOptions()
    {
        var hiddenLayers = CorrectionOptions.TryParseHiddenLayers(HiddenLayersText, out var parsedLayers, out _)
            ? parsedLayers
            : [100];
        return new CorrectionOptions
        {
            CpuLut = ForceCpu,
            LutDevice = SelectedProcessingDevice.Value,
            FastMode = FastMode,
            BatchPreviewResults = BatchPreviewResults,
            Method = SelectedMethod.Value,
            Degree = int.TryParse(SelectedDegree?.Value, out var degree) ? degree : 2,
            IncludeCombinations = IncludeCombinations,
            FitSamples = (int)Math.Clamp(FitSamples, 1, 200),
            SkipSaturated = SkipSaturated,
            InputSpace = SelectedInputSpace.Value,
            InputWhite = SelectedInputWhite.Value,
            TargetSpace = SelectedTargetSpace.Value,
            TargetWhite = SelectedTargetWhite.Value,
            OutputFormat = SelectedOutputFormat.Value,
            OutputDir = OutputDir,
            PlsComponents = (int)Math.Clamp(PlsComponents, 0, 64),
            HiddenSize = hiddenLayers[0],
            HiddenLayers = hiddenLayers,
            NnOptimizer = SelectedNnOptimizer.Value,
            NnBatchNormalization = NnBatchNormalization,
            NnDropout = (double)Math.Clamp(NnDropout, 0, 0.8m),
            NnLr = (double)NnLr,
            NnEpochs = (int)Math.Clamp(NnEpochs, 50, 20000),
            NnPatience = (int)Math.Clamp(NnPatience, 1, 5000),
            NnTol = ClampTol((double)NnTol, 1e-6),
            LoadedModelPath = LoadedModelPath,
            ModelInPath = LoadedModelPath,
            UseLoadedModel = UseLoadedModel,
            DoFfc = DoFfc,
            DoGc = DoGc,
            DoWb = DoWb,
            DoCc = DoCc,
            WhiteImagePath = WhiteImagePath,
            CropRect = CropRect,
            FfcFitMethod = SelectedFfcFitMethod?.Value ?? "poly",
            FfcBins = (int)Math.Clamp(FfcBins, 4, 200),
            FfcSmoothWindow = (int)Math.Clamp(FfcSmoothWindow, 1, 51),
            FfcDegree = (int)Math.Clamp(FfcDegree, 1, 7),
            FfcNnMaxIter = (int)Math.Clamp(FfcNnMaxIter, 50, 20000),
            FfcTol = ClampTol((double)FfcTol, 1e-8),
            FfcCutOff = ClampFfcCutOff((double)FfcCutOff),
            FfcMaxVal = ClampFfcMaxVal((double)FfcMaxVal, ClampFfcCutOff((double)FfcCutOff)),
            ClipLo = ClampClipLo((double)ClipLo),
            ClipHi = ClampClipHi((double)ClipHi, ClampClipLo((double)ClipLo)),
            Phase2Teacher = Phase2Teacher,
        }.Normalize();
    }

    private void SyncDegreeAndAdvanced()
    {
        var method = CorrectionOptions.CanonicalMethod(SelectedMethod.Value);
        ShowDegree = method != "ccm";
        ShowPolyCombinations = method == "linear";
        ShowPls = method == "pls";
        ShowNn = method == "nn";
        ShowCcIterParams = method is "pls" or "nn";
        DegreeLabel = method == "rpcc" ? "Root degree" : "Polynomial degree";
        var keep = SelectedDegree?.Value ?? "2";
        DegreeOptions.Clear();
        if (method == "rpcc")
        {
            DegreeOptions.Add(new NamedValue("2", "2"));
            DegreeOptions.Add(new NamedValue("3", "3"));
            keep = keep == "3" ? "3" : "2";
        }
        else if (method != "ccm")
        {
            for (var i = 1; i <= 7; i++)
            {
                DegreeOptions.Add(new NamedValue(i.ToString(), i.ToString()));
            }
        }

        SelectedDegree = DegreeOptions.FirstOrDefault(item => item.Value == keep) ?? DegreeOptions.FirstOrDefault();
        OnPropertyChanged(nameof(NnSettingsValid));
        OnPropertyChanged(nameof(HasHiddenLayersError));
        OnPropertyChanged(nameof(HiddenLayersError));
        UpdateCcSummary();
        RefreshValidation();
    }

    private void UpdateCcSummary()
    {
        var options = BuildOptions();
        CcSummary = options.Summary();
        ProcessSummary = BuildProcessSummary(options);
        RefreshIdlePipeline();
    }

    private static string BuildProcessSummary(CorrectionOptions options)
    {
        var parts = new List<string>
        {
            options.DoFfc
                ? options.FfcFitMethod == "nn"
                    ? $"FFC {ColorSpaces.FfcMethodLabel(options.FfcFitMethod)} d{options.FfcDegree} · {options.FfcNnMaxIter} iter"
                    : $"FFC {ColorSpaces.FfcMethodLabel(options.FfcFitMethod)} d{options.FfcDegree}"
                : "FFC off",
            options.DoGc ? "GC safe" : "GC off",
            options.DoWb ? "WB" : "WB off",
            options.DoCc ? $"CC {options.Summary()}" : "CC off",
        };
        return string.Join(" · ", parts);
    }

    private void RefreshDetectSummary()
    {
        if (Active is null)
        {
            DetectSummary = "";
            return;
        }



        if (Active.Handshake is { Ok: true })
        {
            DetectSummary = $"{Active.DetectLabel} · {Math.Max(1, Active.Handshake.ChartCount)} chart(s)";
            return;
        }

        DetectSummary = WillUseLoadedModel
            ? $"{Active.Name} — Run applies the loaded model (no chart needed)"
            : string.IsNullOrWhiteSpace(Active.DetectLabel)
                ? $"{Active.Name} — Detect the chart, then Run to fit"
                : $"{Active.Name} — {Active.DetectLabel}";
    }

    private void MaybeAutoDetect()
    {
        if (_disposed || _restoring || !CanDetect() || Active is not { Handshake: null } item)
        {
            return;
        }

        _ = AutoDetectIfNeededAsync(item);
    }

    private string ResolvedOutputDir()
    {
        if (string.IsNullOrWhiteSpace(OutputDir))
        {
            return RunPaths.RunsRoot(_worker.RepoRoot);
        }

        return Path.IsPathRooted(OutputDir)
            ? Path.GetFullPath(OutputDir)
            : Path.GetFullPath(Path.Combine(_worker.RepoRoot, OutputDir));
    }

    private static void OpenOsPath(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void Log(string action, string detail, LogLevel level = LogLevel.Info, IEnumerable<string>? details = null)
    {
        if (level == LogLevel.Error)
        {
            try
            {
                var folder = Path.Combine(SessionStore.AppDataDir(), "diagnostics");
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, "engine.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:O} {action}: {detail}\n{_worker.FormatRecentStderr()}\n");
            }
            catch { /* Logging must never block recovery. */ }
            detail = ConciseProcessingError(detail);
        }
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        var message = string.IsNullOrWhiteSpace(detail) ? action : $"{action}  {detail}";
        LogEntries.Add(new LogEntry
        {
            Timestamp = stamp,
            Message = message,
            Level = level,
        });
        if (details is not null)
        {
            foreach (var line in details)
            {
                var text = line?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                LogEntries.Add(new LogEntry
                {
                    Timestamp = stamp,
                    Message = text,
                    Level = level,
                    IsDetail = true,
                });
            }
        }

        while (LogEntries.Count > 800)
        {
            LogEntries.RemoveAt(0);
        }

        ErrorMessage = level == LogLevel.Error ? detail : "";
        if (level == LogLevel.Error)
        {
            ToastService.ShowError(detail);
        }
    }

    private IReadOnlyList<string> CorrectionSettingsDetails(HandshakeDocument? handshake = null, bool applyOnly = false)
    {
        var options = BuildOptions();
        return [BuildProcessSummary(options),
            $"{SelectedInputSpace.Label} / {SelectedInputWhite.Label} → {SelectedTargetSpace.Label} / {SelectedTargetWhite.Label}"];
    }

    private IReadOnlyList<string> DetectDetails(SessionItemViewModel item, DetectOutcome result, HandshakeDocument? handshake)
    {
        if (handshake is null) return [];
        return [$"{handshake.PatchCount} patches · {LabelStage(handshake.Device)}"];
    }

    private static IReadOnlyList<string> DeltaEDetails(CorrectionReport? report)
    {
        if (report is null || !report.HasMetrics) return [];
        var lines = new List<string> { $"Fit ΔE₀₀ {report.MeanBefore:0.00} → {report.MeanAfter:0.00}" };
        if (double.IsFinite(report.ValidationMean))
            lines.Add($"CC-only {report.ValidationFolds}-fold ΔE₀₀ {report.ValidationMean:0.00}");
        lines.AddRange(report.FitWarnings);
        if (report.SaturatedPatches > 0) lines.Add($"{report.SaturatedPatches} saturated patches");
        return lines;
    }

    private CancellationToken BeginJob()
    {
        _jobCts?.Dispose();
        _jobCts = new CancellationTokenSource();
        return _jobCts.Token;
    }

    private IReadOnlyList<string>? WorkerErrorDetails() => null;

    private string WithWorkerStderr(string message) => ConciseProcessingError(message);

    internal static string ConciseProcessingError(string message)
    {
        if (message.Contains("No hardware", StringComparison.OrdinalIgnoreCase)
            || message.Contains("No compatible hardware", StringComparison.OrdinalIgnoreCase))
            return "No compatible GPU is available. Choose Auto or CPU for detection, or check the graphics driver.";
        if (message.Contains("Couldn't load Vulkan", StringComparison.OrdinalIgnoreCase)
            || message.Contains("vulkan GPU-only inference unavailable", StringComparison.OrdinalIgnoreCase))
            return "Vulkan inference could not start. Check the Vulkan driver or choose another inference mode.";
        if (message.Contains("no detect execution provider", StringComparison.OrdinalIgnoreCase))
            return "Detection could not start. Choose Check setup to inspect the inference runtime and devices.";
        var first = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Processing failed.";
        return first.Length > 280 ? first[..280] + "…" : first;
    }

    private Task<bool> EnsureOutputAllowedAsync()
    {
        if (string.IsNullOrWhiteSpace(OutputDir))
        {
            return Task.FromResult(true);
        }

        string full;
        try
        {
            full = Path.IsPathRooted(OutputDir)
                ? Path.GetFullPath(OutputDir)
                : Path.GetFullPath(Path.Combine(_worker.RepoRoot, OutputDir));
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            return Task.FromResult(false);
        }

        if (RunPaths.ContainsParentSegment(OutputDir))
        {
            Status = "Output folder cannot contain '..'.";
            Log("Output", Status, LogLevel.Error);
            return Task.FromResult(false);
        }

        // Choosing or entering Save to is the user's destination decision. A
        // packaged app has no user-facing project boundary to approve again.
        OutputDir = full;
        return Task.FromResult(true);
    }

    private void ScheduleSave()
    {
        if (_disposed || !_persistenceEnabled || _restoring)
        {
            return;
        }

        _saveCts?.Cancel();
        _saveCts = new CancellationTokenSource();
        var token = _saveCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, token).ConfigureAwait(false);
                await OnUi(Persist).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void Persist()
    {
        if (!_persistenceEnabled || _restoring)
        {
            return;
        }

        SessionStore.SaveSettings(BuildSessionState(includeItems: false));
    }

    private SessionState BuildSessionState(bool includeItems) => new()
    {
        Items = includeItems
            ? Session.Select(item => new SessionItemDto
            {
                Path = item.Path,
                Name = item.Name,
                HandshakeJson = item.Handshake?.Json ?? "",
                ReferenceProfiles = item.ReferenceProfiles.Select(p => p.Snapshot()).ToList(),
                ReferenceAssignments = item.ReferenceAssignments.ToList(),
                ReferencesNeedAssignment = item.ReferencesNeedAssignment,
                CorrectedPath = item.CorrectedPath,
                ReportPath = item.ReportPath,
                ModelPath = item.ModelPath,
                CompletedContext = item.CompletedContext,
                SourceIdentity = item.SourceIdentity,
                FaceChoice = item.FaceChoice,
                UseAllFaces = item.UseAllFaces,
                IncludeInBatch = item.IncludeInBatch,
                PreviewSuppressed = item.PreviewSuppressed,
                PreviewDir = item.PreviewDir,
                FfcPlane = item.FfcPlane is { } plane
                    ? [plane.X, plane.Y, plane.Width, plane.Height]
                    : null,
                FfcWhiteSize = item.FfcPlane is { } sized && sized.WhiteWidth > 0
                    ? [sized.WhiteWidth, sized.WhiteHeight]
                    : null,
                FfcPlaneSource = item.FfcPlane?.Source ?? "",
            }).ToList()
            : [],
        ActivePath = includeItems ? Active?.Path ?? "" : "",
        Options = BuildOptions(),
        Crop = Crop,
        Theme = IsLightTheme ? "Light" : "Dark",
        TrustedOutputDirs = _trusted.ToList(),
        CatalogFolders = CatalogFolders.Select(folder => folder.Path).ToList(),
        ForceCpu = ForceCpu,
        AccelPolicy = SelectedAccelPolicy.Value,
        ShowLattice = ShowLattice,
        ShowSample = ShowSample,
        ShowDim = ShowDim,
        ShowPlane = ShowPlane,
    };

    private void RestoreSession()
    {
        _saveCts?.Cancel();
        _restoring = true;
        try
        {
            var (state, error) = SessionStore.TryLoad();
            if (!string.IsNullOrWhiteSpace(error))
            {
                Log("Session", "Saved session could not be restored.", LogLevel.Warning, [error]);
            }

            foreach (var dir in state.TrustedOutputDirs)
            {
                _trusted.Add(dir);
            }

            CatalogFolders.Clear();
            foreach (var folder in state.CatalogFolders.Distinct(PathIdentity.Comparer))
            {
                if (Directory.Exists(folder))
                {
                    CatalogFolders.Add(new CatalogFolderViewModel(folder));
                }
            }
            HasCatalogFolders = CatalogFolders.Count > 0;

            ApplyOptions(state.Options);
            Crop = state.Crop is > 0.04 and <= 1 ? state.Crop : 0.75;
            OverlayCrop = Crop;
            IsLightTheme = string.Equals(state.Theme, "Light", StringComparison.OrdinalIgnoreCase);
            var policy = !string.IsNullOrWhiteSpace(state.AccelPolicy)
                ? state.AccelPolicy
                : state.ForceCpu ? "cpu" : "auto";
            SelectedAccelPolicy = Pick(AccelPolicies, policy == "cpu" ? "auto" : policy);
            ForceCpu = state.ForceCpu || policy == "cpu";
            _worker.Policy = SelectedAccelPolicy.Value;
            ShowLattice = state.ShowLattice ?? true;
            ShowSample = state.ShowSample ?? true;
            ShowDim = state.ShowDim ?? true;
            ShowPlane = state.ShowPlane ?? true;

            LoadCatalog();
            RefreshSessionUi();
            EnsureWhiteInSession(activate: false);
            RefreshOverlay();
        }
        finally
        {
            _restoring = false;
            RefreshSessionUi();
            NotifyRunCommands();
        }
    }

    internal int RestoreWorkingItems(SessionState state)
    {
        var wasRestoring = _restoring;
        _restoring = true;
        try
        {
            if (state.HasOptions)
            {
                ApplyOptions(state.Options);
                Crop = state.Crop is > 0.04 and <= 1 ? state.Crop : 0.75;
                OverlayCrop = Crop;
            }
            RestoreWorkingItemsCore(state);
            var skipped = state.Items.Count - Session.Count;
            if (skipped > 0) Status = $"Skipped {skipped} unsupported session item(s). Video is not supported.";
            return skipped;
        }
        finally
        {
            _restoring = wasRestoring;
            UpdateCcSummary(); NotifyRunCommands(); ScheduleSave();
        }
    }

    private void RestoreWorkingItemsCore(SessionState state)
    {
        _previewGeneration++;
        foreach (var item in Session.ToList())
        {
            item.Dispose();
        }

        Session.Clear();
        Active = null;
        _rangeAnchor = null;
        LastPreviewDir = "";
        SetPreview(null, null);
        OverlayHandshake = null;
        HasImage = false;
        HasCorrected = false;
        ShowFacePicker = false;
        FaceOptions.Clear();
        ClearPicker();
        ApplyReport(null);
        foreach (var dto in state.Items)
        {
            if (dto.IsVideo || !IsImage(dto.Path))
            {
                continue;
            }

            var item = new SessionItemViewModel(dto.Path)
            {
                FaceChoice = dto.FaceChoice,
                UseAllFaces = dto.UseAllFaces,
                IncludeInBatch = dto.IncludeInBatch,
                CorrectedPath = dto.CorrectedPath,
                ReportPath = dto.ReportPath,
                ModelPath = dto.ModelPath,
                PreviewDir = dto.PreviewDir,
                CompletedContext = dto.CompletedContext,
                PreviewSuppressed = dto.PreviewSuppressed,
                SourceIdentity = dto.SourceIdentity ?? dto.CompletedContext?.Source,
                IsCorrected = File.Exists(dto.CorrectedPath)
                    || (Directory.Exists(dto.PreviewDir) && ResolvePreviewFile(dto.PreviewDir, "corrected") is not null),
                IncludeInSave = File.Exists(dto.CorrectedPath) || Directory.Exists(dto.PreviewDir),
                Report = CorrectionReport.TryLoad(dto.ReportPath),
                FfcPlane = PlaneFromDto(dto) ?? CorrectionReport.TryLoad(dto.ReportPath)?.FfcPlane
                           ?? FfcPlane.TryLoad(dto.ModelPath),
            };
            item.NotifyOutputs();
            if (!string.IsNullOrWhiteSpace(dto.HandshakeJson))
            {
                try
                {
                    item.ApplyHandshake(new HandshakeDocument(dto.HandshakeJson));
                }
                catch
                {
                    item.ApplyHandshake(null);
                }
            }

            item.ReferenceProfiles = dto.ReferenceProfiles.Select(p => p.Snapshot()).ToList();
            item.ReferenceAssignments = dto.ReferenceAssignments.Select(a => new ChartReferenceAssignment { ChartId = a.ChartId, ProfileId = a.ProfileId, ProfileRevision = a.ProfileRevision }).ToList();
            item.ReferencesNeedAssignment = dto.ReferencesNeedAssignment;
            ValidateRestoredReferences(item);
            Session.Add(item);
        }

        if (Session.Count > 0)
        {
            var active = Session.FirstOrDefault(item => PathIdentity.Equal(item.Path, state.ActivePath))
                         ?? Session[0];
            Activate(active);
            LibraryExpanded = false;
        }

        RefreshSessionUi();
        EnsureWhiteInSession(activate: false);
        NotifyRunCommands();

    }

    private static FfcPlane? PlaneFromDto(SessionItemDto dto)
    {
        if (dto.FfcPlane is not { Length: >= 4 } rect || rect[2] <= 0 || rect[3] <= 0)
        {
            return null;
        }

        return new FfcPlane
        {
            X = rect[0],
            Y = rect[1],
            Width = rect[2],
            Height = rect[3],
            WhiteWidth = dto.FfcWhiteSize is { Length: >= 2 } size ? size[0] : 0,
            WhiteHeight = dto.FfcWhiteSize is { Length: >= 2 } size2 ? size2[1] : 0,
            Source = dto.FfcPlaneSource ?? "",
        };
    }

    private void ApplyOptions(CorrectionOptions options)
    {
        _numericText.Clear();
        ForceCpu = options.CpuLut;
        SelectedProcessingDevice = Pick(ProcessingDevices, options.CpuLut ? "cpu" : options.LutDevice);
        FastMode = options.FastMode;
        BatchPreviewResults = options.BatchPreviewResults;
        options.Normalize();
        SelectedMethod = Pick(Methods, options.Method);
        SyncDegreeAndAdvanced();
        SelectedDegree = DegreeOptions.FirstOrDefault(item => item.Value == options.Degree.ToString()) ?? SelectedDegree;
        SelectedInputSpace = Pick(RgbSpaces, options.InputSpace);
        SelectedInputWhite = Pick(WhitePoints, options.InputWhite);
        SelectedTargetSpace = Pick(RgbSpaces, options.TargetSpace);
        SelectedTargetWhite = Pick(WhitePoints, options.TargetWhite);
        SelectedOutputFormat = Pick(OutputFormats, options.OutputFormat);
        FitSamples = Math.Clamp(options.FitSamples, 1, 200);
        PlsComponents = Math.Clamp(options.PlsComponents, 0, 64);
        HiddenLayersText = CorrectionOptions.FormatHiddenLayers(options.HiddenLayers);
        SelectedNnOptimizer = Pick(NnOptimizers, options.NnOptimizer);
        NnBatchNormalization = options.NnBatchNormalization;
        NnDropout = (decimal)Math.Clamp(options.NnDropout, 0, 0.8);
        NnLr = (decimal)options.NnLr;
        NnEpochs = Math.Clamp(options.NnEpochs, 50, 20000);
        NnPatience = Math.Clamp(options.NnPatience, 1, 5000);
        NnTol = (decimal)ClampTol(options.NnTol, 1e-6);
        SkipSaturated = options.SkipSaturated;
        IncludeCombinations = options.IncludeCombinations;
        OutputDir = options.OutputDir;
        DoFfc = options.DoFfc;
        DoGc = options.DoGc;
        DoWb = options.DoWb;
        DoCc = options.DoCc;
        WhiteImagePath = options.WhiteImagePath;
        CropRect = options.CropRect;
        SelectedFfcFitMethod = Pick(FfcFitMethods, string.IsNullOrWhiteSpace(options.FfcFitMethod) ? "poly" : options.FfcFitMethod);
        FfcBins = Math.Clamp(options.FfcBins, 4, 200);
        FfcSmoothWindow = Math.Clamp(options.FfcSmoothWindow, 1, 51);
        FfcDegree = Math.Clamp(options.FfcDegree, 1, 7);
        FfcNnMaxIter = Math.Clamp(options.FfcNnMaxIter <= 0 ? 10000 : options.FfcNnMaxIter, 50, 20000);
        FfcTol = (decimal)ClampTol(options.FfcTol, 1e-8);
        FfcCutOff = (decimal)ClampFfcCutOff(options.FfcCutOff);
        FfcMaxVal = (decimal)ClampFfcMaxVal(options.FfcMaxVal, ClampFfcCutOff(options.FfcCutOff));
        ClipLo = (decimal)ClampClipLo(options.ClipLo);
        ClipHi = (decimal)ClampClipHi(options.ClipHi, ClampClipLo(options.ClipLo));
        Phase2Teacher = options.Phase2Teacher;
        var model = File.Exists(options.LoadedModelPath) ? options.LoadedModelPath
            : File.Exists(options.ModelInPath) ? options.ModelInPath : "";
        LoadedModelPath = model;
        UseLoadedModel = options.UseLoadedModel == true && File.Exists(model);
        _loadedModelPlane = FfcPlane.TryLoad(model);
        UpdateCcSummary();
    }

    private void LoadCatalog()
    {
        foreach (var item in _catalog)
        {
            item.Dispose();
        }

        _catalog.Clear();
        if (!EnginePaths.TryFindRepoRoot(out var root))
        {
            return;
        }

        var extras = CatalogFolders.Select(folder => folder.Path);
        foreach (var item in SampleCatalog.Load(root, extras).Select(entry => new CatalogItemViewModel(entry)))
        {
            _catalog.Add(item);
        }

        SampleGroups.Clear();
        SampleGroups.Add("All");
        var groups = _catalog.Select(item => item.Group).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Equals("Whites", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(group => group, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            SampleGroups.Add(group);
        }

        if (SelectedGroup != "All" &&
            !SampleGroups.Contains(SelectedGroup, StringComparer.OrdinalIgnoreCase))
        {
            SelectedGroup = "All";
        }

        RefreshFilteredSamples();
    }

    private void RefreshFilteredSamples()
    {
        FilteredSamples.Clear();
        foreach (var item in _catalog)
        {
            if (SelectedGroup != "All" && !string.Equals(item.Group, SelectedGroup, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(SampleQuery) &&
                item.Name.IndexOf(SampleQuery, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            FilteredSamples.Add(item);
        }

        SampleCountLabel = FilteredSamples.Count.ToString();
    }

    private async Task StartWorkerAsync()
    {
        var cancellation = BeginJob();
        try
        {
            _worker.Policy = SelectedAccelPolicy.Value;
            await _worker.StartAsync(cancellation).ConfigureAwait(false);
            var warmed = _worker.Warmed;
            var warmupError = _worker.LastError;
            await OnUi(() =>
            {
                if (ReportWorkerWarmupFailure(warmed, warmupError)) return;
                WorkerReady = true;
                WorkerStartupComplete = true;
                WorkerStartupError = "";
                WorkerLine = string.IsNullOrWhiteSpace(_worker.Title)
                    ? (_worker.Warmed ? $"Warmed · {_worker.Device}" : $"Ready · {_worker.Device}")
                    : _worker.Title;
                Status = "Ready.";
                CurrentStage = "";
                Log("Engine", _worker.Warmed ? $"Ready · {LabelStage(_worker.Device)}" : "Ready. Detection is checked when needed.", LogLevel.Info);
                // Status bar already shows ready. A toast here covers the viewer.
            }).ConfigureAwait(false);
            await RefreshAccelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await OnUi(() =>
            {
                WorkerReady = false;
                WorkerStartupComplete = true;
                WorkerStartupError = ex.Message;
                WorkerLine = "Worker failed";
                Status = ex.Message;
                Log("Worker", ex.Message, LogLevel.Error);
            }).ConfigureAwait(false);
        }
        finally
        {
            await OnUi(() =>
            {
                Busy = false;
                EndPipeline();
                NotifyRunCommands();
                MaybeAutoDetect();
            }).ConfigureAwait(false);
        }
    }

    private const int PreviewMaxWidth = 2560;

    private void SetSourceSize(int width, int height)
    {
        _sourceWidth = Math.Max(1, width);
        _sourceHeight = Math.Max(1, height);
        ImageMeta = _sourceWidth > 1 ? $"{_sourceWidth} × {_sourceHeight}" : "";
        SyncDisplaySize();
    }

    private void SyncDisplaySize()
    {
        if (Preview is not null && TryBitmapSize(Preview, out var width, out var height))
        {
            NaturalWidth = width;
            NaturalHeight = height;
        }
        else if (_sourceWidth > 1)
        {
            var scale = Math.Min(1.0, PreviewMaxWidth / (double)_sourceWidth);
            NaturalWidth = Math.Max(1, _sourceWidth * scale);
            NaturalHeight = Math.Max(1, _sourceHeight * scale);
        }
        else
        {
            NaturalWidth = 1;
            NaturalHeight = 1;
        }

        OverlayScaleX = NaturalWidth / Math.Max(1, _sourceWidth);
        OverlayScaleY = NaturalHeight / Math.Max(1, _sourceHeight);
    }

    private static int MapSourceToPreview(int source, int sourceExtent, int previewExtent)
    {
        if (sourceExtent <= 1 || previewExtent <= 1 || sourceExtent == previewExtent)
        {
            return source;
        }

        return (int)Math.Clamp((long)source * previewExtent / sourceExtent, 0, previewExtent - 1);
    }

    private static int MapPreviewToSource(int preview, int previewExtent, int sourceExtent)
    {
        if (sourceExtent <= 1 || previewExtent <= 1 || sourceExtent == previewExtent)
        {
            return preview;
        }

        return (int)Math.Clamp((long)preview * sourceExtent / previewExtent, 0, sourceExtent - 1);
    }

    private static Bitmap? DecodePreview(string path)
    {
        try
        {
            if (path.EndsWith(".rgb", StringComparison.OrdinalIgnoreCase)
                || PreviewRgb.TryReadHeader(path, out _, out _))
            {
                return PreviewRgb.TryLoad(path);
            }

            return StillImageDecoder.Decode(path, PreviewMaxWidth);
        }
        catch
        {
            return null;
        }
    }

    private Bitmap? GetOrDecodeOriginal(string path)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        lock (_originalCacheLock)
        {
            if (_originalCache.TryGetValue(path, out var hit)
                && hit.Length == info.Length
                && hit.MtimeTicks == info.LastWriteTimeUtc.Ticks)
            {
                _originalLeases[hit.Bitmap] = _originalLeases.GetValueOrDefault(hit.Bitmap) + 1;
                return hit.Bitmap;
            }
        }

        var disk = OriginalCachePath(path, info);
        Bitmap? decoded = null;
        if (File.Exists(disk))
        {
            decoded = PreviewRgb.TryLoad(disk);
        }

        decoded ??= DecodePreview(path);
        if (decoded is null)
        {
            return null;
        }

        try
        {
            if (!File.Exists(disk))
            {
                PreviewRgb.WriteFromBitmap(disk, decoded);
                PruneOriginalDiskCache();
            }
        }
        catch
        {
            // Disk cache is optional; the in-memory bitmap is enough for this session.
        }

        RememberOriginal(path, info, decoded);
        return decoded;
    }

    private void ReleaseIfUncached(Bitmap? bitmap)
    {
        if (bitmap is null)
        {
            return;
        }

        lock (_originalCacheLock)
        {
            if (_originalLeases.TryGetValue(bitmap, out var leases))
                if (leases > 1) _originalLeases[bitmap] = leases - 1;
                else _originalLeases.Remove(bitmap);
        }
        DisposeIfUnused(bitmap);
    }

    private void RememberOriginal(string path, FileInfo info, Bitmap bitmap)
    {
        var retired = new List<Bitmap>();
        lock (_originalCacheLock)
        {
            _originalLeases[bitmap] = _originalLeases.GetValueOrDefault(bitmap) + 1;
            if (_disposed) return;
            if (_originalCache.TryGetValue(path, out var existing)
                && !ReferenceEquals(existing.Bitmap, bitmap))
            {
                retired.Add(existing.Bitmap);
            }

            while (_originalCache.Count >= OriginalCacheLimit)
            {
                var evict = _originalCache.FirstOrDefault(row =>
                    !PathIdentity.Equal(row.Key, path)
                    && !ReferenceEquals(row.Value.Bitmap, OriginalPreview));
                if (evict.Key is null)
                {
                    break;
                }

                if (!ReferenceEquals(evict.Value.Bitmap, bitmap))
                {
                    retired.Add(evict.Value.Bitmap);
                }

                _originalCache.Remove(evict.Key);
            }

            _originalCache[path] = new CachedOriginal
            {
                Bitmap = bitmap,
                Length = info.Length,
                MtimeTicks = info.LastWriteTimeUtc.Ticks,
            };
        }
        foreach (var retiredBitmap in retired.Distinct()) DisposeIfUnused(retiredBitmap);
    }

    internal static string OriginalCachePath(string path, FileInfo info)
    {
        // Old previews were neither EXIF-oriented nor ICC-normalized. Changing
        // decode semantics must invalidate them even when the source is unchanged.
        var key = $"oriented-srgb-v1|{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(SessionStore.AppDataDir(), "orig-preview", hash + ".rgb");
    }

    private static void PruneOriginalDiskCache()
    {
        var dir = Path.Combine(SessionStore.AppDataDir(), "orig-preview");
        if (!Directory.Exists(dir))
        {
            return;
        }

        var files = new DirectoryInfo(dir).GetFiles("*.rgb")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(32)
            .ToArray();
        foreach (var file in files)
        {
            try
            {
                file.Delete();
            }
            catch
            {
            }
        }
    }

    private async Task LoadPreviewsAsync(SessionItemViewModel item, int gen)
    {
        var path = item.Path;
        var correctedPath = item.PreviewSuppressed ? "" : ResolveCorrectedViewPath(item);
        Bitmap? original = null;
        Bitmap? corrected = null;
        try
        {
            original = await Task.Run(() => GetOrDecodeOriginal(path)).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(correctedPath) && File.Exists(correctedPath))
            {
                corrected = await Task.Run(() => DecodePreview(correctedPath)).ConfigureAwait(false);
            }
        }
        catch
        {
            ReleaseIfUncached(original);
            corrected?.Dispose();
            return;
        }

        if (gen != _previewGeneration)
        {
            ReleaseIfUncached(original);
            corrected?.Dispose();
            return;
        }

        await OnUi(() =>
        {
            if (gen != _previewGeneration)
            {
                ReleaseIfUncached(original);
                corrected?.Dispose();
                return;
            }

            if (_sourceWidth <= 1 && original is not null)
            {
                SetSourceSize(original.PixelSize.Width, original.PixelSize.Height);
            }

            if (item.PreviewSuppressed) { corrected?.Dispose(); corrected = null; }
            SetPreview(original, corrected);
            HasCorrected = CorrectedPreview is not null;
            ReleaseIfUncached(original);
        }).ConfigureAwait(false);
    }

    private async Task LoadCorrectedPreviewAsync(SessionItemViewModel item)
    {
        if (item.PreviewSuppressed) return;
        var gen = _previewGeneration;
        var path = ResolveCorrectedViewPath(item);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        Bitmap? corrected;
        try
        {
            corrected = await Task.Run(() => DecodePreview(path)).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (corrected is null || item.PreviewSuppressed || gen != _previewGeneration || !ReferenceEquals(Active, item))
        {
            corrected?.Dispose();
            return;
        }

        await OnUi(() =>
        {
            if (item.PreviewSuppressed || gen != _previewGeneration || !ReferenceEquals(Active, item))
            {
                corrected.Dispose();
                return;
            }

            SetPreview(OriginalPreview, corrected);
            HasCorrected = CorrectedPreview is not null;
            if (HasCorrected && StageName is "Original" or "Corrected")
            {
                ShowCorrected = true;
            }
        }).ConfigureAwait(false);
    }

    private static bool IsImage(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".tif", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase);
    }



    private static List<NamedValue> Map((string Id, string Label)[] items) =>
        items.Select(item => new NamedValue(item.Id, item.Label)).ToList();

    private static NamedValue Pick(IReadOnlyList<NamedValue> items, string value) =>
        items.FirstOrDefault(item =>
            string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Label, value, StringComparison.OrdinalIgnoreCase))
        ?? items[0];

    private static Task OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    partial void OnBusyChanged(bool value) => NotifyRunCommands();
    partial void OnIsSavingChanged(bool value) => NotifyRunCommands();
    partial void OnWorkerReadyChanged(bool value) => NotifyRunCommands();
    partial void OnWorkerStartupCompleteChanged(bool value) => NotifyRunCommands();
    partial void OnWorkerStartupErrorChanged(string value) => OnPropertyChanged(nameof(HasWorkerError));
    partial void OnIsBatchingChanged(bool value) => CancelBatchCommand.NotifyCanExecuteChanged();
    partial void OnLoadedModelPathChanged(string value)
    {
        try { _loadedModelIdentity = File.Exists(value) ? AssetIdentity.Capture(value) : null; }
        catch (IOException) { _loadedModelIdentity = null; }
        ModelLabel = File.Exists(value) ? $"Model · {Path.GetFileName(value)}" : "No model loaded";
        SavedModelSummary = DescribeSavedModel(value);
        HasSavedModelSummary = !string.IsNullOrWhiteSpace(SavedModelSummary);
        if (!File.Exists(value) && UseLoadedModel)
        {
            UseLoadedModel = false;
        }

        NotifyRunCommands();
    }

    partial void OnUseLoadedModelChanged(bool value)
    {
        if (value && !File.Exists(LoadedModelPath))
        {
            UseLoadedModel = false;
            return;
        }

        NotifyRunCommands();
        if (_restoring)
        {
            return;
        }

        ScheduleSave();
        if (!WillUseLoadedModel)
        {
            MaybeAutoDetect();
        }
    }

    private static string DescribeSavedModel(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return "";
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            static bool On(JsonElement el, string name) =>
                el.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;
            var kind = root.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String
                ? kindEl.GetString() ?? ""
                : "";
            if (root.TryGetProperty("correction", out var corr) && corr.ValueKind == JsonValueKind.Object
                && corr.TryGetProperty("kind", out var ck) && ck.ValueKind == JsonValueKind.String)
            {
                kind = ck.GetString() ?? kind;
            }

            var parts = new List<string>
            {
                On(root, "do_ffc") ? "FFC" : "FFC off",
                On(root, "do_gc") ? "GC" : "GC off",
                On(root, "do_wb") ? "WB" : "WB off",
                On(root, "do_cc") ? "CC" : "CC off",
            };
            if (!string.IsNullOrWhiteSpace(kind))
            {
                parts.Add(kind);
            }

            return "Saved model · " + string.Join(" · ", parts);
        }
        catch
        {
            return $"Saved model · {Path.GetFileName(path)}";
        }
    }

    partial void OnActiveChanged(SessionItemViewModel? value)
    {
        OnPropertyChanged(nameof(IsResultPreviewSuppressed));
        OnPropertyChanged(nameof(ReferenceSummary));
        OnPropertyChanged(nameof(ShowStillPreview));
        OnPropertyChanged(nameof(ShowChartSection));
        RefreshSessionNavigation();
        NotifyRunCommands();
    }

    partial void OnHasImageChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowStillPreview));
        OnPropertyChanged(nameof(ShowChartSection));
    }

    partial void OnSelectedMethodChanged(NamedValue value)
    {
        if (!_restoring)
        {
            SyncDegreeAndAdvanced();
            ScheduleSave();
        }
    }

    partial void OnSelectedDegreeChanged(NamedValue? value)
    {
        if (!_restoring)
        {
            UpdateCcSummary();
            ScheduleSave();
        }
    }

    partial void OnSelectedInputSpaceChanged(NamedValue value) { if (!_restoring) { UpdateCcSummary(); RefreshValidation(); ScheduleSave(); } }
    partial void OnSelectedInputWhiteChanged(NamedValue value) { if (!_restoring) { UpdateCcSummary(); RefreshValidation(); ScheduleSave(); } }
    partial void OnSelectedTargetSpaceChanged(NamedValue value) { if (!_restoring) { UpdateCcSummary(); ScheduleSave(); } }
    partial void OnSelectedTargetWhiteChanged(NamedValue value) { if (!_restoring) { UpdateCcSummary(); ScheduleSave(); } }
    partial void OnSelectedOutputFormatChanged(NamedValue value) { if (!_restoring) ScheduleSave(); }
    partial void OnSkipSaturatedChanged(bool value) { if (!_restoring) ScheduleSave(); }
    partial void OnIncludeCombinationsChanged(bool value)
    {
        if (!_restoring)
        {
            UpdateCcSummary();
            ScheduleSave();
        }
    }
    partial void OnOutputDirChanged(string value) { if (!_restoring) ScheduleSave(); }
    partial void OnFitSamplesChanged(decimal value) { if (!_restoring) ScheduleSave(); }
    partial void OnPlsComponentsChanged(decimal value) { if (!_restoring) ScheduleSave(); }
    partial void OnHiddenLayersTextChanged(string value)
    {
        OnPropertyChanged(nameof(NnSettingsValid));
        OnPropertyChanged(nameof(HasHiddenLayersError));
        OnPropertyChanged(nameof(HiddenLayersError));
        UpdateCcSummary();
        NotifyRunCommands();
        if (!_restoring && CorrectionOptions.TryParseHiddenLayers(value, out _, out _))
        {
            ScheduleSave();
        }
    }
    partial void OnSelectedNnOptimizerChanged(NamedValue value) { if (!_restoring) { UpdateCcSummary(); ScheduleSave(); } }
    partial void OnNnBatchNormalizationChanged(bool value) { if (!_restoring) { UpdateCcSummary(); ScheduleSave(); } }
    partial void OnNnDropoutChanged(decimal value) { if (!_restoring) { UpdateCcSummary(); ScheduleSave(); } }
    partial void OnNnLrChanged(decimal value) { if (!_restoring) ScheduleSave(); }
    partial void OnNnEpochsChanged(decimal value) { if (!_restoring) ScheduleSave(); }
    partial void OnNnPatienceChanged(decimal value) { if (!_restoring) ScheduleSave(); }
    partial void OnNnTolChanged(decimal value) { if (!_restoring) ScheduleSave(); }
    partial void OnShowLatticeChanged(bool value) { if (!_restoring) ScheduleSave(); }
    partial void OnShowSampleChanged(bool value) { if (!_restoring) ScheduleSave(); }
    partial void OnShowDimChanged(bool value) { if (!_restoring) ScheduleSave(); }
    partial void OnShowPlaneChanged(bool value) { if (!_restoring) ScheduleSave(); }
    partial void OnSampleQueryChanged(string value) => RefreshFilteredSamples();
    partial void OnSelectedGroupChanged(string value) => RefreshFilteredSamples();
    partial void OnCropChanged(double value)
    {
        OnPropertyChanged(nameof(CropLabel));
        if (_restoring)
        {
            OverlayCrop = value;
            return;
        }

        ScheduleSave();
        _cropCts?.Cancel();
        _cropCts = new CancellationTokenSource();
        var token = _cropCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(80, token).ConfigureAwait(false);
                await OnUi(() => OverlayCrop = value).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    partial void OnBatchProgressChanged(double value) => BatchProgressPct = $"{value:0}%";
    partial void OnJobProgressChanged(double value) => JobProgressPct = $"{value:0}%";

    partial void OnFaceIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedFace));
        SyncFaceChoice();
        if (!_restoring) ScheduleSave();
    }

    partial void OnWorkerLineChanged(string value) => OnPropertyChanged(nameof(WorkerChip));

    private static double ClampTol(double value, double fallback)
    {
        return double.IsFinite(value) && value > 0 ? Math.Clamp(value, 1e-12, 1e-2) : fallback;
    }

    private static double ClampFfcCutOff(double value)
    {
        return double.IsFinite(value) ? Math.Clamp(value, 0.5, 4.0) : 1.3;
    }

    private static double ClampFfcMaxVal(double value, double cutOff)
    {
        var max = double.IsFinite(value) ? Math.Clamp(value, 1.0, 16.0) : 1.8;
        return max > cutOff ? max : cutOff + 0.5;
    }

    private static double ClampClipLo(double value)
    {
        return double.IsFinite(value) ? Math.Clamp(value, -2.0, 0.99) : -0.2;
    }

    private static double ClampClipHi(double value, double lo)
    {
        var hi = double.IsFinite(value) ? Math.Clamp(value, 0.01, 8.0) : 1.3;
        return hi > lo ? hi : Math.Min(1.3, lo + 0.5);
    }

    private static string ShortenWorkerLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return "Starting";
        }

        return line
            .Replace("NVIDIA GeForce ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase);
    }

    partial void OnIsLightThemeChanged(bool value)
    {
        ThemeLabel = value ? "Dark" : "Light";
        ThemeHint = value ? "Switch to dark theme" : "Switch to light theme";
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = value ? ThemeVariant.Light : ThemeVariant.Dark;
        }

        if (!_restoring)
        {
            Log("Theme", value ? "Switched to light theme" : "Switched to dark theme");
            ScheduleSave();
        }
    }

    partial void OnHasCorrectedChanged(bool value) => OnPropertyChanged(nameof(HasStageCorrected));

    partial void OnShowCorrectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowOriginal));
        if (value && StageName != "Corrected")
        {
            StageName = "Corrected";
        }

        UpdateDisplayedPreview();
        if (HasPicker)
        {
            PeekColor(_peekX, _peekY, pin: _pickerPinned, showLoupe: ShowLoupe);
        }
    }
}
