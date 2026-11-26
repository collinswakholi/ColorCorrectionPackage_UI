using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Headless;

namespace ColorCorrect.App.Headless.Tests;

/// <summary>
/// Turns a dead Avalonia test loop into an immediate, explained failure.
/// Avalonia.Headless 12.0.2 runs every test on one loop thread and only catches
/// cancellation there. Any other exception that escapes its per-test application
/// setup or teardown ends the thread silently; every later test then waits forever
/// and the run is reported as a hang in whichever test happened to be next.
/// The known cause was app code reading <c>Dispatcher.UIThread</c> from a background
/// thread between two tests, which made that thread the "UI thread" for the next one
/// (see <see cref="BackgroundToUiTests"/>). This watchdog covers any other cause: it
/// writes the exception to stderr and to the file named by CCP_TEST_LOOP_LOG, then
/// ends the process instead of leaving it to the hang timeout.
/// </summary>
internal static class SessionLoopWatchdog
{
    [ModuleInitializer]
    internal static void Start()
    {
        var thread = new Thread(Watch) { IsBackground = true, Name = "Avalonia test loop watchdog" };
        thread.Start();
    }

    private static void Watch()
    {
        // Read the session Avalonia's runner created; never create one here. A session
        // started from this thread runs tests without xunit's test context.
        var sessions = typeof(HeadlessUnitTestSession).GetField("s_session", BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetValue(null) as Dictionary<Assembly, HeadlessUnitTestSession>;
        var field = typeof(HeadlessUnitTestSession).GetField("_dispatchTask", BindingFlags.Instance | BindingFlags.NonPublic);
        if (sessions is null || field is null) return; // a different Avalonia version; nothing to watch
        Task? loop = null;
        while (true)
        {
            Thread.Sleep(250);
            if (loop is null)
            {
                lock (sessions)
                {
                    if (sessions.TryGetValue(typeof(SessionLoopWatchdog).Assembly, out var session))
                    {
                        loop = field.GetValue(session) as Task;
                    }
                }
            }

            if (loop is null || !loop.IsFaulted) continue;
            var error = loop.Exception!.GetBaseException();
            var message = "The Avalonia headless test loop died, so no further test can run. Cause: " + error;
            Console.Error.WriteLine(message);
            var log = Environment.GetEnvironmentVariable("CCP_TEST_LOOP_LOG");
            if (!string.IsNullOrWhiteSpace(log))
            {
                try { File.WriteAllText(log, message); } catch { }
            }
            Environment.Exit(70);
        }
    }
}
