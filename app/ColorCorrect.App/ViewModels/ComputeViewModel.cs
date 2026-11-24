using System;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media.Imaging;
using ColorCorrect.App.Models;
using ColorCorrect.Engine;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ColorCorrect.App.ViewModels;

/// <summary>Processing engine state: worker startup and readiness, processing device and acceleration choices, and the GPU setup options.</summary>
public partial class ComputeViewModel : ViewModelBase
{
    [ObservableProperty] private string _workerLine = "Worker: starting";
    [ObservableProperty] private bool _workerStartupComplete;
    [ObservableProperty] private string _workerStartupError = "";
    [ObservableProperty] private bool _workerReady;
    [ObservableProperty] private NamedValue _selectedProcessingDevice = new("auto", "Auto (recommended)");
    [ObservableProperty] private bool _forceCpu;
    [ObservableProperty] private bool _fastMode = true;
    [ObservableProperty] private NamedValue _selectedAccelPolicy = new("auto", "Auto (recommended)");
    [ObservableProperty] private bool _phase2Teacher = true;
    [ObservableProperty] private string _accelJson = "";
    [ObservableProperty] private string _accelHint = "Checking graphics hardware…";
    [ObservableProperty] private string _detectSummary = "Waiting for detect";
    [ObservableProperty] private NamedValue _selectedSetupMode = new("recommended", "Recommended");
    [ObservableProperty] private bool _setupWebGpu = true;
    [ObservableProperty] private bool _setupDirectMl = true;
    [ObservableProperty] private string _setupSummary = "";
}
