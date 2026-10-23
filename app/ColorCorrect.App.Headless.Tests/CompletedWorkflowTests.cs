using System.Reflection;
using Avalonia.Headless.XUnit;
using ColorCorrect.App.ViewModels;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class CompletedWorkflowTests
{
    [AvaloniaFact]
    public void RelinkButtonRequiresAnActiveMissingImage()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false) { Busy = false };
        var window = new ColorCorrect.App.Views.MainWindow { DataContext = vm };
        var available = Path.GetTempFileName();
        using var missing = new SessionItemViewModel(available + ".missing.jpg");
        using var present = new SessionItemViewModel(available);
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(window.RelinkSelectedImage.IsVisible);
            vm.Active = missing; Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(window.RelinkSelectedImage.IsVisible);
            vm.Active = present; Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(window.RelinkSelectedImage.IsVisible);
            vm.Active = null; Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(window.RelinkSelectedImage.IsVisible);
        }
        finally { window.Close(); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); File.Delete(available); }
    }

    [AvaloniaFact]
    public void ApplyOnlyCapturesModelBAndFailureCannotReplaceItWithModelC()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccp-result-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var photo = Path.Combine(dir, "photo.jpg"); File.WriteAllText(photo, "image");
            var b = Path.Combine(dir, "B.json"); var c = Path.Combine(dir, "C.json"); File.WriteAllText(b, "{}"); File.WriteAllText(c, "{}");
            var vm = new MainWindowViewModel(persistenceEnabled: false);
            using var item = new SessionItemViewModel(photo) { ModelPath = "old-A.json" }; vm.Session.Add(item);
            var context = CompletedResultContext.Capture(AssetIdentity.Capture(photo), b, new() { DoGc = false, DoWb = true, FastMode = false });
            var apply = typeof(MainWindowViewModel).GetMethod("ApplyOutcome", BindingFlags.Instance | BindingFlags.NonPublic)!;
            apply.Invoke(vm, [item, new CorrectionOutcome { Ok = true, ModelPath = b, CompletedContext = context }, false, false]);
            vm.LoadedModelPath = c; vm.DoGc = true; vm.DoWb = false; vm.FastMode = true;
            Assert.Equal(b, item.ModelPath); Assert.Equal(b, item.CompletedContext!.Model.Path);
            Assert.False(vm.ReplayOptions(item)!.DoGc); Assert.True(vm.ReplayOptions(item)!.DoWb); Assert.False(vm.ReplayOptions(item)!.FastMode);
            apply.Invoke(vm, [item, new CorrectionOutcome { Ok = false, ModelPath = c, Error = "cancelled" }, false, false]);
            Assert.Same(context, item.CompletedContext); Assert.Equal(b, item.ModelPath); Assert.True(item.IsCorrected);
            var session = Path.Combine(dir, "session.json");
            Assert.True(SessionStore.Write(session, new SessionState { Items = [new() { Path = photo, ModelPath = b, CompletedContext = context }] }));
            var (saved, error) = SessionStore.TryLoadFrom(session); Assert.Null(error);
            vm.RestoreWorkingItems(saved); Assert.Equal(context.Model.Sha256, vm.Session[0].CompletedContext!.Model.Sha256);
            Assert.False(vm.ReplayOptions(vm.Session[0])!.DoGc);
            foreach (var restored in vm.Session) restored.Dispose();
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public void MissingSessionImageKeepsReferencesAndCanRelinkIdenticalContent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccp-relink-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var original = Path.Combine(dir, "original.jpg"); File.WriteAllText(original, "photo"); var identity = AssetIdentity.Capture(original);
            var moved = Path.Combine(dir, "moved.jpg"); File.Move(original, moved);
            var vm = new MainWindowViewModel(persistenceEnabled: false);
            vm.RestoreWorkingItems(new SessionState { Items = [new() { Path = original, SourceIdentity = identity, HandshakeJson = "{\"chart_type\":\"classic\",\"snapshot_id\":\"detection\"}", ModelPath = "unavailable-model.json" }] });
            using var item = Assert.Single(vm.Session); Assert.True(item.IsMissing); Assert.Equal("unavailable-model.json", item.ModelPath);
            Assert.True(item.Relink(moved)); Assert.False(item.IsMissing); Assert.NotNull(item.Handshake);
            var other = Path.Combine(dir, "other.jpg"); File.WriteAllText(other, "different");
            Assert.False(item.Relink(other)); Assert.Null(item.Handshake); Assert.False(item.IsCorrected);
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public void NumericDraftsAreValidatedOnlyForTheRequestedOperation()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        vm.SelectedMethod = vm.Methods.Single(x => x.Value == "nn"); vm.NnEpochsText = "bad";
        Assert.Contains("Epochs", vm.OperationError(true)); Assert.Null(vm.OperationError(false));
        vm.NnEpochsText = "50.5"; Assert.Contains("whole number", vm.OperationError(true));
        vm.NnEpochsText = "50"; Assert.Null(vm.OperationError(true));
        vm.FfcBinsText = "bad"; Assert.Null(vm.OperationError(true));
        vm.DoFfc = true; Assert.Contains("white reference", vm.OperationError(true));
        vm.DoFfc = false; vm.DoCc = false; vm.HiddenLayersText = "bad"; Assert.Null(vm.OperationError(true));
    }

    [AvaloniaFact]
    public void CancelIsAvailableDuringSingleImageWork()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false) { Busy = true, IsBatching = false };
        Assert.True(vm.CancelBatchCommand.CanExecute(null)); vm.CancelBatchCommand.Execute(null);
        Assert.Contains("Completed results are kept", vm.Status);
        vm.Busy = false; Assert.False(vm.CancelBatchCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void CancelSignalsCurrentJobWithoutRemovingEarlierBatchResults()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false) { Busy = true, IsBatching = true };
        using var completed = new SessionItemViewModel("completed.jpg") { IsCorrected = true, ModelPath = "owned-B.json", CorrectedPath = "saved-B.jpg" };
        vm.Session.Add(completed);
        var token = (CancellationToken)typeof(MainWindowViewModel).GetMethod("BeginJob", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null)!;
        vm.CancelBatchCommand.Execute(null);
        Assert.True(token.IsCancellationRequested);
        Assert.True(completed.IsCorrected); Assert.Equal("owned-B.json", completed.ModelPath); Assert.Equal("saved-B.jpg", completed.CorrectedPath);
    }

    [AvaloniaFact]
    public void ReloadedResultKeepsSavedOutputsWhenOwnedWhiteIsUnavailable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccp-result-white-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var photo = Path.Combine(dir, "photo.jpg"); var model = Path.Combine(dir, "B.json"); var white = Path.Combine(dir, "white.jpg"); var corrected = Path.Combine(dir, "corrected.jpg");
            File.WriteAllText(photo, "photo"); File.WriteAllText(model, "{}"); File.WriteAllText(white, "white A"); File.WriteAllText(corrected, "completed pixels");
            var context = CompletedResultContext.Capture(AssetIdentity.Capture(photo), model, new() { DoFfc = true, WhiteImagePath = white });
            var session = Path.Combine(dir, "saved.json");
            Assert.True(SessionStore.Write(session, new SessionState { Items = [new() { Path = photo, ModelPath = model, CompletedContext = context, CorrectedPath = corrected }] }));
            File.Delete(white); File.Delete(model);
            var (saved, error) = SessionStore.TryLoadFrom(session); Assert.Null(error);
            var vm = new MainWindowViewModel(persistenceEnabled: false); vm.RestoreWorkingItems(saved);
            using var item = Assert.Single(vm.Session); Assert.NotNull(vm.ReplayOptions(item));
            var owned = item.CompletedContext!; File.WriteAllText(owned.White!.Path, "different reference");
            Assert.Null(vm.ReplayOptions(item)); Assert.True(item.IsCorrected); Assert.True(File.Exists(item.CorrectedPath)); Assert.Same(owned, item.CompletedContext);
            File.Delete(owned.White.Path); Assert.Null(vm.ReplayOptions(item)); Assert.Equal("completed pixels", File.ReadAllText(item.CorrectedPath));
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task EightyFirstImportCannotEvictCompletedResultsAndRestoredSessionsKeepAllEntries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccp-capacity-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        try
        {
            using var pixel = new SkiaSharp.SKBitmap(1, 1); pixel.Erase(SkiaSharp.SKColors.Gray);
            using var image = SkiaSharp.SKImage.FromBitmap(pixel); using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            var bytes = png.ToArray(); var paths = Enumerable.Range(0, 81).Select(i => Path.Combine(dir, $"photo-{i:00}.png")).ToArray();
            foreach (var path in paths) File.WriteAllBytes(path, bytes);
            vm.AddPaths(paths[..80]); Assert.Equal(80, vm.Session.Count);
            var first = vm.Session[0]; first.IsCorrected = true; first.ModelPath = "owned-B.json";
            vm.AddPaths([paths[80]]); Assert.Equal(80, vm.Session.Count); Assert.Same(first, vm.Session[0]); Assert.Equal("owned-B.json", first.ModelPath); Assert.True(first.IsCorrected);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (vm.Session.Any(item => item.Thumb is null) && DateTime.UtcNow < deadline) { await Task.Delay(20); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); }
            Assert.All(vm.Session, item => Assert.NotNull(item.Thumb));
            vm.RestoreWorkingItems(new SessionState { Items = paths.Select(path => new SessionItemDto { Path = path, ModelPath = "retained.json" }).ToList() });
            Assert.Equal(81, vm.Session.Count); Assert.Equal(paths[80], vm.Session[80].Path); Assert.All(vm.Session, item => Assert.Equal("retained.json", item.ModelPath));
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (vm.Session.Any(item => item.Thumb is null) && DateTime.UtcNow < deadline) { await Task.Delay(20); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); }
        }
        finally { foreach (var item in vm.Session) item.Dispose(); Directory.Delete(dir, true); }
    }

    [AvaloniaTheory]
    [InlineData("linear")][InlineData("poly")][InlineData("pls")][InlineData("nn")]
    public void LoadingModelRestoresFfcFamilyAndRejectsChangedModelOrWhite(string method)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccp-load-model-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var white = Path.Combine(dir, "white.png"); File.WriteAllText(white, "white A");
            var model = Path.Combine(dir, "model.json");
            File.WriteAllText(model, System.Text.Json.JsonSerializer.Serialize(new { do_ffc = true, do_gc = false, do_wb = true, do_cc = true,
                ffc_params = new { method, bins = 17, smooth_window = 3, degree = 4, nn_max_iter = 90, tol = 1e-7 },
                python_api = new { white, white_sha256 = AssetIdentity.Capture(white).Sha256.ToLowerInvariant(), crop_rect = "1,2,30,40" } }));
            var vm = new MainWindowViewModel(persistenceEnabled: false) { LoadedModelPath = model };
            vm.RestoreLoadedModelSettings(model);
            Assert.Equal(method, vm.SelectedFfcFitMethod.Value); Assert.Equal(17m, vm.FfcBins); Assert.Equal(3m, vm.FfcSmoothWindow);
            Assert.Equal(4m, vm.FfcDegree); Assert.Equal(90m, vm.FfcNnMaxIter); Assert.Equal(0.0000001m, vm.FfcTol);
            Assert.Equal("1,2,30,40", vm.CropRect); Assert.False(vm.DoGc); Assert.True(vm.DoFfc);
            Assert.Null(vm.OperationError(false));
            File.WriteAllText(white, "white B"); Assert.Contains("white reference changed", vm.OperationError(false));
            File.WriteAllText(white, "white A"); File.WriteAllText(model, "{}"); Assert.Contains("model changed", vm.OperationError(false));
        }
        finally { Directory.Delete(dir, true); }
    }
}
