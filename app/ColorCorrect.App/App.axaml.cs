using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ColorCorrect.App.Services;
using ColorCorrect.App.ViewModels;
using ColorCorrect.App.Views;

namespace ColorCorrect.App;

public partial class App : Application
{
    private const int MinSplashMs = 3000;
    private const int MaxSplashMs = 45000;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel();
            var splash = new SplashWindow();
            var startup = new CancellationTokenSource();
            var shutdown = new WindowShutdownGuard(splash, async () =>
            {
                try { startup.Cancel(); }
                finally { await vm.DisposeAsync(); }
            });
            desktop.MainWindow = splash;
            splash.Show();
            _ = RunSplashThenMainAsync(desktop, vm, splash, shutdown, startup);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task RunSplashThenMainAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindowViewModel vm,
        SplashWindow splash,
        WindowShutdownGuard shutdown,
        CancellationTokenSource startup)
    {
        try
        {
            vm.Start();

            var minDelay = Task.Delay(MinSplashMs, startup.Token);
            var workerWait = WaitForWorkerAsync(vm, splash, TimeSpan.FromMilliseconds(MaxSplashMs), startup.Token);
            await Task.WhenAll(minDelay, workerWait).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (startup.IsCancellationRequested || shutdown.IsClosing) return;
                var window = new MainWindow { DataContext = vm };
                vm.Dialogs = new WindowDialogs(window);
                desktop.MainWindow = window;
                window.Show();
                shutdown.Detach();
                splash.Close();
            });
        }
        catch (OperationCanceledException) when (startup.IsCancellationRequested) { }
        finally { startup.Dispose(); }
    }

    private static async Task WaitForWorkerAsync(MainWindowViewModel vm, SplashWindow splash, TimeSpan timeout, CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellation.ThrowIfCancellationRequested();
            var finished = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                finished = vm.WorkerReady || vm.WorkerStartupComplete;
                splash.SetStatus(vm.WorkerReady
                    ? "Engine ready"
                    : vm.WorkerStartupComplete
                        ? "Engine unavailable — opening recovery controls"
                        : string.IsNullOrWhiteSpace(vm.CurrentStage) ? "Starting color engine…" : vm.CurrentStage);
            });
            if (finished)
            {
                return;
            }

            await Task.Delay(50, cancellation).ConfigureAwait(false);
        }

        await Dispatcher.UIThread.InvokeAsync(() => splash.SetStatus("Engine is still warming — opening workspace"));
    }
}
