using CommunityToolkit.Mvvm.ComponentModel;

namespace ColorCorrect.App.ViewModels;

/// <summary>Window layout preferences: light/dark theme with its label and hint, and which sidebar sections are expanded.</summary>
public partial class LayoutViewModel : ViewModelBase
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ThemeLabel), nameof(ThemeHint))] private bool _isLightTheme;
    [ObservableProperty] private bool _libraryExpanded;
    [ObservableProperty] private bool _processExpanded = true;
    [ObservableProperty] private bool _modelExpanded;

    public string ThemeLabel => IsLightTheme ? "Dark" : "Light";
    public string ThemeHint => IsLightTheme ? "Switch to dark theme" : "Switch to light theme";
}
