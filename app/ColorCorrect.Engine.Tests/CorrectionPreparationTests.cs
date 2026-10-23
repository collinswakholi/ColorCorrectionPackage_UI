using System.Collections;
using System.Reflection;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.Engine.Tests;

public sealed class CorrectionPreparationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccp-preparation-" + Guid.NewGuid().ToString("N"));
    public CorrectionPreparationTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "models", "opt2"));
        Asset("refs/neutrals.json", "{}");
    }

    private string Asset(string relative, string bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, bytes);
        return path;
    }

    private static HandshakeDocument Geometry() => new("{\"chart_type\":\"classic\",\"cli_chart\":\"classic\"}");

    [Theory]
    [InlineData("fit")]
    [InlineData("apply")]
    [InlineData("batch")]
    public async Task AlreadyCancelledCallsLeaveAllRunAndDestinationPathsUntouched(string operation)
    {
        var source = Asset("photo.jpg", "source"); var model = Asset("model.json", "{}"); var white = Asset("white.jpg", "white");
        var options = new CorrectionOptions { DoFfc = true, WhiteImagePath = white, OutputDir = Path.Combine(_root, "destination") };
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order().ToArray();
        await using var worker = new SpeedyWorker(Path.Combine(_root, "never-launched"), _root);
        var client = new CorrectionClient(worker, _root);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Task Call() => operation switch
        {
            "fit" => client.FitAsync(source, Geometry(), options, 0, false, .75, cancellation.Token),
            "apply" => client.ApplyModelAsync(source, model, options, cancellation.Token),
            _ => client.ApplyManyAsync(Enumerable.Repeat(source, 10).ToArray(), model, options, cancellation.Token),
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(Call);
        Assert.Equal(before, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        Assert.False(Directory.Exists(Path.Combine(_root, "assets")));
        Assert.False(Directory.Exists(options.OutputDir));
        Assert.Equal(white, options.WhiteImagePath);
    }

    [Fact]
    public async Task ApplyCapturesInputsAndSettingsBeforeReturningToTheCaller()
    {
        var source = Asset("photo.jpg", "source"); var model = Asset("model.json", "{}"); var white = Asset("white.jpg", "captured white");
        var destination = Path.Combine(_root, "captured-destination");
        var options = new CorrectionOptions { DoFfc = true, WhiteImagePath = white, OutputDir = destination };
        var inputs = new CallerOwnedInputs([source, source]); var caller = Environment.CurrentManagedThreadId;
        await using var worker = new SpeedyWorker(Path.Combine(_root, "never-launched"), _root);
        var gate = (SemaphoreSlim)typeof(SpeedyWorker).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;
        await gate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var pending = new CorrectionClient(worker, _root).ApplyManyAsync(inputs, model, options, cancellation.Token);
            inputs.CallerContinued();
            options.WhiteImagePath = Path.Combine(_root, "edited-missing-white.jpg"); options.OutputDir = Path.Combine(_root, "edited-destination");
            await WaitUntil(() => Directory.Exists(destination)); // Preparation finished; worker capability request remains gated.
            var run = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "assets", "runs")));
            Assert.Equal("captured white", File.ReadAllText(Path.Combine(run, "white-reference.jpg")));
            Assert.False(Directory.Exists(options.OutputDir));
            Assert.Equal(caller, inputs.ReadThread); Assert.Equal(1, inputs.Enumerations); Assert.False(inputs.LateRead);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.EndsWith("edited-missing-white.jpg", options.WhiteImagePath);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task FitCapturesWhiteAndGeometryBeforeCallerChangesNextSettings()
    {
        var source = Asset("photo.jpg", "source"); var white = Asset("white.jpg", "captured white");
        var options = new CorrectionOptions { DoFfc = true, WhiteImagePath = white };
        var geometry = Geometry();
        await using var worker = new SpeedyWorker(Path.Combine(_root, "never-launched"), _root);
        var gate = (SemaphoreSlim)typeof(SpeedyWorker).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;
        await gate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var pending = new CorrectionClient(worker, _root).FitAsync(source, geometry, options, 0, false, .75, cancellation.Token);
            options.WhiteImagePath = Path.Combine(_root, "edited-white.jpg"); options.DoFfc = false;
            await WaitUntil(() => Directory.Exists(Path.Combine(_root, "assets", "runs"))
                && Directory.GetDirectories(Path.Combine(_root, "assets", "runs"), "previews", SearchOption.AllDirectories).Length == 1);
            var run = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "assets", "runs")));
            Assert.Equal("captured white", File.ReadAllText(Path.Combine(run, "white-reference.jpg")));
            Assert.Contains(geometry.SnapshotId, File.ReadAllText(Path.Combine(run, "chart.json")));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.False(options.DoFfc); Assert.EndsWith("edited-white.jpg", options.WhiteImagePath);
        }
        finally { gate.Release(); }
    }

    private static async Task WaitUntil(Func<bool> complete)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!complete()) await Task.Delay(10, deadline.Token);
    }

    private sealed class CallerOwnedInputs(List<string> values) : IReadOnlyList<string>
    {
        private bool _returned;
        public int ReadThread { get; private set; }
        public int Enumerations { get; private set; }
        public bool LateRead { get; private set; }
        private void Read() { ReadThread = Environment.CurrentManagedThreadId; if (_returned) { LateRead = true; throw new InvalidOperationException("The caller changed this input list after the asynchronous call returned."); } }
        public int Count { get { Read(); return values.Count; } }
        public string this[int index] { get { Read(); return values[index]; } }
        public IEnumerator<string> GetEnumerator() { Read(); Enumerations++; return values.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void CallerContinued() { _returned = true; values.Clear(); values.Add("edited-missing-image.jpg"); }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
