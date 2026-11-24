using CommunityToolkit.Mvvm.ComponentModel;

namespace ColorCorrect.App.ViewModels;

/// <summary>The running operation: busy and batching flags, job and batch progress with their labels, the current stage, the status line and the last error.</summary>
public partial class JobViewModel : ViewModelBase
{
    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private bool _isBatching;
    [ObservableProperty] private bool _showBatchProgress;
    [ObservableProperty] private bool _showPipeline;
    [ObservableProperty] private bool _showJobProgress;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(JobProgressPct))] private double _jobProgress;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BatchProgressPct))] private double _batchProgress;
    [ObservableProperty] private string _batchLabel = "";
    [ObservableProperty] private string _currentStage = "";
    [ObservableProperty] private string _status = "Starting ColorCorrect engine…";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _batchCurrentImageLabel = "";
    [ObservableProperty] private string _batchProgressSummary = "";

    public string JobProgressPct => $"{JobProgress:0}%";
    public string BatchProgressPct => $"{BatchProgress:0}%";
}
