using System.Reflection;
using Avalonia.Headless.XUnit;
using ColorCorrect.App.ViewModels;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class SettingsPersistenceTests
{
    [AvaloniaFact]
    public async Task OlderSettingsSnapshotNeverOverwritesANewerOne()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccp-settings-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("COLORCORRECT_DATA_DIR");
        Environment.SetEnvironmentVariable("COLORCORRECT_DATA_DIR", dir);
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var build = typeof(MainWindowViewModel).GetMethod("BuildSessionState", flags)!;
            var write = typeof(MainWindowViewModel).GetMethod("WriteSettings", flags)!;
            var state = (SessionState)build.Invoke(vm, [false])!;
            var path = SessionStore.SettingsPath();

            write.Invoke(vm, [state, 2L]);
            Assert.True(File.Exists(path));
            File.Delete(path);

            write.Invoke(vm, [state, 1L]); // a stale background snapshot arriving late
            Assert.False(File.Exists(path));

            write.Invoke(vm, [state, 3L]);
            Assert.True(File.Exists(path));
        }
        finally
        {
            await vm.DisposeAsync();
            Environment.SetEnvironmentVariable("COLORCORRECT_DATA_DIR", previous);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
