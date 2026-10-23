using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ColorCorrect.App;
using ColorCorrect.App.Services;

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
