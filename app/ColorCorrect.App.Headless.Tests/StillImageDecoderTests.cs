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
