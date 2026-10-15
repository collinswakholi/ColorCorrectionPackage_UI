using System.Reflection;
using Avalonia.Controls;

namespace ColorCorrect.App.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "0.1.0" : $"Version {version.Major}.{version.Minor}.{version.Build}";
    }

    public void SetStatus(string message)
    {
        StatusText.Text = message;
    }
}
