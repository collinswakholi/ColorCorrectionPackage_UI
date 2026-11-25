using System;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using ColorCorrect.App.ViewModels;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

/// <summary>Regressions for issues found by driving the real 0.4.0 window (the UI battle test).</summary>
public sealed class BattleTestFindingsTests
{
    [Fact]
    public void OversizedImageErrorIsPlainLanguage()
    {
        // The engine's exact text for a 15000x14000 JPEG (0.4.0 showed it verbatim, env var and all).
        var engine = @"too_big_210mp.jpg: Image is too large: C:\Users\me\AppData\Local\Temp\x\too_big_210mp.jpg is 15000x14000 (210.0 MP); the limit is 200.0 MP. Downscale the image or raise CCP_MAX_IMAGE_MEGAPIXELS.";
        var shown = MainWindowViewModel.ConciseProcessingError(engine);
        Assert.Equal("too_big_210mp.jpg: The image is 15000×14000 (210 MP), over the 200 MP limit. Downscale it and try again.", shown);
        Assert.DoesNotContain("CCP_", shown);
        Assert.DoesNotContain(@"C:\", shown);
    }

    [Fact]
    public void TruncatedJpegErrorSaysTheFileIsDamaged()
    {
        var shown = MainWindowViewModel.ConciseProcessingError(
            "corrupt.jpg: Format error decoding Jpeg: I/O errors Not enough bytes, expected 41972 but found 19988");
        Assert.Equal("corrupt.jpg: The file is damaged or incomplete and cannot be read. Copy or export it again.", shown);
    }

    [Fact]
    public void FullPathsAreShortenedToFileNames()
    {
        var shown = MainWindowViewModel.ConciseProcessingError(
            @"chart_16bit.tif — 16-bit TIFF is not supported: C:\Users\me\scratch\edge_media\chart_16bit.tif. Export an 8-bit JPEG, PNG, or TIFF first.");
        Assert.Equal("chart_16bit.tif — 16-bit TIFF is not supported: chart_16bit.tif. Export an 8-bit JPEG, PNG, or TIFF first.", shown);
        Assert.Equal("photo.jpg failed", MainWindowViewModel.ConciseProcessingError("/home/me/pics/photo.jpg failed"));
    }

    [Theory]
    [InlineData(1, "1 chart")]
    [InlineData(2, "2 charts")]
    [InlineData(0, "0 charts")]
    public void ChartCountsUseRealPlurals(int count, string expected)
    {
        Assert.Equal(expected, MainWindowViewModel.ChartCount(count));
    }

    [AvaloniaFact]
    public void APhotoDeletedAfterLoadingIsReportedAndOffersRelink()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ccp-battle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var photo = Path.Combine(folder, "deleted_later.jpg");
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        File.Copy(Path.Combine(root, "test_images", "Blank.JPG"), photo);
        try
        {
            var vm = new MainWindowViewModel(persistenceEnabled: false);
            vm.AddPaths([photo]);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (vm.Session.All(i => !string.Equals(i.Path, photo, StringComparison.OrdinalIgnoreCase)) && DateTime.UtcNow < deadline)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                System.Threading.Thread.Sleep(20);
            }
            var item = Assert.Single(vm.Session, i => string.Equals(i.Path, photo, StringComparison.OrdinalIgnoreCase));
            vm.Workspace.Active = item;
            Assert.False(item.IsMissing);
            // Let the thumbnail finish reading the file before deleting it (Windows keeps
            // a file that is open for reading until the reader closes it).
            var thumbDeadline = DateTime.UtcNow.AddSeconds(10);
            while (item.Thumb is null && !item.ThumbFailed && DateTime.UtcNow < thumbDeadline)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                System.Threading.Thread.Sleep(20);
            }
            var notified = false;
            item.PropertyChanged += (_, e) => notified |= e.PropertyName == nameof(SessionItemViewModel.IsMissing);

            File.Delete(photo);
            vm.RefreshMissingFiles();

            Assert.True(notified, "IsMissing must be re-announced so the Relink button appears");
            Assert.True(item.IsMissing);
            Assert.Contains("deleted_later.jpg is no longer at", vm.ValidationMessage);
            Assert.Contains("Relink", vm.ValidationMessage);
        }
        finally
        {
            TestWorkers.DeleteDirectoryAsync(folder).GetAwaiter().GetResult();
        }
    }
}
