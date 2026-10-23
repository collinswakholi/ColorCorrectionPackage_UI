using System.Text.Json;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.Engine.Tests;

public sealed class CompletedResultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccp-completed-" + Guid.NewGuid().ToString("N"));
    public CompletedResultTests() => Directory.CreateDirectory(_root);
    private string FileAt(string name, string content) { var path = Path.Combine(_root, name); File.WriteAllText(path, content); return path; }

    [Fact]
    public void FrozenOptionsAndAssetIdentitiesSurviveCallerEditsAndDetectContentChanges()
    {
        var photo = FileAt("photo.jpg", "photo A"); var model = FileAt("model.json", "{}"); var white = FileAt("white.jpg", "white A");
        var options = new CorrectionOptions { DoFfc = true, WhiteImagePath = white, FfcFitMethod = "pls", FfcDegree = 4 };
        var context = CompletedResultContext.Capture(AssetIdentity.Capture(photo), model, options);
        options.FfcDegree = 7; context.Options().FfcDegree = 6;
        Assert.Equal(4, context.Options().FfcDegree); Assert.Equal("pls", context.Options().FfcFitMethod);
        Assert.Null(context.ReplayError(photo));
        File.WriteAllText(white, "white B"); Assert.Contains("white reference", context.ReplayError(photo));
        File.WriteAllText(white, "white A"); File.WriteAllText(model, "{\"changed\":true}"); Assert.Contains("model", context.ReplayError(photo));
        File.WriteAllText(model, "{}"); File.WriteAllText(photo, "photo B"); Assert.Contains("original image", context.ReplayError(photo));
    }

    [Theory]
    [InlineData("linear")][InlineData("poly")][InlineData("pls")][InlineData("nn")]
    public void ExplicitSessionOwnsResultsAndRetainsMissingSources(string method)
    {
        var photo = FileAt("photo.jpg", "photo"); var model = FileAt("model.json", "{}"); var white = FileAt("white.jpg", "white");
        var context = CompletedResultContext.Capture(AssetIdentity.Capture(photo), model,
            new CorrectionOptions { DoFfc = true, WhiteImagePath = white, FfcFitMethod = method, FfcBins = 17, FfcSmoothWindow = 3, FfcDegree = 4, FfcNnMaxIter = 90, FfcTol = 1e-7, CropRect = "1,2,30,40" });
        var path = Path.Combine(_root, "session.json");
        var state = new SessionState { Items = [new() { Path = photo, ModelPath = model, CompletedContext = context, SourceIdentity = context.Source, HandshakeJson = "{\"snapshot_id\":\"saved\"}" }] };
        Assert.True(SessionStore.Write(path, state));
        File.Delete(model); File.Delete(white); File.Delete(photo);
        var (loaded, error) = SessionStore.TryLoadFrom(path); Assert.Null(error);
        var item = Assert.Single(loaded.Items); Assert.Equal(photo, item.Path); Assert.Equal(state.Items[0].HandshakeJson, item.HandshakeJson);
        Assert.True(File.Exists(item.ModelPath)); Assert.True(File.Exists(item.CompletedContext!.White!.Path));
        var restored = item.CompletedContext.Options(); Assert.Equal(method, restored.FfcFitMethod); Assert.Equal(17, restored.FfcBins);
        Assert.Equal(3, restored.FfcSmoothWindow); Assert.Equal(4, restored.FfcDegree); Assert.Equal(90, restored.FfcNnMaxIter); Assert.Equal(1e-7, restored.FfcTol); Assert.Equal("1,2,30,40", restored.CropRect);
        var folders = Directory.GetDirectories(path + ".assets").Length;
        Assert.True(SessionStore.Write(path, loaded));
        Assert.Equal(folders, Directory.GetDirectories(path + ".assets").Length);
        var relocated = FileAt("moved.jpg", "photo"); Assert.Null(item.CompletedContext.RelinkSource(relocated).ReplayError(relocated));
    }

    [Fact]
    public void RelocatedSessionUsesOwnedAssetsAndRejectsChangedOrMissingWhite()
    {
        var photo = FileAt("photo.jpg", "photo"); var model = FileAt("model.json", "{}"); var white = FileAt("white.jpg", "white A");
        var context = CompletedResultContext.Capture(AssetIdentity.Capture(photo), model, new() { DoFfc = true, WhiteImagePath = white });
        var original = Path.Combine(_root, "original.json"); var moved = Path.Combine(_root, "moved.json");
        Assert.True(SessionStore.Write(original, new SessionState { Options = new() { LoadedModelPath = model }, Items = [new() { Path = photo, CompletedContext = context, ModelPath = model }] }));
        File.Move(original, moved); Directory.Move(original + ".assets", moved + ".assets");
        File.Delete(model); File.Delete(white);
        var (state, error) = SessionStore.TryLoadFrom(moved); Assert.Null(error);
        var owned = Assert.Single(state.Items).CompletedContext!;
        Assert.Null(owned.ReplayError(photo)); Assert.Equal(owned.Model.Path, state.Options.LoadedModelPath);
        Assert.StartsWith(moved + ".assets", owned.Model.Path);
        Assert.Equal(owned.White!.Path, owned.Options().WhiteImagePath);
        File.WriteAllText(owned.White.Path, "white B"); Assert.Contains("white reference", owned.ReplayError(photo));
        File.Delete(owned.White.Path); Assert.Contains("white reference", owned.ReplayError(photo));
        File.WriteAllText(owned.White.Path, "white A"); Assert.Null(owned.ReplayError(photo));
    }

    [Fact]
    public void MalformedCompletedSessionContextIsRejectedAtomically()
    {
        var photo = FileAt("photo.png", "photo"); var model = FileAt("model.json", "{}");
        var original = CompletedResultContext.Capture(AssetIdentity.Capture(photo), model, new());
        var invalid = new[] {
            original with { OptionsJson = "{" },
            original with { OptionsJson = "{\"DoGc\":true,\"DoGc\":false}" },
            original with { Version = 2 },
            original with { Model = original.Model with { Sha256 = "not-a-hash" } },
            original with { Model = null! },
            original with { OptionsJson = JsonSerializer.Serialize(new CorrectionOptions { DoFfc = true }) },
            original with { White = original.Source, OptionsJson = JsonSerializer.Serialize(new CorrectionOptions { DoFfc = true, WhiteImagePath = model }) },
        };
        foreach (var context in invalid)
        {
            var path = FileAt("invalid-session.json", JsonSerializer.Serialize(new SessionState { Items = [new() { Path = photo }, new() { Path = photo, CompletedContext = context }] }));
            var (state, error) = SessionStore.TryLoadFrom(path);
            Assert.NotNull(error); Assert.Empty(state.Items);
        }
    }

    [Fact]
    public void PackagedRunsUseWritableApplicationData()
    {
        FileAt("runtime-support.json", "{}"); var previous = Environment.GetEnvironmentVariable("COLORCORRECT_DATA_DIR");
        try { Environment.SetEnvironmentVariable("COLORCORRECT_DATA_DIR", Path.Combine(_root, "profile")); Assert.Equal(Path.Combine(_root, "profile", "runs"), RunPaths.RunsRoot(_root)); }
        finally { Environment.SetEnvironmentVariable("COLORCORRECT_DATA_DIR", previous); }
    }

    [Fact]
    public async Task DetectionCancellationPropagatesInsteadOfBecomingAFailedDetection()
    {
        await using var worker = new SpeedyWorker(Path.Combine(_root, "unused-worker"), _root);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.DetectAsync("unused.png", cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task ProtocolReadCanBeCancelledWithoutWaitingForItsTimeout()
    {
        using var server = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
        using var client = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, server.GetClientHandleAsString());
        using var reader = new StreamReader(client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var method = typeof(SpeedyWorker).GetMethod("ReadResultAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var reading = (Task<JsonElement>)method.Invoke(null, [reader, TimeSpan.FromMinutes(8), null, cancellation.Token])!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(3)));
    }
    public void Dispose() => Directory.Delete(_root, true);
}
