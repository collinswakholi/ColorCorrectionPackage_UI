using Avalonia.Headless.XUnit;
using ColorCorrect.App.Services;
using ColorCorrect.App.ViewModels;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class StillImageDecoderTests
{
    [Fact]
    public void OriginalPreviewCacheDoesNotReuseUnorientedUnmanagedLegacyPixels()
    {
        var path = System.IO.Path.GetTempFileName();
        try
        {
            var info = new FileInfo(path);
            var legacyKey = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            var legacyName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacyKey)))[..16] + ".rgb";
            var current = MainWindowViewModel.OriginalCachePath(path, info);
            Assert.NotEqual(legacyName, System.IO.Path.GetFileName(current));
            Assert.Equal(current, MainWindowViewModel.OriginalCachePath(path, info));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OriginalPreviewCacheDoesNotReuseChannelSwappedV1Pixels()
    {
        var path = System.IO.Path.GetTempFileName();
        try
        {
            var info = new FileInfo(path);
            var v1Key = $"oriented-srgb-v1|{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            var v1Name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(v1Key)))[..16] + ".rgb";
            Assert.NotEqual(v1Name, System.IO.Path.GetFileName(MainWindowViewModel.OriginalCachePath(path, info)));
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public void OriginalPreviewDiskCacheRoundTripKeepsRedAndBlue()
    {
        // Regression: the original-preview disk cache read StillImageDecoder's Rgba8888
        // pixels as BGRA, so every reopen from the cache showed red and blue swapped.
        SKColor[] colours = [new(220, 30, 20), new(20, 40, 230), new(30, 200, 40), new(240, 220, 30)];
        using var raw = new SKBitmap(colours.Length, 1);
        for (var x = 0; x < colours.Length; x++) raw.SetPixel(x, 0, colours[x]);
        var source = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ccp-ui-rgb-{Guid.NewGuid():N}.png");
        var cache = System.IO.Path.ChangeExtension(source, ".rgb");
        try
        {
            using (var image = SKImage.FromBitmap(raw))
            using (var encoded = image.Encode(SKEncodedImageFormat.Png, 100))
                File.WriteAllBytes(source, encoded.ToArray());

            using var decoded = StillImageDecoder.Decode(source, 100);
            using var bgra = PreviewRgb.FromRgb(colours.Length, 1, [.. colours.SelectMany(c => new[] { c.Red, c.Green, c.Blue })]);
            foreach (var bitmap in new[] { decoded, bgra })
            {
                PreviewRgb.WriteFromBitmap(cache, bitmap);
                using var reloaded = PreviewRgb.TryLoad(cache);
                Assert.NotNull(reloaded);
                using var output = new MemoryStream();
                reloaded.Save(output);
                using var actual = SKBitmap.Decode(output.ToArray());
                for (var x = 0; x < colours.Length; x++) Assert.Equal(colours[x], actual.GetPixel(x, 0));
            }
        }
        finally
        {
            File.Delete(source);
            File.Delete(cache);
        }
    }

    [AvaloniaFact]
    public void DecoderAndSourceSizeAgreeForAllExifOrientationsInBothByteOrders()
    {
        using var raw = new SKBitmap(3, 2);
        for (var y = 0; y < 2; y++) for (var x = 0; x < 3; x++)
            raw.SetPixel(x, y, new SKColor((byte)(30 + x * 70), (byte)(40 + y * 150), 60));
        using var image = SKImage.FromBitmap(raw);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        var jpeg = encoded.ToArray();
        using var decoded = SKBitmap.Decode(jpeg);
        (int X, int Y)[] corners = [(0, 0), (2, 0), (2, 1), (0, 1), (0, 0), (0, 1), (2, 1), (2, 0)];
        foreach (var big in new[] { false, true }) for (byte orientation = 1; orientation <= 8; orientation++)
        {
            byte[] tiff = big
                ? [(byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, 0, 1, 1, 18, 0, 3, 0, 0, 0, 1, 0, orientation, 0, 0, 0, 0, 0, 0]
                : [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0, orientation, 0, 0, 0, 0, 0, 0, 0];
            byte[] payload = [.. "Exif\0\0"u8.ToArray(), .. tiff];
            byte[] bytes = [.. jpeg[..2], 255, 225, 0, (byte)(payload.Length + 2), .. payload, .. jpeg[2..]];
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ccp-ui-exif-{Guid.NewGuid():N}.jpg");
            File.WriteAllBytes(path, bytes);
            try
            {
                Assert.True(ImageSizeReader.TryRead(path, out var width, out var height));
                Assert.Equal(orientation >= 5 ? 2 : 3, width);
                Assert.Equal(orientation >= 5 ? 3 : 2, height);
                using var bitmap = StillImageDecoder.Decode(path, 100);
                Assert.Equal(width, bitmap.PixelSize.Width);
                Assert.Equal(height, bitmap.PixelSize.Height);
                using var output = new MemoryStream();
                bitmap.Save(output);
                using var actual = SKBitmap.Decode(output.ToArray());
                var (x, y) = corners[orientation - 1];
                Assert.Equal(decoded.GetPixel(x, y), actual.GetPixel(0, 0));
            }
            finally { File.Delete(path); }
        }
    }
}
