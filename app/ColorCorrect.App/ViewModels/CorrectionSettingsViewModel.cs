using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ColorCorrect.App.Models;
using ColorCorrect.Engine;
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

    // ---- Numeric text drafts --------------------------------------------------------
    // Dialog fields bind to the *Text properties. A typed value that parses updates the
    // setting; the raw text is kept as a draft so an invalid entry can be reported.

    private readonly Dictionary<string, string> _numericText = new();

    /// <summary>Raised after a numeric field is edited (valid or not), so validation can refresh.</summary>
    public event EventHandler? DraftEdited;

    private string NumericText(string name, decimal value) =>
        _numericText.GetValueOrDefault(name) ?? value.ToString(CultureInfo.CurrentCulture);

    private void SetNumericText(string name, string text, Action<decimal> set)
    {
        _numericText[name] = text;
        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)) set(value);
        DraftEdited?.Invoke(this, EventArgs.Empty);
    }

    public string FitSamplesText { get => NumericText(nameof(FitSamples), FitSamples); set => SetNumericText(nameof(FitSamples), value, v => FitSamples = v); }
    public string PlsComponentsText { get => NumericText(nameof(PlsComponents), PlsComponents); set => SetNumericText(nameof(PlsComponents), value, v => PlsComponents = v); }
    public string NnDropoutText { get => NumericText(nameof(NnDropout), NnDropout); set => SetNumericText(nameof(NnDropout), value, v => NnDropout = v); }
    public string NnLrText { get => NumericText(nameof(NnLr), NnLr); set => SetNumericText(nameof(NnLr), value, v => NnLr = v); }
    public string NnEpochsText { get => NumericText(nameof(NnEpochs), NnEpochs); set => SetNumericText(nameof(NnEpochs), value, v => NnEpochs = v); }
    public string NnPatienceText { get => NumericText(nameof(NnPatience), NnPatience); set => SetNumericText(nameof(NnPatience), value, v => NnPatience = v); }
    public string NnTolText { get => NumericText(nameof(NnTol), NnTol); set => SetNumericText(nameof(NnTol), value, v => NnTol = v); }
    public string FfcBinsText { get => NumericText(nameof(FfcBins), FfcBins); set => SetNumericText(nameof(FfcBins), value, v => FfcBins = v); }
    public string FfcSmoothWindowText { get => NumericText(nameof(FfcSmoothWindow), FfcSmoothWindow); set => SetNumericText(nameof(FfcSmoothWindow), value, v => FfcSmoothWindow = v); }
    public string FfcDegreeText { get => NumericText(nameof(FfcDegree), FfcDegree); set => SetNumericText(nameof(FfcDegree), value, v => FfcDegree = v); }
    public string FfcNnMaxIterText { get => NumericText(nameof(FfcNnMaxIter), FfcNnMaxIter); set => SetNumericText(nameof(FfcNnMaxIter), value, v => FfcNnMaxIter = v); }
    public string FfcTolText { get => NumericText(nameof(FfcTol), FfcTol); set => SetNumericText(nameof(FfcTol), value, v => FfcTol = v); }

    /// <summary>
    /// Discard typed drafts so every field shows its setting again. With
    /// <paramref name="notify"/> false the fields are not told to refresh (used when a whole
    /// set of options is applied and each setting raises its own change).
    /// </summary>
    public void ClearDrafts(bool notify = true)
    {
        var names = _numericText.Keys.ToArray();
        _numericText.Clear();
        if (!notify) return;
        foreach (var name in names) OnPropertyChanged(name + "Text");
    }

    private string? NumberError(string name, decimal value, string label, decimal min, decimal max, bool integer = false)
    {
        if (_numericText.TryGetValue(name, out var text) && !decimal.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            return $"{label}: enter a number.";
        if (value < min || value > max || (integer && value != decimal.Truncate(value)))
            return $"{label}: enter {(integer ? "a whole number" : "a number")} from {min} to {max}.";
        return null;
    }

    // ---- Validation -------------------------------------------------------------------

    /// <summary>
    /// First problem that blocks the next fit (<paramref name="fit"/>) or model apply, or null.
    /// File facts come from the caller: whether the loaded model or its white reference changed,
    /// and whether the white reference is readable (only asked when FFC is on). The method
    /// toggles are the shell's current CC-method switches.
    /// </summary>
    public string? ValidationError(bool fit, bool modelChanged, bool whiteChanged, Func<bool> whiteReadable,
        bool showPls, bool showCcIterParams, bool showNn)
    {
        if (!fit && modelChanged)
            return "The loaded model changed or moved. Load it again before applying it.";
        if (DoFfc)
        {
            if (!whiteReadable()) return "Choose a readable white reference before using FFC.";
            if (!fit && whiteChanged)
                return "The model's white reference changed. Choose the intended white image again before applying it.";
            var error = NumberError(nameof(FfcBins), FfcBins, "FFC bins", 4, 200, true)
                ?? NumberError(nameof(FfcSmoothWindow), FfcSmoothWindow, "FFC smooth window", 1, 51, true);
            if (error is not null) return error;
            if (FfcSmoothWindow % 2 == 0) return "FFC smooth window must be an odd whole number.";
            if (SelectedFfcFitMethod.Value != "linear" && NumberError(nameof(FfcDegree), FfcDegree, "FFC degree", 1, 7, true) is { } degree) return degree;
            if (SelectedFfcFitMethod.Value is "nn" or "pls" && NumberError(nameof(FfcTol), FfcTol, "FFC tolerance", 0.000000000001m, 0.01m) is { } tolerance) return tolerance;
            if (SelectedFfcFitMethod.Value == "nn" && NumberError(nameof(FfcNnMaxIter), FfcNnMaxIter, "FFC iterations", 50, 20000, true) is { } iterations) return iterations;
        }
        if (!fit) return null;
        if ((DoFfc || DoGc || DoWb) && (SelectedInputSpace.Value != "srgb" || SelectedInputWhite.Value != "d65"))
            return "FFC, tone response and white balance require sRGB / D65 input. Convert the image first or turn these stages off.";
        if (NumberError(nameof(FitSamples), FitSamples, "Samples per side", 1, 200, true) is { } samples) return samples;
        if (!DoCc) return null;
        if (showPls && NumberError(nameof(PlsComponents), PlsComponents, "PLS components", 0, 64, true) is { } components) return components;
        if (showCcIterParams && NumberError(nameof(NnTol), NnTol, "Convergence tolerance", 0.000000000001m, 0.01m) is { } tol) return tol;
        if (!showNn) return null;
        if (!CorrectionOptions.TryParseHiddenLayers(HiddenLayersText, out _, out var hiddenError)) return hiddenError;
        return NumberError(nameof(NnDropout), NnDropout, "Dropout rate", 0, 0.8m)
            ?? NumberError(nameof(NnLr), NnLr, "Learning rate", 0.000000000000001m, 1)
            ?? NumberError(nameof(NnEpochs), NnEpochs, "Epochs", 50, 20000, true)
            ?? NumberError(nameof(NnPatience), NnPatience, "Patience", 1, 5000, true);
    }

    // ---- moved from MainWindowViewModel ----
    [ObservableProperty] private bool _showDegree = true;
    [ObservableProperty] private bool _showPolyCombinations = true;
    [ObservableProperty] private bool _showPls;
    [ObservableProperty] private bool _showNn;
    [ObservableProperty] private bool _showCcIterParams;
    [ObservableProperty] private string _degreeLabel = "Polynomial degree";
    [ObservableProperty] private bool _showFfcNn;
    [ObservableProperty] private bool _showFfcIter;
    [ObservableProperty] private string _ccSummary = "Linear · degree 2";
    [ObservableProperty] private string _processSummary = "FFC off · GC 5 · WB · CC Root polynomial";
}
