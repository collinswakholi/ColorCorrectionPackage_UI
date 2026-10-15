using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace ColorCorrect.App.Services;

internal static class BitmapExport
{
    public static void Save(Bitmap bitmap, string path, int jpegQuality = 92)
    {
        var codec = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
            ".png" => SKEncodedImageFormat.Png,
            _ => throw new ArgumentException("Image export requires a PNG or JPEG filename.", nameof(path)),
        };
        using var pixels = ReadPixels(bitmap);
        using var image = SKImage.FromBitmap(pixels);
        using var encoded = image.Encode(codec, codec == SKEncodedImageFormat.Jpeg ? jpegQuality : 100)
            ?? throw new InvalidDataException("Image encoding failed.");
        using var output = File.Create(path);
        encoded.SaveTo(output);
    }

    private static SKBitmap ReadPixels(Bitmap bitmap)
    {
        var color = bitmap.Format == PixelFormat.Bgra8888 ? SKColorType.Bgra8888
            : bitmap.Format == PixelFormat.Rgba8888 ? SKColorType.Rgba8888 : SKColorType.Unknown;
        if (color == SKColorType.Unknown)
        {
            // A less common platform format can still use Avalonia's PNG bridge.
            using var png = new MemoryStream();
            bitmap.Save(png);
            png.Position = 0;
            return SKBitmap.Decode(png) ?? throw new InvalidDataException("Bitmap readback failed.");
        }
        var pixels = new SKBitmap(new SKImageInfo(bitmap.PixelSize.Width, bitmap.PixelSize.Height,
            color, SKAlphaType.Premul));
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height),
                pixels.GetPixels(), pixels.ByteCount, pixels.RowBytes);
            return pixels;
        }
        catch
        {
            pixels.Dispose();
            throw;
        }
    }
}
