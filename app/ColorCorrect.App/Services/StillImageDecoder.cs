using Avalonia.Media.Imaging;
using SkiaSharp;

namespace ColorCorrect.App.Services;

/// <summary>Use the same oriented, display-sRGB coordinate system as the worker.</summary>
internal static class StillImageDecoder
{
    internal static bool SwapsAxes(SKEncodedOrigin origin) =>
        origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    public static bool TrySize(string path, out int width, out int height)
    {
        width = height = 0;
        using var source = new MemoryStream(File.ReadAllBytes(path));
        using var codec = SKCodec.Create(source);
        if (codec is null) return false;
        width = SwapsAxes(codec.EncodedOrigin) ? codec.Info.Height : codec.Info.Width;
        height = SwapsAxes(codec.EncodedOrigin) ? codec.Info.Width : codec.Info.Height;
        return width > 0 && height > 0;
    }

    public static Bitmap Decode(string path, int maxWidth)
    {
        using var source = new MemoryStream(File.ReadAllBytes(path));
        using var codec = SKCodec.Create(source) ?? throw new InvalidDataException("Unsupported image");
        var swapped = SwapsAxes(codec.EncodedOrigin);
        var displayWidth = swapped ? codec.Info.Height : codec.Info.Width;
        var scale = Math.Min(1f, maxWidth / (float)displayWidth);
        var size = codec.GetScaledDimensions(scale);
        using var srgb = SKColorSpace.CreateSrgb();
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul, srgb);
        using var pixels = new SKBitmap(info);
        var result = codec.GetPixels(info, pixels.GetPixels());
        if (result != SKCodecResult.Success) throw new InvalidDataException($"Image decode failed: {result}");
        var w = size.Width;
        var h = size.Height;
        using var oriented = new SKBitmap(new SKImageInfo(swapped ? h : w, swapped ? w : h,
            SKColorType.Rgba8888, SKAlphaType.Premul, srgb));
        using (var canvas = new SKCanvas(oriented))
        {
            var matrix = codec.EncodedOrigin switch
            {
                SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
                SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
                SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
                SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
                SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
                SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
                SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
                _ => SKMatrix.Identity
            };
            canvas.SetMatrix(matrix);
            canvas.DrawBitmap(pixels, 0, 0);
        }
        using var resized = oriented.Width > maxWidth
            ? oriented.Resize(new SKImageInfo(maxWidth, Math.Max(1, oriented.Height * maxWidth / oriented.Width),
                SKColorType.Rgba8888, SKAlphaType.Premul, srgb), new SKSamplingOptions(SKFilterMode.Linear))
            : null;
        using var image = SKImage.FromBitmap(resized ?? oriented);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = encoded.AsStream();
        return new Bitmap(stream);
    }
}
