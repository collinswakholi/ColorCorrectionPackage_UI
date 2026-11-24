using System;
using System.Threading;
using System.Threading.Tasks;
using ColorCorrect.Engine;

namespace ColorCorrect.App.Services;

/// <summary>
/// Debounced, ordered settings persistence. <see cref="Schedule"/> waits for a quiet period,
/// takes a snapshot on the UI thread and writes it in the background. Writes are serialised
/// and tagged with a generation so an older snapshot never overwrites a newer one (including
/// the synchronous <see cref="SaveNow"/> used on flush and close). The first failed write is
/// reported once through <c>onFirstFailure</c>.
/// </summary>
internal sealed class SettingsSaveScheduler
{
    private readonly Func<SessionState?> _snapshot;
    private readonly Func<Action, Task> _runOnUi;
    private readonly Func<SessionState, bool> _write;
    private readonly Action _onFirstFailure;
    private readonly object _writeLock = new();
    private CancellationTokenSource? _pending;
    private long _snapshotGeneration;
    private long _writtenGeneration;
    private bool _warned;

    /// <param name="snapshot">Builds the state to save on the UI thread, or returns null when saving is not allowed now.</param>
    /// <param name="runOnUi">Runs an action on the UI thread.</param>
    /// <param name="write">Writes the state; returns false when it could not be saved.</param>
    /// <param name="onFirstFailure">Called once, from a background thread, after the first failed write.</param>
    public SettingsSaveScheduler(Func<SessionState?> snapshot, Func<Action, Task> runOnUi,
        Func<SessionState, bool> write, Action onFirstFailure)
    {
        _snapshot = snapshot;
        _runOnUi = runOnUi;
        _write = write;
        _onFirstFailure = onFirstFailure;
    }

    /// <summary>Quiet period before a scheduled save runs.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Schedule a save after <see cref="Delay"/>; a newer request replaces a pending one.</summary>
    public void Schedule()
    {
        var current = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _pending, current);
        previous?.Cancel();
        previous?.Dispose();
        var token = current.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Delay, token).ConfigureAwait(false);
                (SessionState State, long Generation)? taken = null;
                await _runOnUi(() => taken = Take()).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (taken is { } t) WriteIfNewest(t.State, t.Generation);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        });
    }

    /// <summary>Cancel a pending scheduled save (a write already in progress still completes).</summary>
    public void CancelPending() => _pending?.Cancel();

    /// <summary>Snapshot and write now, on the calling (UI) thread.</summary>
    public void SaveNow()
    {
        if (Take() is { } taken) WriteIfNewest(taken.State, taken.Generation);
    }

    private (SessionState State, long Generation)? Take() =>
        _snapshot() is { } state ? (state, Interlocked.Increment(ref _snapshotGeneration)) : null;

    /// <summary>Write unless a newer generation was already written; report the first failure.</summary>
    internal void WriteIfNewest(SessionState state, long generation)
    {
        bool saved;
        lock (_writeLock)
        {
            if (generation < _writtenGeneration) return;
            saved = _write(state);
            if (saved) _writtenGeneration = generation;
        }

        if (!saved && !_warned)
        {
            _warned = true;
            _onFirstFailure();
        }
    }
}
