using CommunityToolkit.Mvvm.ComponentModel;

namespace ColorCorrect.App.ViewModels;

/// <summary>Outputs and models: output folder and format, which results to save and which can be saved, save progress, the report flag, the loaded model and its status, and the batch preview option.</summary>
public partial class OutputViewModel : ViewModelBase
{
    [ObservableProperty] private string _outputDir = "";
    [ObservableProperty] private NamedValue _selectedOutputFormat = new("png", "PNG");
    [ObservableProperty] private bool _saveCorrected = true;
    [ObservableProperty] private bool _saveFfc = true;
    [ObservableProperty] private bool _saveGc = true;
    [ObservableProperty] private bool _saveWb = true;
    [ObservableProperty] private bool _saveCc = true;
    [ObservableProperty] private bool _saveReport = true;
    [ObservableProperty] private bool _canSaveCorrected;
    [ObservableProperty] private bool _canSaveFfc;
    [ObservableProperty] private bool _canSaveGc;
    [ObservableProperty] private bool _canSaveWb;
    [ObservableProperty] private bool _canSaveCc;
    [ObservableProperty] private bool _canSaveReport;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private bool _showSaveProgress;
    [ObservableProperty] private double _saveProgress;
    [ObservableProperty] private string _saveProgressLabel = "";
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private string _loadedModelPath = "";
    [ObservableProperty] private bool _useLoadedModel;
    [ObservableProperty] private string _modelLabel = "No model loaded";
    [ObservableProperty] private string _modelIoStatus = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSavedModelSummary))] private string _savedModelSummary = "";
    [ObservableProperty] private bool _batchPreviewResults = true;

    public bool HasSavedModelSummary => !string.IsNullOrWhiteSpace(SavedModelSummary);
}
