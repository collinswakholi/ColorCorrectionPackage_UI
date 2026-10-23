using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ColorCorrect.App.Services;
using ColorCorrect.App.ViewModels;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class BatchProgressTests
{
    private static object? Call(MainWindowViewModel vm, string method, params object?[] args)
        => typeof(MainWindowViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, args);

    [Fact]
    public void PreviewOffCapturesRealOutputAndOnlyTransformsPresentInSavedModel()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"gc_model\":null,\"gc_coeffs\":[],\"wb_diag\":[1,1,1],\"correction\":null}");
            var source = new CorrectionOptions { DoGc = true, DoWb = true, DoCc = true, OutputDir = "explicit-export-folder" };
            var captured = MainWindowViewModel.CaptureBatchOptions(source, false, path);
            Assert.True(captured.WriteOutput); Assert.Equal("none", captured.PreviewMode); Assert.Equal("", captured.OutputDir);
            Assert.False(captured.BatchPreviewResults); Assert.False(captured.DoGc); Assert.True(captured.DoWb); Assert.False(captured.DoCc);
            Assert.False(source.WriteOutput); Assert.Equal("corrected", source.PreviewMode); Assert.Equal("explicit-export-folder", source.OutputDir);
            var preview = MainWindowViewModel.CaptureBatchOptions(source, true);
            Assert.False(preview.WriteOutput); Assert.Equal("corrected", preview.PreviewMode);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public void EachImageResetsStagesAndCombinedLutDoesNotInventSequentialStages()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false) { Busy = true, IsBatching = true, DoFfc = false, DoWb = true };
        using var first = new SessionItemViewModel("first.jpg");
        using var second = new SessionItemViewModel("second.jpg");
        var captured = new CorrectionOptions { DoFfc = true, DoGc = true, DoWb = false, DoCc = true };
        Call(vm, "BeginBatchImage", first, 0, 2, captured, true);
        Assert.True(vm.PipelineSteps.Single(x => x.Key == "ffc").IsEnabled);
        Assert.False(vm.PipelineSteps.Single(x => x.Key == "wb").IsEnabled);
        Call(vm, "ApplyEngineProgress", "Apply · FFC complete.jpg");
        Assert.Equal("Pending", vm.PipelineSteps.Single(x => x.Key == "ffc").StatusText);
        Call(vm, "ApplyEngineProgress", "FFC");
        Assert.Equal("Running", vm.PipelineSteps.Single(x => x.Key == "ffc").StatusText);
        Call(vm, "ApplyEngineProgress", "FFC complete");
        Call(vm, "ApplyEngineProgress", "Combined LUT · GC + CC");
        Assert.All(vm.PipelineSteps.Where(x => x.Key is "gc" or "cc"), step => { Assert.True(step.IsActive); Assert.False(step.IsDone); });
        Assert.False(vm.PipelineSteps.Single(x => x.Key == "wb").IsActive);
        Assert.InRange(first.ProcessingProgress, 1, 99); Assert.Equal(first.ProcessingProgress / 2, vm.BatchProgress, 6);
        Call(vm, "ApplyEngineProgress", "Combined LUT complete");
        Assert.All(vm.PipelineSteps.Where(x => x.Key is "gc" or "cc"), step => Assert.Equal("Complete", step.StatusText));
        Call(vm, "ApplyEngineProgress", "Writing outputs complete");
        Assert.True(first.ProcessingProgress < 100); // Result ownership is not verified by a progress message.
        Call(vm, "FinishBatchImage", first, true, null);
        Assert.Equal(100, first.ProcessingProgress); Assert.Equal("Complete", first.ProcessingStatus);
        Call(vm, "BeginBatchImage", second, 1, 2, captured, true);
        Assert.Equal(0, vm.JobProgress); Assert.Equal(50, vm.BatchProgress);
        Assert.All(vm.PipelineSteps.Where(x => x.IsEnabled), step => Assert.Equal("Pending", step.StatusText));
        Call(vm, "ApplyEngineProgress", "FFC");
        Call(vm, "FinishBatchImage", second, false, "Cancelled");
        Assert.Equal("Cancelled", second.ProcessingStatus); Assert.True(second.ProcessingProgress < 100);
        Assert.Equal(100, first.ProcessingProgress); Assert.Equal("Complete", first.ProcessingStatus);
    }

    [AvaloniaFact]
    public void IdentityApplyAndFailureNeverMarkUnreportedColorStagesComplete()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false) { Busy = true, IsBatching = true };
        using var item = new SessionItemViewModel("identity.jpg");
        Call(vm, "BeginBatchImage", item, 0, 1, new CorrectionOptions { DoGc = false, DoWb = false, DoCc = false }, true);
        Call(vm, "ApplyEngineProgress", "Loading image");
        Call(vm, "ApplyEngineProgress", "Writing outputs");
        Call(vm, "FinishBatchImage", item, false, "Failed");
        Assert.Equal("Failed", item.ProcessingStatus); Assert.True(item.ProcessingProgress < 100);
        Assert.All(vm.PipelineSteps.Where(x => x.Key is "gc" or "wb" or "cc"), step => { Assert.False(step.IsEnabled); Assert.False(step.IsDone); });
        Assert.False(vm.PipelineSteps.Single(x => x.Key == "write").IsDone);
    }

    [AvaloniaFact]
    public void StartingFusedGcDoesNotCompleteFfcBeforeWorkerConfirmation()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false) { Busy = true, IsBatching = true };
        using var item = new SessionItemViewModel("fused.jpg");
        Call(vm, "BeginBatchImage", item, 0, 1, new CorrectionOptions { DoFfc = true }, false);
        Call(vm, "ApplyEngineProgress", "FFC");
        Call(vm, "ApplyEngineProgress", "GC");
        Assert.True(vm.PipelineSteps.Single(x => x.Key == "ffc").IsActive);
        Assert.False(vm.PipelineSteps.Single(x => x.Key == "ffc").IsDone);
        Assert.True(vm.PipelineSteps.Single(x => x.Key == "gc").IsActive);
        Call(vm, "ApplyEngineProgress", "FFC complete");
        Assert.True(vm.PipelineSteps.Single(x => x.Key == "ffc").IsDone);
        Assert.True(vm.PipelineSteps.Single(x => x.Key == "gc").IsActive);
        Call(vm, "ApplyEngineProgress", "GC complete");
        Assert.True(vm.PipelineSteps.Single(x => x.Key == "gc").IsDone);
    }

    [AvaloniaFact]
    public void ApplyOnlyPlaneComesFromOwnedResultModelInsteadOfEarlierOrLoadedModel()
    {
        var path = Path.GetTempFileName();
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        using var item = new SessionItemViewModel("image.jpg") { FfcPlane = new() { X = 1, Y = 2, Width = 3, Height = 4 } };
        vm.Session.Add(item);
        try
        {
            typeof(MainWindowViewModel).GetField("_loadedModelPlane", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(vm, new FfcPlane { X = 100, Y = 200, Width = 300, Height = 400 });
            File.WriteAllText(path, "{\"ffc_plane\":[10,20,30,40],\"ffc_white_size\":[80,90],\"ffc_plane_source\":\"crop_rect\"}");
            Call(vm, "ApplyOutcome", item, new CorrectionOutcome { Ok = true, ModelPath = path }, false, true);
            Assert.Equal(10, item.FfcPlane!.X); Assert.Equal(20, item.FfcPlane.Y);
            Assert.Equal(30, item.FfcPlane.Width); Assert.Equal(40, item.FfcPlane.Height);
            Assert.Equal(80, item.FfcPlane.WhiteWidth);
            File.WriteAllText(path, "{}");
            Call(vm, "ApplyOutcome", item, new CorrectionOutcome { Ok = true, ModelPath = path }, false, true);
            Assert.Null(item.FfcPlane);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task PreviewOffSurvivesSessionReloadAndOnlyExplicitRequestDisplaysResult()
    {
        var root = Path.Combine(Path.GetTempPath(), "ccp-preview-off-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var vm = new MainWindowViewModel(persistenceEnabled: false) { Busy = false };
        try
        {
            var source = Path.Combine(root, "source.png"); var corrected = Path.Combine(root, "corrected.png");
            using (var bitmap = PreviewRgb.FromRgb(8, 8, Enumerable.Repeat((byte)80, 8 * 8 * 3).ToArray())) bitmap.Save(source);
            using (var bitmap = PreviewRgb.FromRgb(8, 8, Enumerable.Repeat((byte)160, 8 * 8 * 3).ToArray())) bitmap.Save(corrected);
            var model = Path.Combine(root, "model.json"); File.WriteAllText(model, "{}");
            var context = CompletedResultContext.Capture(AssetIdentity.Capture(source), model, new() { WriteOutput = true, PreviewMode = "none" });
            using var item = new SessionItemViewModel(source); vm.Session.Add(item); vm.Active = item;
            Call(vm, "ApplyOutcome", item, new CorrectionOutcome { Ok = true, OutputPath = corrected, ModelPath = model, CompletedContext = context }, false, true);
            Assert.True(item.PreviewSuppressed); Assert.True(vm.IsResultPreviewSuppressed); Assert.Null(vm.CorrectedPreview);
            var session = Path.Combine(root, "session.json");
            Assert.True(SessionStore.Write(session, (SessionState)Call(vm, "BuildSessionState", true)!));
            var (loaded, error) = SessionStore.TryLoadFrom(session); Assert.Null(error);
            vm.RestoreWorkingItems(loaded);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (vm.OriginalPreview is null && DateTime.UtcNow < deadline) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            Assert.True(vm.Active!.PreviewSuppressed); Assert.True(vm.Active.IsCorrected); Assert.Null(vm.CorrectedPreview);
            Assert.True(File.Exists(vm.Active.CorrectedPath));
            await vm.ShowResultPreviewCommand.ExecuteAsync(null);
            Assert.False(vm.Active.PreviewSuppressed); Assert.NotNull(vm.CorrectedPreview);
            Assert.Equal("Corrected", vm.StageName); Assert.Equal(context.Model.Sha256, vm.Active.CompletedContext!.Model.Sha256);
        }
        finally { await vm.DisposeAsync(); Dispatcher.UIThread.RunJobs(); Directory.Delete(root, true); }
    }
}
