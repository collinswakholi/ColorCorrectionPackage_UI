using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.Engine.Tests;

/// <summary>The worker timeout bounds silence, not total run time, up to a ceiling.</summary>
public sealed class WorkerTimeoutTests
{
    private static Task<JsonElement> Read(StreamReader reader, TimeSpan timeout, List<string> progress)
    {
        var method = typeof(SpeedyWorker).GetMethod("ReadResultAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (Task<JsonElement>)method.Invoke(null, [reader, timeout, (Action<string>)progress.Add, CancellationToken.None])!;
    }

    private static async Task Write(Stream stream, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    [Fact]
    public async Task ProgressKeepsALongRunningCommandAlive()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.Out);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.GetClientHandleAsString());
        using var reader = new StreamReader(client);
        var progress = new List<string>();
        var reading = Read(reader, TimeSpan.FromMilliseconds(400), progress);
        // 1.2 s in total, three times the timeout, but never silent for longer than 150 ms.
        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(150);
            await Write(server, $$"""{"type":"progress","message":"step {{i}}"}""");
        }
        await Write(server, """{"ok":true}""");
        var result = await reading.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(8, progress.Count);
    }

    [Fact]
    public async Task SilenceStillTimesOut()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.Out);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.GetClientHandleAsString());
        using var reader = new StreamReader(client);
        var reading = Read(reader, TimeSpan.FromMilliseconds(200), []);
        await Assert.ThrowsAsync<TimeoutException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task EndlessProgressStopsAtTheCeiling()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.Out);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.GetClientHandleAsString());
        using var reader = new StreamReader(client);
        var timeout = TimeSpan.FromMilliseconds(100);
        var reading = Read(reader, timeout, []);
        using var stop = new CancellationTokenSource();
        var spam = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(30);
                    await Write(server, """{"type":"progress","message":"still going"}""");
                }
            }
            catch (IOException) { }
        });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => reading.WaitAsync(TimeSpan.FromSeconds(10)));
        stop.Cancel();
        Assert.InRange(watch.Elapsed, timeout * (SpeedyWorker.ProgressCeilingFactor - 1), TimeSpan.FromSeconds(5));
    }
}
