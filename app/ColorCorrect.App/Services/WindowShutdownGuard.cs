using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ColorCorrect.App.Services;

/// <summary>Keep the last window alive until its owned asynchronous cleanup finishes.</summary>
internal sealed class WindowShutdownGuard
{
    private readonly Window _window;
    private readonly Func<ValueTask> _cleanup;
    private Task? _completion;
    private bool _allowClose;
    private bool _closeScheduled;

    public WindowShutdownGuard(Window window, Func<ValueTask> cleanup)
    {
        _window = window;
        _cleanup = cleanup;
        window.Closing += OnClosing;
        window.Closed += (_, _) => Detach();
    }

    public bool IsClosing => _completion is not null;
    public Task Completion => _completion ?? Task.CompletedTask;

    // Startup transfers ownership to MainWindow before closing its splash.
    public void Detach() => _window.Closing -= OnClosing;

    private void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_allowClose) return;
        _completion ??= CleanupAsync();
        if (_completion.IsCompletedSuccessfully)
        {
            _allowClose = true;
            return;
        }

        args.Cancel = true;
        if (_closeScheduled) return;
        _closeScheduled = true;
        _window.IsEnabled = false;
        _ = CloseAfterCleanupAsync();
    }

    private async Task CleanupAsync()
    {
        try { await _cleanup(); }
        catch (Exception exception)
        {
            // Cleanup owns worker termination in its finally path. A settings or
            // cache failure must not escape an async event handler on shutdown.
            Trace.TraceError("Application shutdown cleanup failed: {0}", exception);
        }
    }

    private async Task CloseAfterCleanupAsync()
    {
        await Completion.ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _allowClose = true;
            _window.Close();
        });
    }
}
