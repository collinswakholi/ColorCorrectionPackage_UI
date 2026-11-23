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
        // A queued detection can fail normally, but can never launch a real engine/GPU.
        await TestWorkers.UseNonLaunchingWorkerAsync(vm, root);
        var gate = (SemaphoreSlim)typeof(MainWindowViewModel).GetField("_run", flags)!.GetValue(vm)!;
        var held = false;
        Task? afterQueuedWork = null;
        try
        {
            Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(12))); held = true;
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
            afterQueuedWork = gate.WaitAsync();
            gate.Release(); held = false;
            await afterQueuedWork.WaitAsync(TimeSpan.FromSeconds(12)); held = true;
            Assert.Equal(expectedDetections, item.DetectGeneration);
        }
        finally
        {
            if (held) gate.Release();
            // If the barrier timed out it still owns the gate once it is granted.
            // Release it then; otherwise DisposeAsync waits for the gate forever
            // and the test host hangs instead of reporting the failure.
            else if (afterQueuedWork is { IsCompleted: false })
                _ = afterQueuedWork.ContinueWith(_ => gate.Release(), TaskScheduler.Default);
            await vm.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(12));
            Dispatcher.UIThread.RunJobs();
            await TestWorkers.DeleteDirectoryAsync(root);
        }
    }
}
