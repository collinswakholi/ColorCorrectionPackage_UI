using System;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media.Imaging;
using ColorCorrect.App.Models;
using ColorCorrect.Engine;
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
    [ObservableProperty] private double _jobProgress;
    [ObservableProperty] private string _jobProgressPct = "0%";
    [ObservableProperty] private double _batchProgress;
    [ObservableProperty] private string _batchProgressPct = "0%";
    [ObservableProperty] private string _batchLabel = "";
    [ObservableProperty] private string _currentStage = "";
    [ObservableProperty] private string _status = "Starting ColorCorrect engine…";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _batchCurrentImageLabel = "";
    [ObservableProperty] private string _batchProgressSummary = "";
}
