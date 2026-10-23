using Avalonia.Headless.XUnit;
using ColorCorrect.App.Services;
using ColorCorrect.App.ViewModels;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class SessionIoTests
{
    [AvaloniaFact]
    public async Task SessionSaveCapturesSettingsBeforeBackgroundIoAndRestoresCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), "ccp-session-io-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "saved.json");
        var vm = new MainWindowViewModel(persistenceEnabled: false) { Busy = false, DoGc = true, Dialogs = new SessionDialogs(path) };
        try
        {
            var pending = vm.SaveSessionAsCommand.ExecuteAsync(null);
            vm.DoGc = false;
            await pending;
            Assert.True(File.Exists(path));
            var (saved, error) = SessionStore.TryLoadFrom(path);
            Assert.Null(error);
            Assert.True(saved.Options.DoGc);
            Assert.False(vm.DoGc);
            Assert.False(vm.IsSaving);
            Assert.StartsWith("Session saved", vm.Status);
            Assert.True(vm.LoadSessionCommand.CanExecute(null));

            vm.Busy = true;
            Assert.False(vm.SaveSessionAsCommand.CanExecute(null));
            Assert.False(vm.LoadSessionCommand.CanExecute(null));
            vm.Busy = false;
            vm.IsSaving = true;
            Assert.False(vm.SaveSessionAsCommand.CanExecute(null));
            Assert.False(vm.LoadSessionCommand.CanExecute(null));
            vm.IsSaving = false;
            Assert.True(vm.SaveSessionAsCommand.CanExecute(null));
        }
        finally { await vm.DisposeAsync(); Directory.Delete(root, true); }
    }

    private sealed class SessionDialogs(string path) : IAppDialogs
    {
        public Task<string?> SaveJsonAsync(string title, string suggestedName) => Task.FromResult<string?>(path);
        public Task<IReadOnlyList<string>> OpenImagesAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> OpenFolderImagesAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> OpenJsonAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> SaveImageAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> SavePlotAsync(string title, string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> OpenWhiteAsync() => Task.FromResult<string?>(null);
        public Task<string?> SaveCubeAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message, string? confirmLabel = null, bool danger = false) => Task.FromResult(true);
        public Task SetClipboardAsync(string text) => Task.CompletedTask;
    }
}
