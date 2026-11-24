using ColorCorrect.App.Services;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

/// <summary>Ordering, debouncing and failure reporting of the settings save scheduler.</summary>
public sealed class SettingsPersistenceTests
{
    private static Task RunInline(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    [Fact]
    public void OlderSettingsSnapshotNeverOverwritesANewerOne()
    {
        var writes = new List<string>();
        var saver = new SettingsSaveScheduler(() => null, RunInline,
            state => { writes.Add(state.ActivePath); return true; }, () => { });

        saver.WriteIfNewest(new SessionState { ActivePath = "gen2" }, 2);
        saver.WriteIfNewest(new SessionState { ActivePath = "gen1" }, 1); // a stale background snapshot arriving late
        saver.WriteIfNewest(new SessionState { ActivePath = "gen3" }, 3);

        Assert.Equal(["gen2", "gen3"], writes);
    }

    [Fact]
    public async Task ScheduledSavesAreCoalescedIntoOneWriteOfTheLatestState()
    {
        var current = "a";
        var writes = new List<string>();
        var saver = new SettingsSaveScheduler(() => new SessionState { ActivePath = current }, RunInline,
            state => { lock (writes) writes.Add(state.ActivePath); return true; }, () => { })
        { Delay = TimeSpan.FromMilliseconds(80) };

        saver.Schedule();
        current = "b";
        saver.Schedule();
        current = "c";
        saver.Schedule();
        await Task.Delay(400);

        lock (writes) Assert.Equal(["c"], writes);
    }

    [Fact]
    public async Task CancelPendingDropsAScheduledSaveAndSaveNowWritesImmediately()
    {
        var writes = 0;
        var saver = new SettingsSaveScheduler(() => new SessionState(), RunInline,
            _ => { Interlocked.Increment(ref writes); return true; }, () => { })
        { Delay = TimeSpan.FromMilliseconds(80) };

        saver.Schedule();
        saver.CancelPending();
        await Task.Delay(300);
        Assert.Equal(0, writes);

        saver.SaveNow();
        Assert.Equal(1, writes);
    }

    [Fact]
    public void NullSnapshotSkipsSavingAndFailureIsReportedOnce()
    {
        var failures = 0;
        var writes = 0;
        var allow = false;
        var saver = new SettingsSaveScheduler(() => allow ? new SessionState() : null, RunInline,
            _ => { writes++; return false; }, () => failures++);

        saver.SaveNow(); // saving not allowed (e.g. while restoring)
        Assert.Equal(0, writes);

        allow = true;
        saver.SaveNow();
        saver.SaveNow();
        Assert.Equal(2, writes);
        Assert.Equal(1, failures);
    }
}
