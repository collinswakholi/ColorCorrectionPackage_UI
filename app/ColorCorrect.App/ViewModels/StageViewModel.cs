using System;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media.Imaging;
using ColorCorrect.App.Models;
using ColorCorrect.Engine;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ColorCorrect.App.ViewModels;

/// <summary>The image stage: preview bitmaps, chart and plane overlay geometry, crop, face choice, display toggles and the selected processing stage.</summary>
public partial class StageViewModel : ViewModelBase
{
    [ObservableProperty] private Bitmap? _originalPreview;
    [ObservableProperty] private Bitmap? _correctedPreview;
    [ObservableProperty] private Bitmap? _preview;
    [ObservableProperty] private HandshakeDocument? _overlayHandshake;
    [ObservableProperty] private FfcPlane? _overlayPlane;
    [ObservableProperty] private Rect _overlayPlaneRect;
    [ObservableProperty] private bool _showPlane = true;
    [ObservableProperty] private double _overlayCrop = 0.75;
    [ObservableProperty] private double _crop = 0.75;
    [ObservableProperty] private string _cropRect = "";
    [ObservableProperty] private double _naturalWidth = 1;
    [ObservableProperty] private double _naturalHeight = 1;
    [ObservableProperty] private double _overlayScaleX = 1;
    [ObservableProperty] private double _overlayScaleY = 1;
    [ObservableProperty] private int _faceIndex;
    [ObservableProperty] private int _overlayFaceChoice;
    [ObservableProperty] private bool _showCorrected;
    [ObservableProperty] private bool _showFacePicker;
    [ObservableProperty] private bool _useAllFaces;
    [ObservableProperty] private bool _showLattice = true;
    [ObservableProperty] private bool _showSample = true;
    [ObservableProperty] private bool _showDim = true;
    [ObservableProperty] private string _stageName = "Original";
    [ObservableProperty] private bool _isStagePreviewLoading;
    [ObservableProperty] private string _stagePreviewLoadingLabel = "";
    [ObservableProperty] private string _lastPreviewDir = "";
    [ObservableProperty] private string _overlayBadge = "";
}
