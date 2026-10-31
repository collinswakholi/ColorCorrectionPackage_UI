using System.Diagnostics;
using System.Reflection;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ColorCorrect.App.ViewModels;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class WorkerRestartProgressTests
{
    [AvaloniaTheory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task RestartClearsWarmupProgressAndPreservesCompletedResults(string mode)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ccp-warmup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        Process? observer = null;
        Task? restart = null;
        try
        {
            File.WriteAllText(Path.Combine(folder, "mode"), mode);
            var executable = await CreateWorkerFixture(folder);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var field = typeof(MainWindowViewModel).GetField("_worker", flags)!;
            var original = (SpeedyWorker)field.GetValue(vm)!;
            typeof(MainWindowViewModel).GetMethod("UnhookWorkerProgress", flags)!.Invoke(vm, null);
            var worker = new SpeedyWorker(executable, folder);
            field.SetValue(vm, worker);
            typeof(MainWindowViewModel).GetMethod("HookWorkerProgress", flags)!.Invoke(vm, null);
            await original.DisposeAsync();

            var model = Path.Combine(folder, "completed-model.json");
            var output = Path.Combine(folder, "completed-result.jpg");
            File.WriteAllText(model, "previous successful model");
            File.WriteAllText(output, "previous successful result");
            var item = new SessionItemViewModel(Path.Combine(folder, "missing-source.jpg"))
            {
                IsCorrected = true, ModelPath = model, CorrectedPath = output,
            };
            vm.Session.Add(item);
            vm.SelectedAccelPolicy = vm.AccelPolicies.Single(policy => policy.Value == "cpu-inference");

            restart = vm.ForceCpuRestartCommand.ExecuteAsync(null);
            await Until(() => vm.CurrentStage == "Warming Opt2 1280");
            Assert.True(vm.Busy);
            Assert.True(vm.JobProgress > 0);
            observer = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(folder, "pid"))));
            _ = observer.SafeHandle;

            if (mode == "cancel") vm.CancelBatchCommand.Execute(null);
            else File.WriteAllText(Path.Combine(folder, "continue"), "");
            await restart.WaitAsync(TimeSpan.FromSeconds(15));
            Dispatcher.UIThread.RunJobs();

            Assert.False(vm.Busy);
            Assert.Empty(vm.CurrentStage);
            Assert.False(vm.ShowJobProgress);
            Assert.Equal(0, vm.JobProgress);
            Assert.Equal(mode == "success", vm.WorkerReady);
            Assert.True(vm.WorkerStartupComplete);
            if (mode == "success") Assert.Contains("Warmed", vm.Status);
            else Assert.NotEmpty(vm.WorkerStartupError);
            Assert.True(item.IsCorrected);
            Assert.Equal(model, item.ModelPath);
            Assert.Equal(output, item.CorrectedPath);
            Assert.Equal("previous successful model", File.ReadAllText(model));
            Assert.Equal("previous successful result", File.ReadAllText(output));

            await vm.DisposeAsync();
            Assert.True(observer.HasExited);
            Assert.Equal(0, observer.ExitCode);
        }
        finally
        {
            File.WriteAllText(Path.Combine(folder, "continue"), "");
            await vm.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(12));
            if (restart is not null) await restart.WaitAsync(TimeSpan.FromSeconds(12));
            observer?.Dispose();
            Dispatcher.UIThread.RunJobs();
            Directory.Delete(folder, true);
        }
    }

    private static async Task<string> CreateWorkerFixture(string folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            var script = Path.Combine(folder, "worker");
            await File.WriteAllTextAsync(script, """
                #!/bin/sh
                printf '%s\n' "$$" > pid
                printf '%s\n' '{"ok":true,"capabilities":["cpu_inference_v1"]}'
                IFS= read -r line
                printf '%s\n' '{"type":"progress","message":"Warming Opt2 1280"}'
                mode=$(cat mode)
                if [ "$mode" = cancel ]; then IFS= read -r line; exit 0; fi
                while [ ! -f continue ]; do sleep 0.01; done
                if [ "$mode" = failure ]; then
                    printf '%s\n' '{broken'
                    IFS= read -r line
                    exit 0
                fi
                printf '%s\n' '{"ok":true,"warmed":true,"device":"cpu-inference"}'
                while IFS= read -r line; do
                    case "$line" in
                        *shutdown*) exit 0 ;;
                        *) printf '%s\n' '{"ok":true}' ;;
                    esac
                done
                """, new UTF8Encoding(false));
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return script;
        }

        var source = Path.Combine(folder, "worker.cs");
        await File.WriteAllTextAsync(source, """
            using System;
            using System.IO;
            using System.Text;
            using System.Threading;
            class Worker {
                static void Main() {
                    Console.OutputEncoding = new UTF8Encoding(false);
                    File.WriteAllText("pid", System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
                    Console.WriteLine("{\"ok\":true,\"capabilities\":[\"cpu_inference_v1\"]}");
                    Console.ReadLine();
                    Console.WriteLine("{\"type\":\"progress\",\"message\":\"Warming Opt2 1280\"}");
                    string mode = File.ReadAllText("mode");
                    if (mode == "cancel") { Console.ReadLine(); return; }
                    while (!File.Exists("continue")) Thread.Sleep(10);
                    if (mode == "failure") { Console.WriteLine("{broken"); Console.ReadLine(); return; }
                    Console.WriteLine("{\"ok\":true,\"warmed\":true,\"device\":\"cpu-inference\"}");
                    string line;
                    while ((line = Console.ReadLine()) != null) {
                        if (line.Contains("shutdown")) return;
                        Console.WriteLine("{\"ok\":true}");
                    }
                }
            }
            """, new UTF8Encoding(false));
        var compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
        Assert.True(File.Exists(compiler), "The Windows native protocol fixture requires the built-in .NET Framework compiler.");
        var executable = Path.Combine(folder, "worker.exe");
        var start = new ProcessStartInfo(compiler)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("/nologo");
        start.ArgumentList.Add("/target:exe");
        start.ArgumentList.Add("/out:" + executable);
        start.ArgumentList.Add(source);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, await stdout + await stderr);
        return executable;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition());
    }
}
