using Avalonia.Media.Imaging;
using SkiaSharp;

namespace ColorCorrect.App.Services;

/// <summary>Use the same oriented, display-sRGB coordinate system as the worker.</summary>
internal static class StillImageDecoder
{
    private static MemoryStream ReadSnapshot(string path)
    {
        // Preview jobs may finish after a session item is removed. Allow deletion
        // or atomic replacement while reading the original handle, but not writes
        // into that file. Decode from a detached snapshot once the read completes.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var snapshot = new MemoryStream(checked((int)file.Length));
        try
        {
            file.CopyTo(snapshot);
            snapshot.Position = 0;
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
    }

    internal static bool SwapsAxes(SKEncodedOrigin origin) =>
        origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    public static bool TrySize(string path, out int width, out int height)
    {
        width = height = 0;
        using var source = ReadSnapshot(path);
        using var codec = SKCodec.Create(source);
        if (codec is null) return false;
        width = SwapsAxes(codec.EncodedOrigin) ? codec.Info.Height : codec.Info.Width;
        height = SwapsAxes(codec.EncodedOrigin) ? codec.Info.Width : codec.Info.Height;
        return width > 0 && height > 0;
    }

    /// <summary>
    /// Limits concurrent thumbnail decodes so adding hundreds of large stills does not
    /// decode them all at once (each decode holds the file and a full-size pixel buffer).
    /// </summary>
    public static readonly SemaphoreSlim ThumbnailGate = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 4));

    /// <summary>Decode, downscale to <paramref name="maxWidth"/> display pixels, then apply EXIF orientation.</summary>
    public static Bitmap Decode(string path, int maxWidth)
    {
        using var source = ReadSnapshot(path);
        using var codec = SKCodec.Create(source) ?? throw new InvalidDataException("Unsupported image");
        var swapped = SwapsAxes(codec.EncodedOrigin);
        var displayWidth = swapped ? codec.Info.Height : codec.Info.Width;
        var scale = Math.Min(1f, maxWidth / (float)displayWidth);
        // JPEG can decode at a reduced size directly; other formats decode at full size.
        var size = codec.GetScaledDimensions(scale);
        using var srgb = SKColorSpace.CreateSrgb();
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul, srgb);
        using var pixels = new SKBitmap(info);
        var result = codec.GetPixels(info, pixels.GetPixels());
        if (result != SKCodecResult.Success) throw new InvalidDataException($"Image decode failed: {result}");

        // Resize before orienting so the rotation copy is thumbnail sized, not full size.
        // Target dimensions match the former orient-then-resize path exactly.
        var w = size.Width;
        var h = size.Height;
        var orientedWidth = swapped ? h : w;
        using var resized = orientedWidth > maxWidth
            ? pixels.Resize(swapped
                    ? new SKImageInfo(Math.Max(1, w * maxWidth / h), maxWidth, SKColorType.Rgba8888, SKAlphaType.Premul, srgb)
                    : new SKImageInfo(maxWidth, Math.Max(1, h * maxWidth / w), SKColorType.Rgba8888, SKAlphaType.Premul, srgb),
                new SKSamplingOptions(SKFilterMode.Linear))
            : null;
        var small = resized ?? pixels;
        w = small.Width;
        h = small.Height;

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
            canvas.DrawBitmap(small, 0, 0);
        }

        // Hand the pixels to Avalonia directly; the former PNG encode/decode round trip
        // cost a full compression pass per image.
        return new Bitmap(Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Premul,
            oriented.GetPixels(), new Avalonia.PixelSize(oriented.Width, oriented.Height),
            new Avalonia.Vector(96, 96), oriented.RowBytes);
    }
}
