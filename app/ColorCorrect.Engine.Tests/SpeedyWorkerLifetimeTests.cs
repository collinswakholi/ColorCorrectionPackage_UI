using System.Diagnostics;
using System.Reflection;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.Engine.Tests;

public sealed class SpeedyWorkerLifetimeTests
{
    [Fact]
    public async Task DisposalCancelsAnUnansweredRequestAndQueuedRestartAndReapsTheOwnedProcess()
    {
        using var child = StartUnresponsiveWorker();
        using var observer = Process.GetProcessById(child.Id);
        var worker = new SpeedyWorker(child.StartInfo.FileName, Path.GetTempPath());
        typeof(SpeedyWorker).GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(worker, child);
        var requestRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Progress += _ => requestRead.TrySetResult();
        try
        {
            var active = worker.SendAsync(new() { ["cmd"] = "stage_preview" });
            await requestRead.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var queued = worker.RestartAsync();
            Assert.False(queued.IsCompleted);

            var elapsed = Stopwatch.StartNew();
            var first = worker.DisposeAsync().AsTask();
            var repeated = worker.DisposeAsync().AsTask();
            Assert.Same(first, repeated);
            await Task.WhenAll(first, repeated).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(observer.HasExited, "The owned worker must exit before disposal completes.");
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => worker.StartAsync());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => worker.RestartAsync());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => worker.RequireCapabilityAsync("reference_profiles_v1"));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => worker.SendAsync(new() { ["cmd"] = "ping" }));
        }
        finally
        {
            try { await worker.DisposeAsync(); }
            finally
            {
                // A failed assertion must not leave the deliberately sleeping fixture behind.
                try { if (!observer.HasExited) observer.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
        }
    }

    [Fact]
    public async Task NeverStartedWorkerCanBeDisposedRepeatedlyAndCannotRestart()
    {
        var worker = new SpeedyWorker();
        var first = worker.DisposeAsync().AsTask();
        var repeated = worker.DisposeAsync().AsTask();
        Assert.Same(first, repeated);
        await first;
        await worker.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => worker.StartAsync());
    }

    private static Process StartUnresponsiveWorker()
    {
        var info = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
                : "/bin/sh",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        const string progress = "{\"type\":\"progress\",\"message\":\"request-read\"}";
        if (OperatingSystem.IsWindows())
        {
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add("$null = [Console]::ReadLine(); [Console]::WriteLine('" + progress
                + "'); [Console]::Out.Flush(); Start-Sleep -Seconds 300");
        }
        else
        {
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("IFS= read -r request; printf '%s\\n' '" + progress + "'; sleep 300");
        }
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start the owned shutdown fixture.");
    }
}
