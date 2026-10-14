using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ColorCorrect.App;

[assembly: AvaloniaTestApplication(typeof(ColorCorrect.App.Headless.Tests.TestAppBuilder))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerTest)]

namespace ColorCorrect.App.Headless.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            UseHeadlessDrawing = false,
        })
        .WithInterFont();
}
