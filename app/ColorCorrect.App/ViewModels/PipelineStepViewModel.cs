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
    public string StatusText => !IsEnabled ? "Not used" : IsSkipped ? "Skipped" : IsActive ? "Running" : IsDone ? "Complete" : "Pending";

    [NotifyPropertyChangedFor(nameof(StatusText))]
    [ObservableProperty] private bool _isEnabled;
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [ObservableProperty] private bool _isActive;
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [ObservableProperty] private bool _isDone;
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [ObservableProperty] private bool _isSkipped;

    public void Reset()
    {
        IsActive = false;
        IsDone = false;
        IsSkipped = false;
    }
}
