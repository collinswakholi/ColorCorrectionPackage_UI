using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using ColorCorrect.Engine;

namespace ColorCorrect.App.Services;

/// <summary>
/// Last-resort handling for exceptions nothing else caught. A failed command used to
/// terminate the app (and lose the session); now it is logged to diagnostics/engine.log,
/// shown as an error toast, and the app keeps running.
/// </summary>
public static class UnexpectedErrors
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Record("UI", e.Exception);
            ToastService.ShowError($"Something went wrong: {e.Exception.Message} The app is still running; details are in the diagnostics log.");
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Record("Background task", e.Exception);
            e.SetObserved();
        };
    }

    internal static void Record(string where, Exception error)
    {
        try
        {
            var folder = Path.Combine(SessionStore.AppDataDir(), "diagnostics");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "engine.log"), $"{DateTime.Now:O} Unexpected error ({where}): {error}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Diagnostics must never cause a second failure.
        }
    }
}
