using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ColorCorrect.App.ViewModels;
using ColorCorrect.App.Views;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class WindowShutdownTests
{
    [AvaloniaFact]
    public async Task WindowStaysAliveUntilItsRealIdleWorkerExitsAndCloseIsIdempotent()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        var worker = Field<SpeedyWorker>(vm, "_worker");
        using var observer = AttachChild(worker, blockedRequest: false);
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 640 };
        bool? workerExitedWhenWindowClosed = null;
        window.Closed += (_, _) => workerExitedWhenWindowClosed = observer.HasExited;
        window.Show();
        try
        {
            window.ExitLink.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Close();
            var first = vm.DisposeAsync().AsTask();
            Assert.Same(first, vm.DisposeAsync().AsTask());
            Assert.True(window.IsVisible || observer.HasExited);
            await Until(() => !window.IsVisible);
            await first;
            Assert.True(workerExitedWhenWindowClosed);
            Assert.True(observer.HasExited);
            Assert.Equal(0, observer.ExitCode);
        }
        finally
        {
            StopChild(observer);
            await vm.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(12));
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task CloseCancelsAnUncancellablePreviewAndDrainsItsUiFinallyBeforeDisposingState()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        var worker = Field<SpeedyWorker>(vm, "_worker");
        var run = Field<SemaphoreSlim>(vm, "_run");
        using var observer = AttachChild(worker, blockedRequest: true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Progress += _ => started.TrySetResult();
        var cancelled = false;
        var uiFinallyRan = false;
        var output = Path.Combine(Path.GetTempPath(), $"ccp-durable-close-{Guid.NewGuid():N}.jpg");
        var bytes = new byte[] { 17, 29, 41, 53 };
        File.WriteAllBytes(output, bytes);
        vm.Session.Add(new SessionItemViewModel("captured-source.jpg") { IsCorrected = true, CorrectedPath = output });
        await run.WaitAsync();
        var operation = Task.Run(async () =>
        {
            try
            {
                // Deferred previews can have no current-job cancellation token.
                await worker.SendAsync(new() { ["cmd"] = "preview_stage" }, TimeSpan.FromMinutes(10));
            }
            catch (OperationCanceledException) { cancelled = true; }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => { uiFinallyRan = true; run.Release(); });
            }
        });
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 640 };
        bool? workerExitedWhenWindowClosed = null;
        window.Closed += (_, _) => workerExitedWhenWindowClosed = observer.HasExited;
        window.Show();
        try
        {
            await Until(() => started.Task.IsCompleted);
            var timer = Stopwatch.StartNew();
            window.Close();
            window.Close();
            Assert.True(window.IsVisible);
            Assert.False(window.IsEnabled);
            Assert.Same(vm.DisposeAsync().AsTask(), vm.DisposeAsync().AsTask());
            await Until(() => !window.IsVisible);
            await operation;
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(12));
            Assert.True(cancelled);
            Assert.True(uiFinallyRan);
            Assert.True(workerExitedWhenWindowClosed);
            Assert.True(observer.HasExited);
            Assert.Equal(bytes, File.ReadAllBytes(output));
        }
        finally
        {
            StopChild(observer);
            await operation.WaitAsync(TimeSpan.FromSeconds(12));
            await vm.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(12));
            window.Close();
            Dispatcher.UIThread.RunJobs();
            File.Delete(output);
        }
    }

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;

    private static Process AttachChild(SpeedyWorker worker, bool blockedRequest)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
            : "/bin/sh")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(blockedRequest
                ? "$null = [Console]::ReadLine(); [Console]::WriteLine('{\"type\":\"progress\",\"message\":\"request started\"}'); [Console]::Out.Flush(); Start-Sleep -Seconds 60"
                : "$null = [Console]::ReadLine(); exit 0");
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(blockedRequest
                ? "IFS= read -r line; printf '%s\\n' '{\"type\":\"progress\",\"message\":\"request started\"}'; sleep 60"
                : "IFS= read -r line; exit 0");
        }
        var process = Process.Start(start)!;
        var observer = Process.GetProcessById(process.Id);
        // Retain its OS handle while alive: reopening an already exited PID can
        // report absence but cannot recover the cooperative shutdown exit code.
        _ = observer.SafeHandle;
        typeof(SpeedyWorker).GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(worker, process);
        return observer;
    }

    private static void StopChild(Process child)
    {
        if (child.HasExited) return;
        child.Kill(entireProcessTree: true);
        child.WaitForExit(3000);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition());
    }
}
