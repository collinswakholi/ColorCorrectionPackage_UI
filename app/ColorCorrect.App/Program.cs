using Avalonia;
using ColorCorrect.App.Views;
using System;

namespace ColorCorrect.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception error) when (StartupFailure.MissingLibrary(error) is { } library)
        {
            // A missing X11/GTK/ICU library used to abort with a core dump.
            return StartupFailure.Report(library);
        }
    }

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
