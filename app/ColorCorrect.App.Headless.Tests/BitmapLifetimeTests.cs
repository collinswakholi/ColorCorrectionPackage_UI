using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ColorCorrect.App.Services;
using ColorCorrect.App.ViewModels;
using ColorCorrect.App.Views;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class BitmapLifetimeTests
{
    [AvaloniaFact]
    public void ClearingDisplayedPreviewDoesNotDisposeAnOwnedStageBitmap()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        using var bitmap = PreviewRgb.FromRgb(2, 2, new byte[12]);
        var cache = (Dictionary<string, Bitmap>)typeof(MainWindowViewModel).GetField("_stageBitmaps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        cache["corrected"] = bitmap; vm.Preview = bitmap;
        typeof(MainWindowViewModel).GetMethod("SetPreview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [null, null]);
        Assert.Equal(2, cache["corrected"].PixelSize.Width);
        cache.Clear();
    }

    [AvaloniaFact]
    public async Task CachedOriginalSurvivesEvictionUntilItsPendingDisplayUpdateReleasesIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "ccp-original-leases-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var decode = typeof(MainWindowViewModel).GetMethod("GetOrDecodeOriginal", flags)!;
        var release = typeof(MainWindowViewModel).GetMethod("ReleaseIfUncached", flags)!;
        var display = typeof(MainWindowViewModel).GetMethod("SetPreview", flags)!;
        var pending = new List<Bitmap>();
        try
        {
            for (var i = 0; i < 12; i++)
            {
                var path = Path.Combine(root, $"{i}.png");
                using (var image = PreviewRgb.FromRgb(2, 2, new byte[12])) image.Save(path);
                pending.Add((Bitmap)decode.Invoke(vm, [path])!);
            }
            var first = pending[0]; Assert.Equal(2, first.PixelSize.Width);
            display.Invoke(vm, [first, null]);
            foreach (var bitmap in pending) release.Invoke(vm, [bitmap]);
            Assert.Equal(2, first.PixelSize.Width);
            display.Invoke(vm, [null, null]);
            Assert.Throws<ObjectDisposedException>(() => first.PixelSize);
        }
        finally { await vm.DisposeAsync(); Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void ThumbnailOwnersClearTheirBindingBeforeDisposingPixels()
    {
        using var session = new SessionItemViewModel("unavailable.png");
        using var catalog = new CatalogItemViewModel(new("unavailable.png", "unavailable.png", "Samples", false));
        var sessionBitmap = PreviewRgb.FromRgb(2, 2, new byte[12]);
        var catalogBitmap = PreviewRgb.FromRgb(2, 2, new byte[12]);
        session.Thumb = sessionBitmap; catalog.Thumb = catalogBitmap;
        session.PropertyChanged += (_, args) => { if (args.PropertyName == "Thumb") Assert.Equal(2, sessionBitmap.PixelSize.Width); };
        catalog.PropertyChanged += (_, args) => { if (args.PropertyName == "Thumb") Assert.Equal(2, catalogBitmap.PixelSize.Width); };
        session.Dispose(); catalog.Dispose();
        Assert.Null(session.Thumb); Assert.Null(catalog.Thumb);
        Assert.Throws<ObjectDisposedException>(() => sessionBitmap.PixelSize);
        Assert.Throws<ObjectDisposedException>(() => catalogBitmap.PixelSize);
    }

    [AvaloniaFact]
    public async Task RelinkingSavedCompletedResultAfterMissingSourceReloadKeepsRenderedImagesAlive()
    {
        var root = Path.Combine(Path.GetTempPath(), "ccp-bitmap-relink-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var vm = new MainWindowViewModel(persistenceEnabled: false) { Busy = false };
        var window = new MainWindow { DataContext = vm };
        try
        {
            var source = Path.Combine(root, "source.png"); var moved = Path.Combine(root, "moved.png");
            using (var bitmap = PreviewRgb.FromRgb(16, 16, Enumerable.Repeat((byte)125, 16 * 16 * 3).ToArray())) bitmap.Save(source);
            var model = Path.Combine(root, "model.json"); File.WriteAllText(model, "{}");
            var preview = Path.Combine(root, "previews"); Directory.CreateDirectory(preview);
            PreviewRgb.Write(Path.Combine(preview, "corrected.rgb"), 16, 16, Enumerable.Repeat((byte)160, 16 * 16 * 3).ToArray());
            PreviewRgb.Write(Path.Combine(preview, "gc.rgb"), 16, 16, Enumerable.Repeat((byte)150, 16 * 16 * 3).ToArray());
            var context = CompletedResultContext.Capture(AssetIdentity.Capture(source), model, new() { DoGc = true });
            var session = Path.Combine(root, "session.json");
            Assert.True(SessionStore.Write(session, new SessionState { ActivePath = source, Items = [new() { Path = source, ModelPath = model, PreviewDir = preview, CompletedContext = context }] }));
            File.Move(source, moved);
            var (loaded, error) = SessionStore.TryLoadFrom(session); Assert.Null(error);
            window.Show(); vm.RestoreWorkingItems(loaded);
            var stages = (Dictionary<string, Bitmap>)typeof(MainWindowViewModel).GetField("_stageBitmaps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
            await Until(() => vm.Preview is not null && vm.CorrectedPreview is not null && stages.ContainsKey("corrected"));
            Assert.True(vm.Active!.IsMissing);
            Assert.True(SessionStore.Write(Path.Combine(root, "unresolved.json"), loaded));
            vm.Dialogs = new RelinkDialogs(moved);
            await vm.RelinkImageCommand.ExecuteAsync(null);
            await Until(() => vm.OriginalPreview is not null && vm.Active?.Thumb is not null);
            Assert.False(vm.Active!.IsMissing); Assert.NotNull(vm.Active.CompletedContext); Assert.Equal(moved, vm.Active.Path);
            for (var cycle = 0; cycle < 5; cycle++)
            {
                foreach (var stage in new[] { "GC", "Corrected", "Original" })
                {
                    vm.StageName = stage;
                    await Until(() => stage == "Original" ? ReferenceEquals(vm.Preview, vm.OriginalPreview)
                        : stages.TryGetValue(stage.ToLowerInvariant(), out var stageBitmap) && ReferenceEquals(vm.Preview, stageBitmap));
                    Dispatcher.UIThread.RunJobs();
                    using var rendered = window.CaptureRenderedFrame(); Assert.NotNull(rendered);
                    Assert.True(vm.Preview!.PixelSize.Width > 0);
                }
            }
        }
        finally
        {
            window.Close(); Dispatcher.UIThread.RunJobs(); await vm.DisposeAsync(); Dispatcher.UIThread.RunJobs(); Directory.Delete(root, true);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
        Assert.True(condition());
    }

    private sealed class RelinkDialogs(string path) : IAppDialogs
    {
        public Task<IReadOnlyList<string>> OpenImagesAsync() => Task.FromResult<IReadOnlyList<string>>([path]);
        public Task<IReadOnlyList<string>> OpenFolderImagesAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> OpenJsonAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> SaveJsonAsync(string title, string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> SaveImageAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> SavePlotAsync(string title, string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> OpenWhiteAsync() => Task.FromResult<string?>(null);
        public Task<string?> SaveCubeAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message, string? confirmLabel = null, bool danger = false) => Task.FromResult(true);
        public Task SetClipboardAsync(string text) => Task.CompletedTask;
    }
}
