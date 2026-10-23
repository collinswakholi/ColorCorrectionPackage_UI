using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ColorCorrect.App.Views;

public partial class CcSettingsDialog : Window
{
    public CcSettingsDialog()
    {
        InitializeComponent();
        AddHandler(InputElement.KeyDownEvent, OnKeyDown,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        DialogLayout.FitToOwner(this);
        Dispatcher.UIThread.Post(
            () => ModelSelect.Focus(NavigationMethod.Tab),
            DispatcherPriority.Input);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private async void References_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel { Active.Handshake: { Ok: true } } vm || vm.Busy || vm.IsSaving)
        {
            if (DataContext is ViewModels.MainWindowViewModel current) current.Status = "Detect a chart image before editing its references.";
            return;
        }
        await new ReferenceEditorDialog(vm).ShowDialog(this);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
