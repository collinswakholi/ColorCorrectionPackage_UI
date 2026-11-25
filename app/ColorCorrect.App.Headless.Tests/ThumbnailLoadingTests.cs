using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ColorCorrect.App.Services;
using ColorCorrect.App.ViewModels;
using SkiaSharp;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class ThumbnailLoadingTests
{
    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task UndecodableFileShowsFailedThumbnailInsteadOfBlankTile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccp-thumb-bad-{Guid.NewGuid():N}.jpg");
        File.WriteAllText(path, "not an image");
        try
        {
            using var item = new SessionItemViewModel(path);
            await WaitFor(() => item.ThumbFailed);
            Assert.True(item.ThumbFailed);
            Assert.Null(item.Thumb);
            Assert.Equal("Unsupported or damaged image.", item.ThumbError);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task ValidFileLoadsThumbnailWithoutError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccp-thumb-ok-{Guid.NewGuid():N}.png");
        using (var bmp = new SKBitmap(400, 300)) { bmp.Erase(SKColors.Teal); using var img = SKImage.FromBitmap(bmp); File.WriteAllBytes(path, img.Encode(SKEncodedImageFormat.Png, 100).ToArray()); }
        try
        {
            using var item = new SessionItemViewModel(path);
            await WaitFor(() => item.Thumb is not null);
            Assert.NotNull(item.Thumb);
            Assert.False(item.ThumbFailed);
            Assert.Equal(96, item.Thumb!.PixelSize.Width);
            Assert.Equal(72, item.Thumb.PixelSize.Height);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public void FailedDecodeReturnsItsGateSlotWhileTheUiThreadIsBusy()
    {
        // A failed decode used to wait for the UI thread before releasing its slot, so
        // two missing files during a busy UI moment stalled every later thumbnail.
        var gate = StillImageDecoder.ThumbnailGate;
        var idle = gate.CurrentCount;
        var path = Path.Combine(Path.GetTempPath(), $"ccp-thumb-missing-{Guid.NewGuid():N}.png");
        using var item = new SessionItemViewModel(path);
        // The constructor takes a slot synchronously. Keep the UI thread blocked (no
        // RunJobs), so the failure notice cannot be shown, and wait for the slot.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (gate.CurrentCount < idle && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.True(gate.CurrentCount >= idle, "a failed decode kept its thumbnail slot while the UI thread was busy");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("File not found.", item.ThumbError);
    }

    [Fact]
    public void ThumbnailGateBoundsConcurrentDecodes()
    {
        // The gate's capacity, not its free count: other tests may be decoding thumbnails
        // at the same moment (the free count read 1 on a CI run while they were).
        Assert.InRange(StillImageDecoder.ThumbnailConcurrency, 2, 4);
    }
}
