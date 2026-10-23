using Avalonia;
using ColorCorrect.App.Services;
using System;

namespace ColorCorrect.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
        // WSLg's in-process GTK picker can terminate the process without a
        // managed exception. Keep ordinary desktop platforms' native pickers.
        return OperatingSystem.IsLinux() && DesktopDialogPolicy.RequiresManagedPicker()
            ? DesktopDialogPolicy.ConfigureManagedPicker(builder)
            : builder;
    }
}
