using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ColorCorrect.App;
using ColorCorrect.App.Views;

[assembly: AvaloniaTestApplication(typeof(ColorCorrect.App.Headless.Tests.TestAppBuilder))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerTest)]

namespace ColorCorrect.App.Headless.Tests;

public static class TestAppBuilder
{
    // Exercise the same managed-picker registration as the WSL production path.
    public static AppBuilder BuildAvaloniaApp() => DesktopDialogPolicy.ConfigureManagedPicker(AppBuilder
        .Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            UseHeadlessDrawing = false,
        })
        .WithInterFont());
}

internal static class TestDataDirectory
{
    // Keep settings, sessions and preview caches out of the real user profile. Tests used to
    // write into %LOCALAPPDATA%\CCP-New and evict the user's own preview cache entries.
    [ModuleInitializer]
    internal static void Isolate()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COLORCORRECT_DATA_DIR")))
        {
            return;
        }
        var dir = Path.Combine(Path.GetTempPath(), "ccp-headless-tests", Environment.ProcessId.ToString());
        Environment.SetEnvironmentVariable("COLORCORRECT_DATA_DIR", dir);
    }
}
