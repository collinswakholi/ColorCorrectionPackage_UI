using ColorCorrect.App.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ColorCorrect.App.ViewModels;

/// <summary>
/// Stored correction settings: which stages run, flat-field (FFC) fitting, color-space and
/// white-point choices, and the color-correction model with its PLS / neural-network
/// parameters. The shell reacts to changes (summaries, validation, persistence) through
/// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/>.
/// </summary>
public partial class CorrectionSettingsViewModel : ViewModelBase
{
    [ObservableProperty] private bool _doFfc;
    [ObservableProperty] private bool _doGc = true;
    [ObservableProperty] private bool _doWb = true;
    [ObservableProperty] private bool _doCc = true;
    [ObservableProperty] private string _whiteImagePath = "";
    [ObservableProperty] private NamedValue _selectedFfcFitMethod = new("poly", "Polynomial");
    [ObservableProperty] private decimal _ffcBins = 50;
    [ObservableProperty] private decimal _ffcSmoothWindow = 5;
    [ObservableProperty] private decimal _ffcDegree = 3;
    [ObservableProperty] private decimal _ffcNnMaxIter = 10000;
    [ObservableProperty] private decimal _ffcTol = 0.00000001m;
    [ObservableProperty] private decimal _ffcCutOff = 1.3m;
    [ObservableProperty] private decimal _ffcMaxVal = 1.8m;
    [ObservableProperty] private decimal _clipLo = -0.2m;
    [ObservableProperty] private decimal _clipHi = 1.3m;
    [ObservableProperty] private NamedValue _selectedMethod = new("linear", "Linear");
    [ObservableProperty] private NamedValue? _selectedDegree;
    [ObservableProperty] private NamedValue _selectedInputSpace = new("srgb", "sRGB");
    [ObservableProperty] private NamedValue _selectedInputWhite = new("d65", "D65");
    [ObservableProperty] private NamedValue _selectedTargetSpace = new("srgb", "sRGB");
    [ObservableProperty] private NamedValue _selectedTargetWhite = new("d65", "D65");
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
    [ObservableProperty] private bool _includeCombinations = true;
    [ObservableProperty] private bool _skipSaturated;
}
