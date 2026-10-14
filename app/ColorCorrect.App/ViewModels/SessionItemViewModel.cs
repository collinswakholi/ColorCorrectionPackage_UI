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

    public SessionItemViewModel(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        // Outputs written by ColorCorrect use this suffix. Keep them available for
        // comparison, but do not feed them back through correction by default.
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        if (stem.EndsWith("_corrected", StringComparison.OrdinalIgnoreCase))
        {
            IncludeInBatch = false;
        }

        _ = LoadThumbAsync();
    }

    public string Path { get; }
    public string Name { get; }
    public string SessionAutomationName => $"Session still {Name}";
    public string RemoveAutomationName => $"Remove {Name} from session";
    public string FileName => Name;
    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(Name);
    public string SessionTip => string.IsNullOrWhiteSpace(DetectLabel) ? Name : $"{Name} · {DetectLabel}";
    public bool HasDetectBadge =>
        !string.IsNullOrWhiteSpace(DetectLabel)
        && DetectLabel is not ("no chart" or "detect failed");
    public bool HasSaveable =>
        IsCorrected
        || File.Exists(CorrectedPath)
        || (!string.IsNullOrWhiteSpace(PreviewDir) && Directory.Exists(PreviewDir));

    public void NotifyOutputs() => OnPropertyChanged(nameof(HasSaveable));
    public HandshakeDocument? Handshake { get; set; }
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

    partial void OnDetectLabelChanged(string value)
    {
        OnPropertyChanged(nameof(HasDetectBadge));
        OnPropertyChanged(nameof(SessionTip));
    }

    public void ApplyHandshake(HandshakeDocument? handshake)
    {
        Handshake = handshake;
        DetectLabel = handshake is { Ok: true }
            ? ColorSpaces.ChartLabel(handshake.ChartType)
            : handshake is null ? "" : "no chart";
        OnPropertyChanged(nameof(HasDetectBadge));
        OnPropertyChanged(nameof(SessionTip));
    }

    public void Dispose()
    {
        _loadGen++;
        Thumb?.Dispose();
        Thumb = null;
    }

    private async Task LoadThumbAsync()
    {
        var gen = ++_loadGen;
        Bitmap? bmp;
        try
        {
            bmp = await Task.Run(() =>
            {
                return ColorCorrect.App.Services.StillImageDecoder.Decode(Path, 96);
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

            Thumb = bmp;
        });
    }
}
