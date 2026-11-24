using System;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media.Imaging;
using ColorCorrect.App.Models;
using ColorCorrect.Engine;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ColorCorrect.App.ViewModels;

/// <summary>The working session: the active still, session and selection flags, counts, catalog filter and image metadata.</summary>
public partial class WorkspaceViewModel : ViewModelBase
{
    [ObservableProperty] private SessionItemViewModel? _active;
    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private bool _hasImage;
    [ObservableProperty] private bool _hasCorrected;
    [ObservableProperty] private bool _hasSelected;
    [ObservableProperty] private bool _hasSaveItems;
    [ObservableProperty] private bool _hasCatalogFolders;
    [ObservableProperty] private bool _hasWhiteRef;
    [ObservableProperty] private string _sessionCountLabel = "0";
    [ObservableProperty] private string _sampleCountLabel = "0";
    [ObservableProperty] private string _sampleQuery = "";
    [ObservableProperty] private string _selectedGroup = "All";
    [ObservableProperty] private string _imageMeta = "";
}
