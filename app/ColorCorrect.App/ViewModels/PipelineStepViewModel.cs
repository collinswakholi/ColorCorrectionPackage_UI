using CommunityToolkit.Mvvm.ComponentModel;

namespace ColorCorrect.App.ViewModels;

public sealed partial class PipelineStepViewModel : ObservableObject
{
    public PipelineStepViewModel(string key, string label, bool enabled)
    {
        Key = key;
        Label = label;
        IsEnabled = enabled;
    }

    public string Key { get; }
    public string Label { get; }

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isSkipped;

    public void Reset()
    {
        IsActive = false;
        IsDone = false;
        IsSkipped = false;
    }
}
