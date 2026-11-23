using System.Reflection;
using ColorCorrect.App.ViewModels;
using ColorCorrect.Engine;

namespace ColorCorrect.App.Headless.Tests;

internal static class TestWorkers
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>
    /// Replace the view model's worker with one whose executable does not exist,
    /// so automatic detection fails fast instead of launching the real engine
    /// (which a test that never disposes the view model would leak for the rest
    /// of the run). Disposes the original, never-started worker.
    /// </summary>
    public static async Task UseNonLaunchingWorkerAsync(MainWindowViewModel vm, string root)
    {
        var field = typeof(MainWindowViewModel).GetField("_worker", Private)!;
        var original = (SpeedyWorker)field.GetValue(vm)!;
        typeof(MainWindowViewModel).GetMethod("UnhookWorkerProgress", Private)!.Invoke(vm, null);
        field.SetValue(vm, new SpeedyWorker(Path.Combine(root, "nonexistent-engine"), root));
        typeof(MainWindowViewModel).GetMethod("HookWorkerProgress", Private)!.Invoke(vm, null);
        await original.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(12));
    }

    /// <summary>
    /// Delete a test folder, retrying while a background preview or thumbnail decode
    /// that outlived the view model still has a file open.
    /// </summary>
    public static async Task DeleteDirectoryAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 50)
            {
                await Task.Delay(100);
            }
        }
    }
}
