using System;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media.Imaging;
using ColorCorrect.App.Models;
using ColorCorrect.Engine;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ColorCorrect.App.ViewModels;

/// <summary>Window layout preferences: light/dark theme with its label and hint, and which sidebar sections are expanded.</summary>
public partial class LayoutViewModel : ViewModelBase
{
    [ObservableProperty] private string _themeLabel = "Light";
    [ObservableProperty] private string _themeHint = "Switch to light theme";
    [ObservableProperty] private bool _isLightTheme;
    [ObservableProperty] private bool _libraryExpanded;
    [ObservableProperty] private bool _processExpanded = true;
    [ObservableProperty] private bool _modelExpanded;
}
