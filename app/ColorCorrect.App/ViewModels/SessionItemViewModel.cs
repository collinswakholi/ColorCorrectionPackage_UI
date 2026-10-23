using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ColorCorrect.Engine;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ColorCorrect.App.ViewModels;

public partial class SessionItemViewModel : ViewModelBase, IDisposable
{
    private int _loadGen;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isCorrected;
    [ObservableProperty] private bool _isWhiteRef;
    [ObservableProperty] private bool _includeInSave;
    [ObservableProperty] private bool _includeInBatch = true;
    [ObservableProperty] private string _detectLabel = "";
    [ObservableProperty] private Bitmap? _thumb;
    [ObservableProperty] private bool _previewSuppressed;
    [ObservableProperty] private bool _hasProcessingProgress;
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private double _processingProgress;
    [ObservableProperty] private string _processingStage = "";
    [ObservableProperty] private string _processingStatus = "";

    public SessionItemViewModel(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        if (stem.EndsWith("_corrected", StringComparison.OrdinalIgnoreCase)) IncludeInBatch = false;
        _ = LoadThumbAsync();
    }

    public string Path { get; private set; }
    public string Name { get; private set; }
    public bool IsMissing => !File.Exists(Path);
    public CompletedResultContext? CompletedContext { get; set; }
    public AssetIdentity? SourceIdentity { get; set; }
    public string SessionAutomationName => $"Session still {Name}";
    public string RemoveAutomationName => $"Remove {Name} from session";
    public string FileName => Name;
    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(Name);
    public string SessionTip => IsMissing ? $"{Name} · image unavailable — relink to continue" : string.IsNullOrWhiteSpace(DetectLabel) ? Name : $"{Name} · {DetectLabel}";
    public bool HasDetectBadge =>
        !string.IsNullOrWhiteSpace(DetectLabel)
        && DetectLabel is not ("no chart" or "detect failed");
    public bool HasSaveable =>
        IsCorrected
        || File.Exists(CorrectedPath)
        || (!string.IsNullOrWhiteSpace(PreviewDir) && Directory.Exists(PreviewDir));

    public void NotifyOutputs() => OnPropertyChanged(nameof(HasSaveable));
    public HandshakeDocument? Handshake { get; set; }
    public List<ReferenceProfile> ReferenceProfiles { get; set; } = [];
    public List<ChartReferenceAssignment> ReferenceAssignments { get; set; } = [];
    public bool ReferencesNeedAssignment { get; set; }
    public string CorrectedPath { get; set; } = "";
    public string ReportPath { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public string PreviewDir { get; set; } = "";
    public CorrectionReport? Report { get; set; }
    public FfcPlane? FfcPlane { get; set; }
    public int FaceChoice { get; set; }
    public bool UseAllFaces { get; set; }
    public int DetectGeneration { get; set; }
    public int ApplyGeneration { get; set; }

    public bool Relink(string path)
    {
        var identity = SourceIdentity ?? CompletedContext?.Source;
        var same = identity?.Matches(path) == true;
        if (!same)
        {
            Handshake = null;
            ReferencesNeedAssignment = ReferenceAssignments.Count > 0;
            ReferenceAssignments.Clear();
            InvalidateCalibration();
            DetectLabel = "";
        }
        else if (CompletedContext is not null) CompletedContext = CompletedContext.RelinkSource(path);
        Path = System.IO.Path.GetFullPath(path);
        Name = System.IO.Path.GetFileName(path);
        SourceIdentity = AssetIdentity.Capture(path);
        foreach (var property in new[] { nameof(Path), nameof(Name), nameof(FileName), nameof(DisplayName), nameof(SessionTip), nameof(IsMissing), nameof(SessionAutomationName), nameof(RemoveAutomationName) })
            OnPropertyChanged(property);
        _loadGen++;
        _ = LoadThumbAsync();
        return same;
    }

    partial void OnDetectLabelChanged(string value)
    {
        OnPropertyChanged(nameof(HasDetectBadge));
        OnPropertyChanged(nameof(SessionTip));
    }

    public void ApplyHandshake(HandshakeDocument? handshake)
    {
        if (ReferenceAssignments.Count > 0 && Handshake?.SnapshotId != handshake?.SnapshotId)
        {
            ReferenceAssignments.Clear();
            ReferencesNeedAssignment = true;
            InvalidateCalibration();
        }
        Handshake = handshake;
        DetectLabel = handshake is { Ok: true }
            ? ColorSpaces.ChartLabel(handshake.ChartType)
            : handshake is null ? "" : "no chart";
        OnPropertyChanged(nameof(HasDetectBadge));
        OnPropertyChanged(nameof(SessionTip));
    }

    /// <summary>Detach stale results while preserving their saved files on disk.</summary>
    public void InvalidateCalibration()
    {
        ApplyGeneration++;
        IsCorrected = false;
        IncludeInSave = false;
        CorrectedPath = "";
        PreviewDir = "";
        ReportPath = "";
        ModelPath = "";
        Report = null;
        FfcPlane = null;
        CompletedContext = null;
        PreviewSuppressed = false;
        NotifyOutputs();
    }

    public void Dispose()
    {
        _loadGen++;
        DetectGeneration++;
        ApplyGeneration++;
        var old = Thumb;
        Thumb = null;
        old?.Dispose();
    }

    private async Task LoadThumbAsync()
    {
        var gen = ++_loadGen;
        var path = Path;
        AssetIdentity? identity = null;
        Bitmap? bmp;
        try
        {
            bmp = await Task.Run(() =>
            {
                if (SourceIdentity is null && File.Exists(path)) identity = AssetIdentity.Capture(path);
                return ColorCorrect.App.Services.StillImageDecoder.Decode(path, 96);
            }).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (bmp is null || gen != _loadGen)
        {
            bmp?.Dispose();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (gen != _loadGen)
            {
                bmp.Dispose();
                return;
            }

            SourceIdentity ??= identity;
            var old = Thumb;
            Thumb = bmp;
            old?.Dispose();
        });
    }
}
