using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ColorCorrect.App.Services;
using ColorCorrect.App.ViewModels;
using ColorCorrect.App.Views;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class KeyboardWorkflowTests
{
    [AvaloniaFact]
    public void RoutedOpenAndSaveShortcutsChooseDistinctOperationsAndConsumeTheKeys()
    {
        var dialogs = new RecordingDialogs();
        var vm = new MainWindowViewModel(persistenceEnabled: false) { Busy = false, Dialogs = dialogs };
        // A completed row enables Save; no worker or output-file processing is needed.
        vm.Session.Add(new SessionItemViewModel("keyboard-menu-fixture.jpg") { IsCorrected = true });
        vm.PrepareSaveMenu();
        var window = new MainWindow { Width = 1200, Height = 900, DataContext = vm };
        var handled = new List<bool>();
        EventHandler<KeyEventArgs> recorder = (_, args) => handled.Add(args.Handled);
        window.AddHandler(InputElement.KeyDownEvent, recorder, RoutingStrategies.Bubble, handledEventsToo: true);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.True(window.SaveLink.IsEnabled);
            Assert.True(window.SaveLink.Focus());
            foreach (var (key, modifiers, expected) in new[]
            {
                (PhysicalKey.O, RawInputModifiers.Control, "images"),
                (PhysicalKey.O, RawInputModifiers.Control | RawInputModifiers.Shift, "folder"),
                (PhysicalKey.O, RawInputModifiers.Control | RawInputModifiers.Alt, "open:Load session"),
                (PhysicalKey.S, RawInputModifiers.Control | RawInputModifiers.Shift, "save:Save session"),
            })
            {
                var count = dialogs.Calls.Count;
                window.KeyPressQwerty(key, modifiers);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(count + 1, dialogs.Calls.Count);
                Assert.Equal(expected, dialogs.Calls[^1]);
                Assert.True(handled[^1]);
                Assert.False(window.SaveMenu.IsOpen);
            }

            var pickerCalls = dialogs.Calls.Count;
            window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.True(handled[^1]);
            Assert.True(window.SaveMenu.IsOpen);
            Assert.Equal(pickerCalls, dialogs.Calls.Count);
            window.SaveMenu.IsOpen = false;
        }
        finally
        {
            window.RemoveHandler(InputElement.KeyDownEvent, recorder);
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed class RecordingDialogs : IAppDialogs
    {
        public List<string> Calls { get; } = [];
        public Task<IReadOnlyList<string>> OpenImagesAsync()
        {
            Calls.Add("images");
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
        public Task<IReadOnlyList<string>> OpenFolderImagesAsync()
        {
            Calls.Add("folder");
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
        public Task<string?> OpenJsonAsync(string title) { Calls.Add("open:" + title); return Task.FromResult<string?>(null); }
        public Task<string?> SaveJsonAsync(string title, string suggestedName) { Calls.Add("save:" + title); return Task.FromResult<string?>(null); }
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> SaveImageAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> SavePlotAsync(string title, string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> OpenWhiteAsync() => Task.FromResult<string?>(null);
        public Task<string?> SaveCubeAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message, string? confirmLabel = null, bool danger = false) => Task.FromResult(false);
        public Task SetClipboardAsync(string text) => Task.CompletedTask;
    }
}
