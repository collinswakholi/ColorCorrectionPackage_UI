using System.Reflection;
using Avalonia.Headless.XUnit;
using ColorCorrect.App.ViewModels;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class BatchSnapshotTests
{
    [Fact]
    public async Task CancelledBatchDoesNotCreateOrReadAssets()
    {
        var root = Path.Combine(Path.GetTempPath(), "ccp-cancelled-batch-" + Guid.NewGuid().ToString("N"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MainWindowViewModel.CaptureBatchAssetsAsync(
            root, new CorrectionOptions { DoFfc = true, WhiteImagePath = "missing-white.jpg" }, "missing-model.json", true, cancelled.Token));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task BatchPreparationOwnsAssetsWithoutChangingNextRunSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "ccp-batch-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "models", "opt2"));
        Directory.CreateDirectory(Path.Combine(root, "refs"));
        File.WriteAllText(Path.Combine(root, "refs", "neutrals.json"), "{}");
        var model = Path.Combine(root, "source-model.json");
        var white = Path.Combine(root, "white.jpg");
        File.WriteAllText(model, "{\"gc_model\":null,\"gc_coeffs\":[],\"wb_diag\":[1,1,1],\"correction\":null}");
        File.WriteAllText(white, "original white bytes");
        var options = new CorrectionOptions { DoFfc = true, DoGc = true, DoCc = true, WhiteImagePath = white, BatchPreviewResults = false };
        try
        {
            var pending = MainWindowViewModel.CaptureBatchAssetsAsync(root, options, model, true, CancellationToken.None);
            options.DoWb = false;
            options.WhiteImagePath = "settings for next run";
            var captured = await pending;
            Assert.NotEqual(model, captured.ModelPath);
            Assert.NotEqual(white, captured.Options.WhiteImagePath);
            Assert.Equal(File.ReadAllText(model), File.ReadAllText(captured.ModelPath));
            Assert.Equal("original white bytes", File.ReadAllText(captured.Options.WhiteImagePath));
            File.WriteAllText(white, "replacement white");
            Assert.Equal("original white bytes", File.ReadAllText(captured.Options.WhiteImagePath));
            Assert.True(captured.Options.DoWb);
            Assert.False(captured.Options.DoGc);
            Assert.False(captured.Options.DoCc);
            Assert.True(captured.Options.WriteOutput);
            Assert.Equal("none", captured.Options.PreviewMode);
            Assert.Equal("settings for next run", options.WhiteImagePath);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void QueuedFitRetainsChartSelectionAndProfileRevision()
    {
        using var item = new SessionItemViewModel("queued-image.png");
        item.ApplyHandshake(new HandshakeDocument("{\"chart_type\":\"classic\",\"snapshot_id\":\"original\"}"));
        item.FaceChoice = 1;
        var profile = new ReferenceProfile { Id = "captured", Values = ReferenceCatalog.Get("classic").Lab.Select(v => v.ToArray()).ToList() };
        item.ReferenceProfiles.Add(profile);
        item.ReferenceAssignments.Add(new() { ChartId = item.Handshake!.ChartId(0), ProfileId = profile.Id, ProfileRevision = 1 });
        var method = typeof(MainWindowViewModel).GetMethod("CaptureBatchFit", BindingFlags.NonPublic | BindingFlags.Static)!;
        var snapshot = method.Invoke(null, [item, new CorrectionOptions(), false])!;
        T Get<T>(string name) => (T)snapshot.GetType().GetProperty(name)!.GetValue(snapshot)!;
        var expected = profile.Values[0][0];
        profile.Values[0][0] = 0;
        item.FaceChoice = 0;
        item.UseAllFaces = true;
        item.ApplyHandshake(new HandshakeDocument("{\"chart_type\":\"classic\",\"snapshot_id\":\"replacement\"}"));
        Assert.Equal(1, Get<int>("FaceChoice"));
        Assert.False(Get<bool>("UseAllFaces"));
        Assert.Equal("original", Get<HandshakeDocument>("Handshake").SnapshotId);
        Assert.Equal(expected, Get<CorrectionOptions>("Options").ReferenceProfiles.Single().Values[0][0]);
    }

    [AvaloniaFact]
    public void RemovedItemCannotInstallAnArrivingModelOrOutput()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false) { Busy = false };
        using var removed = new SessionItemViewModel("removed-image.png");
        var model = Path.GetTempFileName();
        try
        {
            typeof(MainWindowViewModel).GetMethod("ApplyOutcome", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [removed, new CorrectionOutcome { Ok = true, ModelPath = model, OutputPath = "stale-output.jpg" }, true, true]);
            Assert.False(removed.IsCorrected);
            Assert.Equal("", removed.CorrectedPath);
            Assert.NotEqual(model, vm.LoadedModelPath);
        }
        finally { File.Delete(model); }
    }
}
