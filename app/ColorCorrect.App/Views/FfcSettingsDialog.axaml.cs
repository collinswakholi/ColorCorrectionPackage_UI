using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ColorCorrect.App.Views;

public partial class FfcSettingsDialog : Window
{
    public FfcSettingsDialog()
    {
        InitializeComponent();
        AddHandler(InputElement.KeyDownEvent, OnKeyDown,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        DialogLayout.FitToOwner(this);
        Dispatcher.UIThread.Post(
            () => FfcMethodSelect.Focus(NavigationMethod.Tab),
            DispatcherPriority.Input);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
