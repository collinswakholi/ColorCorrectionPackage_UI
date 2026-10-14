using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;

namespace ColorCorrect.App.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public ConfirmDialog(string title, string message, string confirmLabel = "Continue", bool danger = false) : this()
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmBtn.Content = string.IsNullOrWhiteSpace(confirmLabel) ? "Continue" : confirmLabel;
        if (danger)
        {
            object? theme = null;
            var found = TryGetResource("DangerButton", ActualThemeVariant, out theme)
                        || (Application.Current?.TryGetResource("DangerButton", ActualThemeVariant, out theme) ?? false);
            if (found && theme is ControlTheme dangerTheme)
            {
                ConfirmBtn.Theme = dangerTheme;
            }
            else
            {
                ConfirmBtn.Foreground = Brushes.White;
                ConfirmBtn.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            }
        }

        AutomationProperties.SetName(ConfirmBtn, ConfirmBtn.Content?.ToString() ?? "Continue");
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(false);
        }
    }
}
