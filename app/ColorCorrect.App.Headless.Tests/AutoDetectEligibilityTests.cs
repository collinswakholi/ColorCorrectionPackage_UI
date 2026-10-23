using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ColorCorrect.App.Services;
using ColorCorrect.App.ViewModels;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class AutoDetectEligibilityTests
{
    [AvaloniaTheory]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(false, false, 1)]
    public async Task BrowsingAnUndetectedImageDoesNotQueueDetectionBehindAnActiveOperation(
        bool busy, bool saving, int expectedDetections)
    {
        var root = Path.Combine(Path.GetTempPath(), "ccp-autodetect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "models", "opt2"));
        Directory.CreateDirectory(Path.Combine(root, "refs"));
        File.WriteAllText(Path.Combine(root, "refs", "neutrals.json"), "{}");
        var imagePath = Path.Combine(root, "undetected.png");
        using (var image = PreviewRgb.FromRgb(2, 2, new byte[12])) image.Save(imagePath);
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var workerField = typeof(MainWindowViewModel).GetField("_worker", flags)!;
        var oldWorker = (SpeedyWorker)workerField.GetValue(vm)!;
        typeof(MainWindowViewModel).GetMethod("UnhookWorkerProgress", flags)!.Invoke(vm, null);
        // A queued detection can fail normally, but can never launch a real engine/GPU.
        workerField.SetValue(vm, new SpeedyWorker(Path.Combine(root, "nonexistent-engine"), root));
        typeof(MainWindowViewModel).GetMethod("HookWorkerProgress", flags)!.Invoke(vm, null);
        await oldWorker.DisposeAsync();
        var gate = (SemaphoreSlim)typeof(MainWindowViewModel).GetField("_run", flags)!.GetValue(vm)!;
        var held = false;
        try
        {
            await gate.WaitAsync(); held = true;
            vm.WorkerReady = true;
            vm.Busy = busy;
            vm.IsSaving = saving;
            var item = new SessionItemViewModel(imagePath);
            vm.Session.Add(item);
            vm.HandleSessionClick(item, toggle: false, range: false);
            Assert.Same(item, vm.Active);
            Assert.Equal(0, item.DetectGeneration);

            // Simulate the current batch/save completing. A semaphore barrier queues
            // after any erroneous automatic detection, so this observes its eventual
            // execution deterministically without waiting an arbitrary quiet interval.
            vm.Busy = false;
            vm.IsSaving = false;
            var afterQueuedWork = gate.WaitAsync();
            gate.Release(); held = false;
            await afterQueuedWork.WaitAsync(TimeSpan.FromSeconds(5)); held = true;
            Assert.Equal(expectedDetections, item.DetectGeneration);
        }
        finally
        {
            if (held) gate.Release();
            await vm.DisposeAsync();
            Dispatcher.UIThread.RunJobs();
            Directory.Delete(root, true);
        }
    }
}
