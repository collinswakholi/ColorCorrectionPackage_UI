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
    private readonly SettingsSaveScheduler _settingsSaver;
    private CancellationTokenSource? _cropCts;
    private SessionItemViewModel? _rangeAnchor;
    private int _previewGeneration;
    private int _sourceWidth = 1;
    private int _sourceHeight = 1;
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
        _settingsSaver = new SettingsSaveScheduler(SettingsSnapshot, OnUi, SessionStore.SaveSettings,
            () => Dispatcher.UIThread.Post(() => Log("Settings",
                "Settings could not be saved. Changes apply to this session only; check that the settings folder is writable.",
                LogLevel.Warning)));
        Settings.PropertyChanged += OnSettingChanged;
        Layout.PropertyChanged += OnLayoutStateChanged;
        Output.PropertyChanged += OnOutputStateChanged;
        Job.PropertyChanged += OnJobStateChanged;
        Stage.PropertyChanged += OnStageStateChanged;
        Compute.PropertyChanged += OnComputeStateChanged;
        Workspace.PropertyChanged += OnWorkspaceStateChanged;
        Settings.DraftEdited += (_, _) => RefreshValidation();
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
        Settings.SelectedMethod = Methods[0];
        Settings.SelectedFfcFitMethod = Pick(FfcFitMethods, "poly");
        Settings.SelectedNnOptimizer = NnOptimizers[0];
        Settings.SelectedInputSpace = Pick(RgbSpaces, "srgb");
        Settings.SelectedInputWhite = Pick(WhitePoints, "d65");
        Settings.SelectedTargetSpace = Pick(RgbSpaces, "srgb");
        Settings.SelectedTargetWhite = Pick(WhitePoints, "d65");
        Output.SelectedOutputFormat = OutputFormats[1];
        SyncDegreeAndAdvanced();
        HookWorkerProgress();
        if (_persistenceEnabled)
        {
            var hasSavedSettings = File.Exists(SessionStore.SettingsPath());
            RestoreSession();
            if (!hasSavedSettings)
            {
                ApplyInstallationDefaults();
                ShowFirstRunSetup = File.Exists(SetupHelperPath) && !File.Exists(InstalledSetup.StatePath);
                if (ShowFirstRunSetup) Compute.SetupSummary = "Choose Recommended, CPU only, or Custom. Check setup verifies installed support; installation shows the proposed changes before applying them.";
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
    public ObservableCollection<LogEntry> LogEntries { get; } = [];
    public ObservableCollection<PipelineStepViewModel> PipelineSteps { get; } = [];

    private FfcPlane? _loadedModelPlane;

    /// <summary>ΔE tiles, per-stage and per-patch rows, report and analysis panel states.</summary>
    public AnalysisViewModel Analysis { get; } = new();

    private void OnHasSessionChanged(bool value) => Analysis.HasSession = value;


    /// <summary>Correction settings (stages, FFC, color spaces, CC model and its parameters).</summary>
    public CorrectionSettingsViewModel Settings { get; } = new();


    private void OnSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Same side effects as the former generated On*Changed hooks. Views bind through
        // Settings.*, so the shell does not re-raise sub-view-model property names.
        switch (e.PropertyName)
        {
            case nameof(CorrectionSettingsViewModel.DoFfc): OnDoFfcChanged(Settings.DoFfc); break;
            case nameof(CorrectionSettingsViewModel.DoGc): OnDoGcChanged(Settings.DoGc); break;
            case nameof(CorrectionSettingsViewModel.DoWb): OnDoWbChanged(Settings.DoWb); break;
            case nameof(CorrectionSettingsViewModel.DoCc): OnDoCcChanged(Settings.DoCc); break;
            case nameof(CorrectionSettingsViewModel.WhiteImagePath): OnWhiteImagePathChanged(Settings.WhiteImagePath); break;
            case nameof(CorrectionSettingsViewModel.SelectedFfcFitMethod): OnSelectedFfcFitMethodChanged(Settings.SelectedFfcFitMethod); break;
            case nameof(CorrectionSettingsViewModel.FfcBins): OnFfcBinsChanged(Settings.FfcBins); break;
            case nameof(CorrectionSettingsViewModel.FfcSmoothWindow): OnFfcSmoothWindowChanged(Settings.FfcSmoothWindow); break;
            case nameof(CorrectionSettingsViewModel.FfcDegree): OnFfcDegreeChanged(Settings.FfcDegree); break;
            case nameof(CorrectionSettingsViewModel.FfcNnMaxIter): OnFfcNnMaxIterChanged(Settings.FfcNnMaxIter); break;
            case nameof(CorrectionSettingsViewModel.FfcTol): OnFfcTolChanged(Settings.FfcTol); break;
            case nameof(CorrectionSettingsViewModel.FfcCutOff): OnFfcCutOffChanged(Settings.FfcCutOff); break;
            case nameof(CorrectionSettingsViewModel.FfcMaxVal): OnFfcMaxValChanged(Settings.FfcMaxVal); break;
            case nameof(CorrectionSettingsViewModel.ClipLo): OnClipLoChanged(Settings.ClipLo); break;
            case nameof(CorrectionSettingsViewModel.ClipHi): OnClipHiChanged(Settings.ClipHi); break;
            case nameof(CorrectionSettingsViewModel.SelectedMethod): OnSelectedMethodChanged(Settings.SelectedMethod); break;
            case nameof(CorrectionSettingsViewModel.SelectedDegree): OnSelectedDegreeChanged(Settings.SelectedDegree); break;
            case nameof(CorrectionSettingsViewModel.SelectedInputSpace): OnSelectedInputSpaceChanged(Settings.SelectedInputSpace); break;
            case nameof(CorrectionSettingsViewModel.SelectedInputWhite): OnSelectedInputWhiteChanged(Settings.SelectedInputWhite); break;
            case nameof(CorrectionSettingsViewModel.SelectedTargetSpace): OnSelectedTargetSpaceChanged(Settings.SelectedTargetSpace); break;
            case nameof(CorrectionSettingsViewModel.SelectedTargetWhite): OnSelectedTargetWhiteChanged(Settings.SelectedTargetWhite); break;
            case nameof(CorrectionSettingsViewModel.FitSamples): OnFitSamplesChanged(Settings.FitSamples); break;
            case nameof(CorrectionSettingsViewModel.PlsComponents): OnPlsComponentsChanged(Settings.PlsComponents); break;
            case nameof(CorrectionSettingsViewModel.HiddenLayersText): OnHiddenLayersTextChanged(Settings.HiddenLayersText); break;
            case nameof(CorrectionSettingsViewModel.SelectedNnOptimizer): OnSelectedNnOptimizerChanged(Settings.SelectedNnOptimizer); break;
            case nameof(CorrectionSettingsViewModel.NnBatchNormalization): OnNnBatchNormalizationChanged(Settings.NnBatchNormalization); break;
            case nameof(CorrectionSettingsViewModel.NnDropout): OnNnDropoutChanged(Settings.NnDropout); break;
            case nameof(CorrectionSettingsViewModel.NnLr): OnNnLrChanged(Settings.NnLr); break;
            case nameof(CorrectionSettingsViewModel.NnEpochs): OnNnEpochsChanged(Settings.NnEpochs); break;
            case nameof(CorrectionSettingsViewModel.NnPatience): OnNnPatienceChanged(Settings.NnPatience); break;
            case nameof(CorrectionSettingsViewModel.NnTol): OnNnTolChanged(Settings.NnTol); break;
            case nameof(CorrectionSettingsViewModel.IncludeCombinations): OnIncludeCombinationsChanged(Settings.IncludeCombinations); break;
            case nameof(CorrectionSettingsViewModel.SkipSaturated): OnSkipSaturatedChanged(Settings.SkipSaturated); break;
        }
    }

    /// <summary>The working session: the active still, session and selection flags, counts, catalog filter and image metadata.</summary>
    public WorkspaceViewModel Workspace { get; } = new();

    private void OnWorkspaceStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WorkspaceViewModel.Active): OnActiveChanged(Workspace.Active); break;
            case nameof(WorkspaceViewModel.HasSession): OnHasSessionChanged(Workspace.HasSession); break;
            case nameof(WorkspaceViewModel.HasImage): OnHasImageChanged(Workspace.HasImage); break;
            case nameof(WorkspaceViewModel.HasCorrected): OnHasCorrectedChanged(Workspace.HasCorrected); break;
            case nameof(WorkspaceViewModel.SampleQuery): OnSampleQueryChanged(Workspace.SampleQuery); break;
            case nameof(WorkspaceViewModel.SelectedGroup): OnSelectedGroupChanged(Workspace.SelectedGroup); break;
        }
    }

    /// <summary>Processing engine state: worker startup and readiness, processing device and acceleration choices, and the GPU setup options.</summary>
    public ComputeViewModel Compute { get; } = new();

    private void OnComputeStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ComputeViewModel.WorkerLine): OnWorkerLineChanged(Compute.WorkerLine); break;
            case nameof(ComputeViewModel.WorkerStartupComplete): OnWorkerStartupCompleteChanged(Compute.WorkerStartupComplete); break;
            case nameof(ComputeViewModel.WorkerStartupError): OnWorkerStartupErrorChanged(Compute.WorkerStartupError); break;
            case nameof(ComputeViewModel.WorkerReady): OnWorkerReadyChanged(Compute.WorkerReady); break;
            case nameof(ComputeViewModel.SelectedProcessingDevice): OnSelectedProcessingDeviceChanged(Compute.SelectedProcessingDevice); break;
            case nameof(ComputeViewModel.ForceCpu): OnForceCpuChanged(Compute.ForceCpu); break;
            case nameof(ComputeViewModel.FastMode): OnFastModeChanged(Compute.FastMode); break;
            case nameof(ComputeViewModel.SelectedAccelPolicy): OnSelectedAccelPolicyChanged(Compute.SelectedAccelPolicy); break;
            case nameof(ComputeViewModel.Phase2Teacher): OnPhase2TeacherChanged(Compute.Phase2Teacher); break;
            case nameof(ComputeViewModel.SelectedSetupMode): OnSelectedSetupModeChanged(Compute.SelectedSetupMode); break;
            case nameof(ComputeViewModel.SetupSummary): OnSetupSummaryChanged(Compute.SetupSummary); break;
        }
    }

    /// <summary>The image stage: preview bitmaps, chart and plane overlay geometry, crop, face choice, display toggles and the selected processing stage.</summary>
    public StageViewModel Stage { get; } = new();

    private void OnStageStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(StageViewModel.ShowPlane): OnShowPlaneChanged(Stage.ShowPlane); break;
            case nameof(StageViewModel.Crop): OnCropChanged(Stage.Crop); break;
            case nameof(StageViewModel.FaceIndex): OnFaceIndexChanged(Stage.FaceIndex); break;
            case nameof(StageViewModel.ShowCorrected): OnShowCorrectedChanged(Stage.ShowCorrected); break;
            case nameof(StageViewModel.ShowLattice): OnShowLatticeChanged(Stage.ShowLattice); break;
            case nameof(StageViewModel.ShowSample): OnShowSampleChanged(Stage.ShowSample); break;
            case nameof(StageViewModel.ShowDim): OnShowDimChanged(Stage.ShowDim); break;
            case nameof(StageViewModel.StageName): OnStageNameChanged(Stage.StageName); break;
            case nameof(StageViewModel.IsStagePreviewLoading): OnIsStagePreviewLoadingChanged(Stage.IsStagePreviewLoading); break;
            case nameof(StageViewModel.LastPreviewDir): OnLastPreviewDirChanged(Stage.LastPreviewDir); break;
        }
    }

    /// <summary>The running operation: busy and batching flags, job and batch progress with their labels, the current stage, the status line and the last error.</summary>
    public JobViewModel Job { get; } = new();

    private void OnJobStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(JobViewModel.Busy): OnBusyChanged(Job.Busy); break;
            case nameof(JobViewModel.IsBatching): OnIsBatchingChanged(Job.IsBatching); break;
            case nameof(JobViewModel.JobProgress): OnJobProgressChanged(Job.JobProgress); break;
            case nameof(JobViewModel.BatchProgress): OnBatchProgressChanged(Job.BatchProgress); break;
        }
    }

    /// <summary>Outputs and models: output folder and format, which results to save and which can be saved, save progress, the report flag, the loaded model and its status, and the batch preview option.</summary>
    public OutputViewModel Output { get; } = new();

    private void OnOutputStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(OutputViewModel.OutputDir): OnOutputDirChanged(Output.OutputDir); break;
            case nameof(OutputViewModel.SelectedOutputFormat): OnSelectedOutputFormatChanged(Output.SelectedOutputFormat); break;
            case nameof(OutputViewModel.IsSaving): OnIsSavingChanged(Output.IsSaving); break;
            case nameof(OutputViewModel.LoadedModelPath): OnLoadedModelPathChanged(Output.LoadedModelPath); break;
            case nameof(OutputViewModel.UseLoadedModel): OnUseLoadedModelChanged(Output.UseLoadedModel); break;
            case nameof(OutputViewModel.BatchPreviewResults): OnBatchPreviewResultsChanged(Output.BatchPreviewResults); break;
        }
    }

    /// <summary>Window layout preferences: light/dark theme with its label and hint, and which sidebar sections are expanded.</summary>
    public LayoutViewModel Layout { get; } = new();

    private void OnLayoutStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LayoutViewModel.IsLightTheme): OnIsLightThemeChanged(Layout.IsLightTheme); break;
        }
    }

    /// <summary>Color picker, loupe, pin and hover-card state.</summary>
    public PickerViewModel Picker { get; } = new();

    public ObservableCollection<string> FaceOptions { get; } = [];
    public string? SelectedFace
    {
        get => Stage.FaceIndex >= 0 && Stage.FaceIndex < FaceOptions.Count ? FaceOptions[Stage.FaceIndex] : null;
        set
        {
            if (value is null)
            {
                return;
            }

            var index = FaceOptions.IndexOf(value);
            if (index >= 0 && index != Stage.FaceIndex)
            {
                Stage.FaceIndex = index;
            }
        }
    }

    public string WorkerChip => ShortenWorkerLine(Compute.WorkerLine);
    public bool NnSettingsValid => !Settings.ShowNn || CorrectionOptions.TryParseHiddenLayers(Settings.HiddenLayersText, out _, out _);
    public bool HasHiddenLayersError => Settings.ShowNn && !CorrectionOptions.TryParseHiddenLayers(Settings.HiddenLayersText, out _, out _);
    public string HiddenLayersError => CorrectionOptions.TryParseHiddenLayers(Settings.HiddenLayersText, out _, out var error) ? "" : error;
    public bool ShowSessionNavigation => Session.Count > 1;
    public bool CanNavigatePrevious => Workspace.Active is not null && Session.IndexOf(Workspace.Active) > 0;
    public bool CanNavigateNext => Workspace.Active is not null && Session.IndexOf(Workspace.Active) >= 0 && Session.IndexOf(Workspace.Active) < Session.Count - 1;
    public string ActivePositionLabel
    {
        get
        {
            var index = Workspace.Active is null ? -1 : Session.IndexOf(Workspace.Active);
            return index < 0 ? "" : $"{index + 1} of {Session.Count} · {Workspace.Active!.DisplayName}";
        }
    }
    public string PreviousSessionHint => CanNavigatePrevious
        ? $"Previous image · {Session[Session.IndexOf(Workspace.Active!) - 1].DisplayName} (Alt+Left)"
        : "No previous image";
    public string NextSessionHint => CanNavigateNext
        ? $"Next image · {Session[Session.IndexOf(Workspace.Active!) + 1].DisplayName} (Alt+Right)"
        : "No next image";
    public string CropLabel => Stage.Crop.ToString("0.00");
    public bool HasLoadedModel => File.Exists(Output.LoadedModelPath);
    public bool WillUseLoadedModel => Output.UseLoadedModel && HasLoadedModel;
    public bool HasWorkerError => Compute.WorkerStartupComplete && !Compute.WorkerReady;
    public bool CanOpenBatch => Compute.WorkerReady && !Job.Busy && !Output.IsSaving && Session.Count > 0;
    public string DetectActionLabel => Workspace.Active is not null && _detectionFailures.ContainsKey(Workspace.Active.Path) ? "Retry" : "Detect";
    public string RunActionHint => !Compute.WorkerReady ? "Wait for the processing engine, or check setup if it could not start."
        : Job.Busy || Output.IsSaving ? "Wait for the current operation, or cancel processing."
        : Workspace.Active is null ? "Add an image to start."
        : !File.Exists(Workspace.Active.Path) ? "Relink the missing image to continue."
        : CachedOperationError(!WillUseLoadedModel) is { } error ? error
        : !WillUseLoadedModel && Workspace.Active.Handshake is not { Ok: true } ? "Detect the chart, or retry detection, before fitting."
        : "Run the active workflow (F5).";
    public string BatchFitHint => !Compute.WorkerReady ? "The processing engine is not ready."
        : Job.Busy || Output.IsSaving ? "Wait for the current operation."
        : !Session.Any(item => item.IncludeInBatch) ? "Select at least one image for the batch."
        : CachedOperationError(true) ?? "Detect a chart and fit each selected image.";
    public string BatchApplyHint => !File.Exists(Output.LoadedModelPath) ? "Load or fit a model before applying it to a batch."
        : !Session.Any(item => item.IncludeInBatch) ? "Select at least one image for the batch."
        : CachedOperationError(false) ?? "Apply the current model to each selected image.";
    public string StatusStateLabel => HasWorkerError
        ? "Engine error"
        : Job.Busy
            ? "Working"
            : Compute.WorkerReady
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
                    Output.UseLoadedModel = true;
                }

                return;
            }

            Output.UseLoadedModel = false;
        }
    }

    public bool IsFitFromImage
    {
        get => !WillUseLoadedModel;
        set
        {
            if (value)
            {
                Output.UseLoadedModel = false;
            }
        }
    }

    public string ApplyLabel => WillUseLoadedModel ? "Apply model" : "Fit + apply";
    public string ModelModeHint => WillUseLoadedModel
        ? "Run applies the loaded GC/WB/CC. Method and degree are ignored. FFC (if on) is recomputed from the white still."
        : Workspace.Active?.Handshake is { Ok: true }
            ? "Run fits a new model from this image and replaces the loaded model."
            : "Detect the chart on this image, then Run to fit a new model.";
    public string WorkflowHint => Workspace.Active is null ? "Add a still, then Fit + apply."
        : WillUseLoadedModel ? "Using the loaded model. Run applies it to this still."
        : Workspace.Active.Handshake is { Ok: true }
            ? Workspace.Active.IsCorrected ? "Corrected. Fit + apply again retrains from this still."
                : "Chart detected. Fit + apply trains a new model from this still."
            : "Detect the chart, then Fit + apply.";
    public bool ShowStillPreview => Workspace.HasImage;
    public bool ShowChartSection => Workspace.HasImage;
    public bool ShowChartOverlay => Stage.OverlayHandshake is { Ok: true };
    public bool ShowOverlayLegend => ShowChartOverlay;
    public bool ShowPlaneOverlay => Stage.OverlayPlane is not null;
    public bool ShowOverlayChrome => ShowChartOverlay || ShowPlaneOverlay;
    public bool ShowPlaneToggle => ShowPlaneOverlay;
    public string PlaneBadge => Stage.OverlayPlane?.Badge ?? "";
    public bool ShowOriginal
    {
        get => !Stage.ShowCorrected;
        set
        {
            if (value)
            {
                Stage.ShowCorrected = false;
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
        Compute.WorkerStartupComplete = false;
        Compute.WorkerStartupError = "";
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
        if (!string.IsNullOrWhiteSpace(Settings.WhiteImagePath) && !File.Exists(Settings.WhiteImagePath))
        {
            var staleName = Path.GetFileName(Settings.WhiteImagePath);
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
            if (!string.IsNullOrWhiteSpace(Settings.WhiteImagePath) && PathsEqual(full, Settings.WhiteImagePath))
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
                Layout.LibraryExpanded = false;
            }

            Log("Session", $"Added {names.Count} file(s).", LogLevel.Info, names);

        }
        if (skipped > 0)
        {
            Job.Status = $"Skipped {skipped} unsupported or unreadable file(s); only JPEG, PNG and TIFF stills are supported.";
            Log("Import", Job.Status, LogLevel.Warning);
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
        Workspace.HasSelected = Session.Any(sessionItem => sessionItem.IsSelected);
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
        var bitmap = Stage.Preview;
        if (bitmap is null || !TryBitmapSize(bitmap, out var previewWidth, out var previewHeight))
        {
            if (showLoupe)
            {
                Picker.HideLoupe();
            }

            return;
        }

        var previewX = Math.Clamp(x, 0, previewWidth - 1);
        var previewY = Math.Clamp(y, 0, previewHeight - 1);
        if (Picker.IsPinned && !pin)
        {
            return;
        }

        if (!pin && Picker.HasPick && previewX == Picker.PreviewX && previewY == Picker.PreviewY)
        {
            return;
        }

        if (!PixelSampler.TrySample(bitmap, previewX, previewY, out var sampled))
        {
            if (showLoupe)
            {
                Picker.HideLoupe();
            }

            return;
        }

        var sourceX = MapPreviewToSource(previewX, previewWidth, _sourceWidth);
        var sourceY = MapPreviewToSource(previewY, previewHeight, _sourceHeight);
        Picker.Show(sampled with { X = sourceX, Y = sourceY }, previewX, previewY, showLoupe, pin, x, y);
    }

    public void PlaceLoupe(double pointerX, double pointerY, double hostW, double hostH) =>
        Picker.PlaceLoupe(pointerX, pointerY, hostW, hostH);

    public void HideLoupe() => Picker.HideLoupe();

    public void PeekPlot(PatchRow row, int index, bool corrected, double x, double y, double hostW, double hostH) =>
        Picker.PeekPlot(row, index, corrected, x, y, hostW, hostH);

    public void HidePlotHover() => Picker.HidePlotHover();

    public void ClearPicker() => Picker.Clear();

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
        Workspace.Active = null;
        _rangeAnchor = null;
        Stage.LastPreviewDir = "";
        SetPreview(null, null);
        Stage.OverlayHandshake = null;
        ApplyReport(null);
        Compute.DetectSummary = "Waiting for detect";
        Workspace.HasImage = false;
        Workspace.HasCorrected = false;
        Stage.ShowFacePicker = false;
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
            Job.Status = "No previous session to delete.";
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

        if (!string.IsNullOrWhiteSpace(Stage.LastPreviewDir) && !Directory.Exists(Stage.LastPreviewDir))
        {
            Stage.LastPreviewDir = "";
        }

        if (!string.IsNullOrWhiteSpace(Output.LoadedModelPath) && !File.Exists(Output.LoadedModelPath))
        {
            Output.LoadedModelPath = "";
            _loadedModelPlane = null;
        }

        ClearSession();
        Job.Status = $"Deleted {deleted} run folder(s). Settings kept.";
        Log("Session", Job.Status, LogLevel.Warning, [$"{deleted} of {dirs.Count} folders removed"]);
        FlushPersist();
    }

    public void FlushPersist()
    {
        _settingsSaver.CancelPending();
        if (_persistenceEnabled && !_restoring)
        {
            Persist();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void RemoveSelected()
    {
        var victims = Session.Where(item => item.IsSelected).ToList();
        if (victims.Count == 0 && Workspace.Active is not null)
        {
            victims.Add(Workspace.Active);
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

        if (Workspace.Active is not null && !Session.Contains(Workspace.Active))
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
        var path = Workspace.Active?.ReportPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Job.Status = "No report for the active image.";
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

    [RelayCommand(CanExecute = nameof(CanNavigatePrevious))]
    private void PreviousSession() => NavigateSession(-1);

    [RelayCommand(CanExecute = nameof(CanNavigateNext))]
    private void NextSession() => NavigateSession(1);

    private void NavigateSession(int delta)
    {
        if (Workspace.Active is null)
        {
            return;
        }

        var index = Session.IndexOf(Workspace.Active);
        var next = index + delta;
        if (next >= 0 && next < Session.Count)
        {
            HandleSessionClick(Session[next], toggle: false, range: false);
        }
    }

    [RelayCommand]
    private void ToggleTheme() => Layout.IsLightTheme = !Layout.IsLightTheme;

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
        if (Picker.LastPick is not { } pick || Dialogs is null)
        {
            Job.Status = "Right-click a point on the image first.";
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
        Job.Status = part switch
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

        Output.OutputDir = folder;
        if (!await EnsureOutputAllowedAsync())
        {
            Output.OutputDir = "";
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

        Output.LoadedModelPath = path;
        RestoreLoadedModelSettings(path);
        Output.UseLoadedModel = true;
        _loadedModelPlane = FfcPlane.TryLoad(path);
        Output.ModelIoStatus = $"Loaded {Path.GetFileName(path)}";
        Log("Model", $"Loaded {Path.GetFileName(path)}.", LogLevel.Success);
        RefreshOverlay();
        NotifyRunCommands();
        ScheduleSave();
    }

    [RelayCommand(CanExecute = nameof(CanSaveModel))]
    private async Task SaveModelAsync()
    {
        if (Dialogs is null || !File.Exists(Output.LoadedModelPath))
        {
            return;
        }

        var dest = await Dialogs.SaveJsonAsync("Save correction model", Path.GetFileName(Output.LoadedModelPath));
        if (string.IsNullOrWhiteSpace(dest))
        {
            return;
        }

        File.Copy(Output.LoadedModelPath, dest, overwrite: true);
        Output.ModelIoStatus = $"Saved {Path.GetFileName(dest)}";
        Log("Model", $"Saved {Path.GetFileName(dest)}.", LogLevel.Success, [dest]);
    }

    [RelayCommand]
    private async Task ExecuteSaveAsync()
    {
        var items = Session.Where(item => item.IncludeInSave && item.HasSaveable).ToList();
        if (items.Count == 0)
        {
            Job.Status = "Nothing selected to save.";
            ToastService.ShowWarning(Job.Status);
            return;
        }

        if (!Output.SaveCorrected && !(Output.SaveFfc && Output.CanSaveFfc) && !(Output.SaveGc && Output.CanSaveGc)
            && !(Output.SaveWb && Output.CanSaveWb) && !(Output.SaveCc && Output.CanSaveCc) && !Output.SaveReport)
        {
            Job.Status = "Choose at least one output to save.";
            ToastService.ShowWarning(Job.Status);
            return;
        }

        // Keep the Avalonia synchronization context while validating the selected
        // destination and, below, confirming replacement of existing files.
        if (!await EnsureOutputAllowedAsync())
        {
            return;
        }

        var destDir = string.IsNullOrWhiteSpace(Output.OutputDir)
            ? Path.Combine(RunPaths.RunsRoot(_worker.RepoRoot), "saved")
            : Path.IsPathRooted(Output.OutputDir) ? Output.OutputDir : Path.GetFullPath(Path.Combine(_worker.RepoRoot, Output.OutputDir));
        var format = Output.SelectedOutputFormat?.Value ?? "jpg";
        var ext = format.Equals("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
        Output.IsSaving = true;
        Output.ShowSaveProgress = true;
        Output.SaveProgress = 0;
        Output.SaveProgressLabel = $"Preparing {items.Count} image(s)…";
        Job.CurrentStage = "Save";
        var jobs = new List<(string Src, string Dest)>();
        var failures = new ConcurrentQueue<string>();
        var artifactNames = CorrectionClient.BuildArtifactBases(items.Select(item => item.Path).ToArray());
        try
        {
            Directory.CreateDirectory(destDir);
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                Output.SaveProgress = 40.0 * i / Math.Max(1, items.Count);
                Output.SaveProgressLabel = $"Preparing {item.FileName}…";
                var stem = artifactNames[i];
                if (Output.SaveCorrected)
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

                if (Output.SaveFfc && ResultStageEnabled(item, "ffc"))
                {
                    await EnsureItemStageAsync(item, "ffc");
                    if (!QueueStageCopy(jobs, item, destDir, stem, "ffc", ext)) failures.Enqueue($"{item.FileName}: requested FFC stage is unavailable.");
                }

                if (Output.SaveGc && ResultStageEnabled(item, "gc"))
                {
                    await EnsureItemStageAsync(item, "gc");
                    if (!QueueStageCopy(jobs, item, destDir, stem, "gc", ext)) failures.Enqueue($"{item.FileName}: requested GC stage is unavailable.");
                }

                if (Output.SaveWb && ResultStageEnabled(item, "wb"))
                {
                    await EnsureItemStageAsync(item, "wb");
                    if (!QueueStageCopy(jobs, item, destDir, stem, "wb", ext)) failures.Enqueue($"{item.FileName}: requested WB stage is unavailable.");
                }

                if (Output.SaveCc && ResultStageEnabled(item, "cc"))
                {
                    await EnsureItemStageAsync(item, "cc");
                    if (!QueueStageCopy(jobs, item, destDir, stem, "cc", ext))
                    {
                        if (!QueueStageCopy(jobs, item, destDir, stem, "cc", ext, sourceStage: "corrected")) failures.Enqueue($"{item.FileName}: requested CC stage is unavailable.");
                    }
                }

                if (Output.SaveReport && File.Exists(item.ReportPath))
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
                Job.Status = "Save cancelled. Existing files were kept.";
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
                        Output.SaveProgress = 40 + 60.0 * n / Math.Max(1, jobs.Count);
                        Output.SaveProgressLabel = $"Saving {n} / {jobs.Count}";
                    }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failures.Enqueue($"{Path.GetFileName(job.Dest)}: {ex.Message}");
                }
            });

            Job.Status = failures.IsEmpty
                ? copied == 0
                    ? "No matching stage files to copy."
                    : $"Saved {copied} file(s) as {format.ToUpperInvariant()} to {destDir}"
                : copied == 0
                    ? $"Save failed: {failures.Count} file(s) could not be written."
                    : $"Saved {copied} file(s); {failures.Count} file(s) failed. Check the save log.";
            Log("Save", Job.Status, failures.IsEmpty && copied > 0 ? LogLevel.Success : LogLevel.Warning,
                failures.IsEmpty ? null : failures.ToArray());
            if (copied > 0 && failures.IsEmpty)
            {
                ToastService.ShowSuccess(Job.Status);
            }
            else
            {
                ToastService.ShowWarning(Job.Status);
            }
        }
        catch (Exception ex)
        {
            Job.Status = $"Save failed: {ex.Message}";
            Log("Save", Job.Status, LogLevel.Error);
            ToastService.ShowError(Job.Status);
        }
        finally
        {
            Output.IsSaving = false;
            Output.ShowSaveProgress = false;
            Output.SaveProgress = 0;
            Output.SaveProgressLabel = "";
            Job.CurrentStage = "";
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
        Settings.SelectedMethod = Methods[0];
        SyncDegreeAndAdvanced();
        Settings.SelectedDegree = DegreeOptions.FirstOrDefault(x => x.Value == "2") ?? DegreeOptions.FirstOrDefault();
        Settings.SelectedInputSpace = Pick(RgbSpaces, "srgb");
        Settings.SelectedInputWhite = Pick(WhitePoints, "d65");
        Settings.SelectedTargetSpace = Pick(RgbSpaces, "srgb");
        Settings.SelectedTargetWhite = Pick(WhitePoints, "d65");
        Output.SelectedOutputFormat = OutputFormats[1];
        Settings.FitSamples = 1;
        Settings.PlsComponents = 0;
        Settings.HiddenLayersText = "100";
        Settings.SelectedNnOptimizer = NnOptimizers[0];
        Settings.NnBatchNormalization = false;
        Settings.NnDropout = 0;
        Settings.NnLr = 0.001m;
        Settings.NnEpochs = 10000;
        Settings.NnPatience = 1500;
        Settings.NnTol = 0.000001m;
        Settings.SkipSaturated = false;
        Settings.IncludeCombinations = true;
        Settings.DoFfc = false;
        Settings.DoGc = true;
        Settings.DoWb = true;
        Settings.DoCc = true;
        Settings.SelectedFfcFitMethod = Pick(FfcFitMethods, "poly");
        Settings.FfcBins = 50;
        Settings.FfcSmoothWindow = 5;
        Settings.FfcDegree = 3;
        Settings.FfcNnMaxIter = 10000;
        Settings.FfcTol = 0.00000001m;
        Settings.FfcCutOff = 1.3m;
        Settings.FfcMaxVal = 1.8m;
        Settings.ClipLo = -0.2m;
        Settings.ClipHi = 1.3m;
        Compute.Phase2Teacher = true;
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
            _settingsSaver.CancelPending();
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
        var original = Stage.OriginalPreview;
        var corrected = Stage.CorrectedPreview;
        var displayed = Stage.Preview;
        Stage.Preview = null;
        Stage.OriginalPreview = null;
        Stage.CorrectedPreview = null;
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
        Stage.LastPreviewDir = Directory.Exists(item.PreviewDir) ? item.PreviewDir : "";
        if (Stage.StageName != "Original")
        {
            Stage.StageName = "Original";
        }

        Stage.ShowCorrected = false;
        Workspace.HasCorrected = false;
        ClearPicker();
        SetPreview(null, null);
        ApplyReport(null);

        Workspace.Active = item;
        NotifyStageRadios();
        Workspace.HasImage = true;
        RebuildFaces(item);
        if (ImageSizeReader.TryRead(item.Path, out var width, out var height))
        {
            SetSourceSize(width, height);
        }

        ApplyReport(item.Report);
        RefreshOverlay();
        Output.HasReport = File.Exists(item.ReportPath);
        OpenReportCommand.NotifyCanExecuteChanged();
        NotifyRunCommands();
        _ = LoadPreviewsAsync(item, gen);

        MaybeAutoDetect();
        OnPropertyChanged(nameof(IsResultPreviewSuppressed));
        if (!item.PreviewSuppressed && !string.IsNullOrWhiteSpace(Stage.LastPreviewDir) && item.IsCorrected)
        {
            Stage.StageName = "Corrected";
            ApplyStagePreview();
        }
    }

    private void RebuildFaces(SessionItemViewModel item)
    {
        FaceOptions.Clear();
        var handshake = item.Handshake;
        if (handshake is null || handshake.ChartCount <= 1)
        {
            Stage.ShowFacePicker = false;
            Stage.UseAllFaces = false;
            Stage.FaceIndex = 0;
            Stage.OverlayFaceChoice = 0;
            return;
        }

        foreach (var label in handshake.FaceLabels())
        {
            var parts = label.Split(". ", 2);
            FaceOptions.Add(parts.Length == 2 ? $"{parts[0]}. {ColorSpaces.ChartLabel(parts[1])}" : label);
        }

        FaceOptions.Add("All charts");
        Stage.ShowFacePicker = true;
        Stage.FaceIndex = item.UseAllFaces
            ? FaceOptions.Count - 1
            : Math.Clamp(item.FaceChoice, 0, Math.Max(0, FaceOptions.Count - 2));
        OnPropertyChanged(nameof(SelectedFace));
        SyncFaceChoice();
    }

    private void SyncFaceChoice()
    {
        Stage.UseAllFaces = Stage.ShowFacePicker && FaceOptions.Count > 0 && Stage.FaceIndex == FaceOptions.Count - 1 && FaceOptions[^1] == "All charts";
        Stage.OverlayFaceChoice = Stage.UseAllFaces ? 0 : Math.Max(0, Stage.FaceIndex);
        if (Workspace.Active is not null)
        {
            Workspace.Active.UseAllFaces = Stage.UseAllFaces;
            if (!Stage.UseAllFaces)
            {
                Workspace.Active.FaceChoice = Stage.OverlayFaceChoice;
            }
        }
    }

    private void SetPreview(Bitmap? original, Bitmap? corrected)
    {
        var oldOriginal = Stage.OriginalPreview;
        var oldCorrected = Stage.CorrectedPreview;
        var oldPreview = Stage.Preview;
        Stage.OriginalPreview = original;
        Stage.CorrectedPreview = corrected;
        Stage.Preview = null;
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
        if (ReferenceEquals(candidate, Stage.Preview) || ReferenceEquals(candidate, Stage.OriginalPreview)
            || ReferenceEquals(candidate, Stage.CorrectedPreview) || _stageBitmaps.ContainsValue(candidate))
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
        if (!string.IsNullOrWhiteSpace(Stage.LastPreviewDir))
        {
            ApplyStagePreview();
        }

        if (Stage.Preview is null || !TryBitmapSize(Stage.Preview, out _, out _))
        {
            Stage.Preview = Stage.ShowCorrected && Stage.CorrectedPreview is not null ? Stage.CorrectedPreview : Stage.OriginalPreview;
        }
        if (_sourceWidth > 1 && _sourceHeight > 1)
        {
            Workspace.ImageMeta = $"{_sourceWidth} × {_sourceHeight}";
        }
        else if (Stage.Preview is not null && TryBitmapSize(Stage.Preview, out var width, out var height))
        {
            SetSourceSize(width, height);
        }
        else
        {
            Workspace.ImageMeta = "";
        }

        SyncDisplaySize();

        RefreshOverlay();
    }

    private bool ActiveIsWhiteRef()
    {
        if (Workspace.Active is null || string.IsNullOrWhiteSpace(Settings.WhiteImagePath) || !File.Exists(Settings.WhiteImagePath))
        {
            return false;
        }

        return PathsEqual(Workspace.Active.Path, Settings.WhiteImagePath);
    }

    private void EnsureWhiteInSession(bool activate)
    {
        RefreshWhiteBadges();
        if (string.IsNullOrWhiteSpace(Settings.WhiteImagePath) || !File.Exists(Settings.WhiteImagePath))
        {
            RefreshOverlay();
            return;
        }

        var existing = Session.FirstOrDefault(item => PathsEqual(item.Path, Settings.WhiteImagePath));
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
        var original = Stage.StageName == "Original";
        var handshake = original ? Workspace.Active?.Handshake : null;
        Stage.OverlayHandshake = handshake is { Ok: true } ? handshake : null;
        // Plane is detected on the white card. Draw it only on that still.
        var plane = original && ActiveIsWhiteRef()
            ? _loadedModelPlane ?? Workspace.Active?.FfcPlane ?? Session.Select(item => item.FfcPlane).FirstOrDefault(p => p is { IsValid: true })
            : null;
        Stage.OverlayPlane = plane is { IsValid: true } ? plane : null;
        Stage.OverlayBadge = Stage.OverlayHandshake is { Ok: true }
            ? ColorSpaces.ChartLabel(Stage.OverlayHandshake.ChartType)
            : Stage.OverlayPlane?.Badge ?? "";
        if (Stage.OverlayPlane is { } p)
        {
            var (x, y, w, h) = p.MapToSource(Math.Max(1, _sourceWidth), Math.Max(1, _sourceHeight));
            Stage.OverlayPlaneRect = new Rect(x, y, w, h);
        }
        else
        {
            Stage.OverlayPlaneRect = default;
        }

        OnPropertyChanged(nameof(ShowChartOverlay));
        OnPropertyChanged(nameof(ShowOverlayLegend));
        OnPropertyChanged(nameof(ShowPlaneOverlay));
        OnPropertyChanged(nameof(ShowOverlayChrome));
        OnPropertyChanged(nameof(ShowPlaneToggle));
        OnPropertyChanged(nameof(PlaneBadge));
    }

    private void ApplyReport(CorrectionReport? report) =>
        Analysis.Apply(report, Workspace.HasCorrected, () => BuildOptions().CliModel());

    public void PrepareSaveMenu() => RefreshSessionUi();

    private void RefreshSessionUi()
    {
        Workspace.HasSession = Session.Count > 0;
        Workspace.SessionCountLabel = Session.Count.ToString();
        Workspace.HasSelected = Session.Any(item => item.IsSelected);
        Workspace.HasSaveItems = Session.Any(item => item.HasSaveable);
        Output.CanSaveCorrected = Session.Any(item => item.IncludeInSave && (item.IsCorrected || File.Exists(item.CorrectedPath)));
        Output.CanSaveFfc = Session.Any(item => item.IncludeInSave && item.IsCorrected && ResultStageEnabled(item, "ffc"));
        Output.CanSaveGc = Session.Any(item => item.IncludeInSave && item.IsCorrected && ResultStageEnabled(item, "gc"));
        Output.CanSaveWb = Session.Any(item => item.IncludeInSave && item.IsCorrected && ResultStageEnabled(item, "wb"));
        Output.CanSaveCc = Session.Any(item => item.IncludeInSave && item.IsCorrected && ResultStageEnabled(item, "cc"));
        Output.CanSaveReport = Session.Any(item => item.IncludeInSave && File.Exists(item.ReportPath));
        Workspace.HasWhiteRef = !string.IsNullOrWhiteSpace(Settings.WhiteImagePath) && File.Exists(Settings.WhiteImagePath);
        RefreshWhiteBadges();
        Output.HasReport = Workspace.Active is not null && File.Exists(Workspace.Active.ReportPath);
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
            item.IsWhiteRef = !string.IsNullOrWhiteSpace(Settings.WhiteImagePath)
                && PathsEqual(item.Path, Settings.WhiteImagePath);
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
    private bool CanRemove() => Workspace.HasSelected;
    private bool CanOpenReport() => Output.HasReport;
    private bool CanDetect() =>
        Compute.WorkerReady && !Job.Busy && !Output.IsSaving && Workspace.Active is not null && File.Exists(Workspace.Active.Path);
    private bool CanApply() =>
        Compute.WorkerReady && !Job.Busy && !Output.IsSaving && Workspace.Active is not null && File.Exists(Workspace.Active.Path) && CachedOperationError(!WillUseLoadedModel) is null &&
        (WillUseLoadedModel
            || Workspace.Active.Handshake is { Ok: true });
    private bool CanBatchFit() => Compute.WorkerReady && !Job.Busy && !Output.IsSaving && CachedOperationError(true) is null && Session.Any(item => item.IncludeInBatch);
    private bool CanBatchApply() =>
        Compute.WorkerReady && !Job.Busy && !Output.IsSaving && CachedOperationError(false) is null && Session.Any(item => item.IncludeInBatch) && File.Exists(Output.LoadedModelPath);
    private bool CanRestartWorker() => !Job.Busy && !Output.IsSaving;
    private bool CanCancel() => Job.Busy;
    private bool CanSaveModel() => !Job.Busy && File.Exists(Output.LoadedModelPath);
    private bool CanExportCube() => !Job.Busy && File.Exists(Output.LoadedModelPath);

    private CorrectionOptions BuildOptions()
    {
        var hiddenLayers = CorrectionOptions.TryParseHiddenLayers(Settings.HiddenLayersText, out var parsedLayers, out _)
            ? parsedLayers
            : [100];
        return new CorrectionOptions
        {
            LutDevice = Compute.SelectedProcessingDevice.Value,
            FastMode = Compute.FastMode,
            BatchPreviewResults = Output.BatchPreviewResults,
            Method = Settings.SelectedMethod.Value,
            Degree = int.TryParse(Settings.SelectedDegree?.Value, out var degree) ? degree : 2,
            IncludeCombinations = Settings.IncludeCombinations,
            FitSamples = (int)Math.Clamp(Settings.FitSamples, 1, 200),
            SkipSaturated = Settings.SkipSaturated,
            InputSpace = Settings.SelectedInputSpace.Value,
            InputWhite = Settings.SelectedInputWhite.Value,
            TargetSpace = Settings.SelectedTargetSpace.Value,
            TargetWhite = Settings.SelectedTargetWhite.Value,
            OutputFormat = Output.SelectedOutputFormat.Value,
            OutputDir = Output.OutputDir,
            PlsComponents = (int)Math.Clamp(Settings.PlsComponents, 0, 64),
            HiddenLayers = hiddenLayers,
            NnOptimizer = Settings.SelectedNnOptimizer.Value,
            NnBatchNormalization = Settings.NnBatchNormalization,
            NnDropout = (double)Math.Clamp(Settings.NnDropout, 0, 0.8m),
            NnLr = (double)Settings.NnLr,
            NnEpochs = (int)Math.Clamp(Settings.NnEpochs, 50, 20000),
            NnPatience = (int)Math.Clamp(Settings.NnPatience, 1, 5000),
            NnTol = ClampTol((double)Settings.NnTol, 1e-6),
            LoadedModelPath = Output.LoadedModelPath,
            ModelInPath = Output.LoadedModelPath,
            UseLoadedModel = Output.UseLoadedModel,
            DoFfc = Settings.DoFfc,
            DoGc = Settings.DoGc,
            DoWb = Settings.DoWb,
            DoCc = Settings.DoCc,
            WhiteImagePath = Settings.WhiteImagePath,
            CropRect = Stage.CropRect,
            FfcFitMethod = Settings.SelectedFfcFitMethod?.Value ?? "poly",
            FfcBins = (int)Math.Clamp(Settings.FfcBins, 4, 200),
            FfcSmoothWindow = (int)Math.Clamp(Settings.FfcSmoothWindow, 1, 51),
            FfcDegree = (int)Math.Clamp(Settings.FfcDegree, 1, 7),
            FfcNnMaxIter = (int)Math.Clamp(Settings.FfcNnMaxIter, 50, 20000),
            FfcTol = ClampTol((double)Settings.FfcTol, 1e-8),
            FfcCutOff = ClampFfcCutOff((double)Settings.FfcCutOff),
            FfcMaxVal = ClampFfcMaxVal((double)Settings.FfcMaxVal, ClampFfcCutOff((double)Settings.FfcCutOff)),
            ClipLo = ClampClipLo((double)Settings.ClipLo),
            ClipHi = ClampClipHi((double)Settings.ClipHi, ClampClipLo((double)Settings.ClipLo)),
            Phase2Teacher = Compute.Phase2Teacher,
        }.Normalize();
    }

    private void SyncDegreeAndAdvanced()
    {
        var method = CorrectionOptions.CanonicalMethod(Settings.SelectedMethod.Value);
        Settings.ShowDegree = method != "ccm";
        Settings.ShowPolyCombinations = method == "linear";
        Settings.ShowPls = method == "pls";
        Settings.ShowNn = method == "nn";
        Settings.ShowCcIterParams = method is "pls" or "nn";
        Settings.DegreeLabel = method == "rpcc" ? "Root degree" : "Polynomial degree";
        var keep = Settings.SelectedDegree?.Value ?? "2";
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

        Settings.SelectedDegree = DegreeOptions.FirstOrDefault(item => item.Value == keep) ?? DegreeOptions.FirstOrDefault();
        OnPropertyChanged(nameof(NnSettingsValid));
        OnPropertyChanged(nameof(HasHiddenLayersError));
        OnPropertyChanged(nameof(HiddenLayersError));
        UpdateCcSummary();
        RefreshValidation();
    }

    private void UpdateCcSummary()
    {
        var options = BuildOptions();
        Settings.CcSummary = options.Summary();
        Settings.ProcessSummary = BuildProcessSummary(options);
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
        if (Workspace.Active is null)
        {
            Compute.DetectSummary = "";
            return;
        }



        if (Workspace.Active.Handshake is { Ok: true })
        {
            Compute.DetectSummary = $"{Workspace.Active.DetectLabel} · {Math.Max(1, Workspace.Active.Handshake.ChartCount)} chart(s)";
            return;
        }

        Compute.DetectSummary = WillUseLoadedModel
            ? $"{Workspace.Active.Name} — Run applies the loaded model (no chart needed)"
            : string.IsNullOrWhiteSpace(Workspace.Active.DetectLabel)
                ? $"{Workspace.Active.Name} — Detect the chart, then Run to fit"
                : $"{Workspace.Active.Name} — {Workspace.Active.DetectLabel}";
    }

    private void MaybeAutoDetect()
    {
        if (_disposed || _restoring || !CanDetect() || Workspace.Active is not { Handshake: null } item)
        {
            return;
        }

        _ = AutoDetectIfNeededAsync(item);
    }

    private string ResolvedOutputDir()
    {
        if (string.IsNullOrWhiteSpace(Output.OutputDir))
        {
            return RunPaths.RunsRoot(_worker.RepoRoot);
        }

        return Path.IsPathRooted(Output.OutputDir)
            ? Path.GetFullPath(Output.OutputDir)
            : Path.GetFullPath(Path.Combine(_worker.RepoRoot, Output.OutputDir));
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

        Job.ErrorMessage = level == LogLevel.Error ? detail : "";
        if (level == LogLevel.Error)
        {
            ToastService.ShowError(detail);
        }
    }

    private IReadOnlyList<string> CorrectionSettingsDetails(HandshakeDocument? handshake = null, bool applyOnly = false)
    {
        var options = BuildOptions();
        return [BuildProcessSummary(options),
            $"{Settings.SelectedInputSpace.Label} / {Settings.SelectedInputWhite.Label} → {Settings.SelectedTargetSpace.Label} / {Settings.SelectedTargetWhite.Label}"];
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
        if (string.IsNullOrWhiteSpace(Output.OutputDir))
        {
            return Task.FromResult(true);
        }

        string full;
        try
        {
            full = Path.IsPathRooted(Output.OutputDir)
                ? Path.GetFullPath(Output.OutputDir)
                : Path.GetFullPath(Path.Combine(_worker.RepoRoot, Output.OutputDir));
        }
        catch (Exception ex)
        {
            Job.Status = ex.Message;
            return Task.FromResult(false);
        }

        if (RunPaths.ContainsParentSegment(Output.OutputDir))
        {
            Job.Status = "Output folder cannot contain '..'.";
            Log("Output", Job.Status, LogLevel.Error);
            return Task.FromResult(false);
        }

        // Choosing or entering Save to is the user's destination decision. A
        // packaged app has no user-facing project boundary to approve again.
        Output.OutputDir = full;
        return Task.FromResult(true);
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

        if (Workspace.SelectedGroup != "All" &&
            !SampleGroups.Contains(Workspace.SelectedGroup, StringComparer.OrdinalIgnoreCase))
        {
            Workspace.SelectedGroup = "All";
        }

        RefreshFilteredSamples();
    }

    private void RefreshFilteredSamples()
    {
        FilteredSamples.Clear();
        foreach (var item in _catalog)
        {
            if (Workspace.SelectedGroup != "All" && !string.Equals(item.Group, Workspace.SelectedGroup, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(Workspace.SampleQuery) &&
                item.Name.IndexOf(Workspace.SampleQuery, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            FilteredSamples.Add(item);
        }

        Workspace.SampleCountLabel = FilteredSamples.Count.ToString();
    }

    private async Task StartWorkerAsync()
    {
        var cancellation = BeginJob();
        try
        {
            _worker.Policy = Compute.SelectedAccelPolicy.Value;
            await _worker.StartAsync(cancellation).ConfigureAwait(false);
            var warmed = _worker.Warmed;
            var warmupError = _worker.LastError;
            await OnUi(() =>
            {
                if (ReportWorkerWarmupFailure(warmed, warmupError)) return;
                Compute.WorkerReady = true;
                Compute.WorkerStartupComplete = true;
                Compute.WorkerStartupError = "";
                Compute.WorkerLine = string.IsNullOrWhiteSpace(_worker.Title)
                    ? (_worker.Warmed ? $"Warmed · {_worker.Device}" : $"Ready · {_worker.Device}")
                    : _worker.Title;
                Job.Status = "Ready.";
                Job.CurrentStage = "";
                Log("Engine", _worker.Warmed ? $"Ready · {LabelStage(_worker.Device)}" : "Ready. Detection is checked when needed.", LogLevel.Info);
                // Status bar already shows ready. A toast here covers the viewer.
            }).ConfigureAwait(false);
            await RefreshAccelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await OnUi(() =>
            {
                Compute.WorkerReady = false;
                Compute.WorkerStartupComplete = true;
                Compute.WorkerStartupError = ex.Message;
                Compute.WorkerLine = "Worker failed";
                Job.Status = ex.Message;
                Log("Worker", ex.Message, LogLevel.Error);
            }).ConfigureAwait(false);
        }
        finally
        {
            await OnUi(() =>
            {
                Job.Busy = false;
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
        Workspace.ImageMeta = _sourceWidth > 1 ? $"{_sourceWidth} × {_sourceHeight}" : "";
        SyncDisplaySize();
    }

    private void SyncDisplaySize()
    {
        if (Stage.Preview is not null && TryBitmapSize(Stage.Preview, out var width, out var height))
        {
            Stage.NaturalWidth = width;
            Stage.NaturalHeight = height;
        }
        else if (_sourceWidth > 1)
        {
            var scale = Math.Min(1.0, PreviewMaxWidth / (double)_sourceWidth);
            Stage.NaturalWidth = Math.Max(1, _sourceWidth * scale);
            Stage.NaturalHeight = Math.Max(1, _sourceHeight * scale);
        }
        else
        {
            Stage.NaturalWidth = 1;
            Stage.NaturalHeight = 1;
        }

        Stage.OverlayScaleX = Stage.NaturalWidth / Math.Max(1, _sourceWidth);
        Stage.OverlayScaleY = Stage.NaturalHeight / Math.Max(1, _sourceHeight);
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
                    && !ReferenceEquals(row.Value.Bitmap, Stage.OriginalPreview));
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
            Workspace.HasCorrected = Stage.CorrectedPreview is not null;
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

        if (corrected is null || item.PreviewSuppressed || gen != _previewGeneration || !ReferenceEquals(Workspace.Active, item))
        {
            corrected?.Dispose();
            return;
        }

        await OnUi(() =>
        {
            if (item.PreviewSuppressed || gen != _previewGeneration || !ReferenceEquals(Workspace.Active, item))
            {
                corrected.Dispose();
                return;
            }

            SetPreview(Stage.OriginalPreview, corrected);
            Workspace.HasCorrected = Stage.CorrectedPreview is not null;
            if (Workspace.HasCorrected && Stage.StageName is "Original" or "Corrected")
            {
                Stage.ShowCorrected = true;
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

    private void OnBusyChanged(bool value) => NotifyRunCommands();
    private void OnIsSavingChanged(bool value) => NotifyRunCommands();
    private void OnWorkerReadyChanged(bool value) => NotifyRunCommands();
    private void OnWorkerStartupCompleteChanged(bool value) => NotifyRunCommands();
    private void OnWorkerStartupErrorChanged(string value) => OnPropertyChanged(nameof(HasWorkerError));
    private void OnIsBatchingChanged(bool value) => CancelBatchCommand.NotifyCanExecuteChanged();
    private void OnLoadedModelPathChanged(string value)
    {
        try { _loadedModelIdentity = File.Exists(value) ? AssetIdentity.Capture(value) : null; }
        catch (IOException) { _loadedModelIdentity = null; }
        Output.ModelLabel = File.Exists(value) ? $"Model · {Path.GetFileName(value)}" : "No model loaded";
        Output.SavedModelSummary = DescribeSavedModel(value);
        Output.HasSavedModelSummary = !string.IsNullOrWhiteSpace(Output.SavedModelSummary);
        if (!File.Exists(value) && Output.UseLoadedModel)
        {
            Output.UseLoadedModel = false;
        }

        NotifyRunCommands();
    }

    private void OnUseLoadedModelChanged(bool value)
    {
        if (value && !File.Exists(Output.LoadedModelPath))
        {
            Output.UseLoadedModel = false;
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

    private void OnActiveChanged(SessionItemViewModel? value)
    {
        OnPropertyChanged(nameof(IsResultPreviewSuppressed));
        OnPropertyChanged(nameof(ReferenceSummary));
        OnPropertyChanged(nameof(ShowStillPreview));
        OnPropertyChanged(nameof(ShowChartSection));
        RefreshSessionNavigation();
        NotifyRunCommands();
    }

    private void OnHasImageChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowStillPreview));
        OnPropertyChanged(nameof(ShowChartSection));
    }

    private void OnSelectedMethodChanged(NamedValue value)
    {
        if (!_restoring)
        {
            SyncDegreeAndAdvanced();
            ScheduleSave();
        }
    }

    private void OnSelectedDegreeChanged(NamedValue? value)
    {
        if (!_restoring)
        {
            UpdateCcSummary();
            ScheduleSave();
        }
    }

    private void OnSelectedInputSpaceChanged(NamedValue value) { if (!_restoring) { UpdateCcSummary(); RefreshValidation(); ScheduleSave(); } }
    private void OnSelectedInputWhiteChanged(NamedValue value) { if (!_restoring) { UpdateCcSummary(); RefreshValidation(); ScheduleSave(); } }
    private void OnSelectedTargetSpaceChanged(NamedValue value) { if (!_restoring) { UpdateCcSummary(); ScheduleSave(); } }
    private void OnSelectedTargetWhiteChanged(NamedValue value) { if (!_restoring) { UpdateCcSummary(); ScheduleSave(); } }
    private void OnSelectedOutputFormatChanged(NamedValue value) { if (!_restoring) ScheduleSave(); }
    private void OnSkipSaturatedChanged(bool value) { if (!_restoring) ScheduleSave(); }
    private void OnIncludeCombinationsChanged(bool value)
    {
        if (!_restoring)
        {
            UpdateCcSummary();
            ScheduleSave();
        }
    }
    private void OnOutputDirChanged(string value) { if (!_restoring) ScheduleSave(); }
    private void OnFitSamplesChanged(decimal value) { if (!_restoring) ScheduleSave(); }
    private void OnPlsComponentsChanged(decimal value) { if (!_restoring) ScheduleSave(); }
    private void OnHiddenLayersTextChanged(string value)
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
    private void OnSelectedNnOptimizerChanged(NamedValue value) { if (!_restoring) { UpdateCcSummary(); ScheduleSave(); } }
    private void OnNnBatchNormalizationChanged(bool value) { if (!_restoring) { UpdateCcSummary(); ScheduleSave(); } }
    private void OnNnDropoutChanged(decimal value) { if (!_restoring) { UpdateCcSummary(); ScheduleSave(); } }
    private void OnNnLrChanged(decimal value) { if (!_restoring) ScheduleSave(); }
    private void OnNnEpochsChanged(decimal value) { if (!_restoring) ScheduleSave(); }
    private void OnNnPatienceChanged(decimal value) { if (!_restoring) ScheduleSave(); }
    private void OnNnTolChanged(decimal value) { if (!_restoring) ScheduleSave(); }
    private void OnShowLatticeChanged(bool value) { if (!_restoring) ScheduleSave(); }
    private void OnShowSampleChanged(bool value) { if (!_restoring) ScheduleSave(); }
    private void OnShowDimChanged(bool value) { if (!_restoring) ScheduleSave(); }
    private void OnShowPlaneChanged(bool value) { if (!_restoring) ScheduleSave(); }
    private void OnSampleQueryChanged(string value) => RefreshFilteredSamples();
    private void OnSelectedGroupChanged(string value) => RefreshFilteredSamples();
    private void OnCropChanged(double value)
    {
        OnPropertyChanged(nameof(CropLabel));
        if (_restoring)
        {
            Stage.OverlayCrop = value;
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
                await OnUi(() => Stage.OverlayCrop = value).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void OnBatchProgressChanged(double value) => Job.BatchProgressPct = $"{value:0}%";
    private void OnJobProgressChanged(double value) => Job.JobProgressPct = $"{value:0}%";

    private void OnFaceIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedFace));
        SyncFaceChoice();
        if (!_restoring) ScheduleSave();
    }

    private void OnWorkerLineChanged(string value) => OnPropertyChanged(nameof(WorkerChip));

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

    private void OnIsLightThemeChanged(bool value)
    {
        Layout.ThemeLabel = value ? "Dark" : "Light";
        Layout.ThemeHint = value ? "Switch to dark theme" : "Switch to light theme";
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

    private void OnHasCorrectedChanged(bool value) => OnPropertyChanged(nameof(HasStageCorrected));

    private void OnShowCorrectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowOriginal));
        if (value && Stage.StageName != "Corrected")
        {
            Stage.StageName = "Corrected";
        }

        UpdateDisplayedPreview();
        if (Picker.HasPick)
        {
            PeekColor(Picker.PreviewX, Picker.PreviewY, pin: Picker.IsPinned, showLoupe: Picker.ShowLoupe);
        }
    }
}
