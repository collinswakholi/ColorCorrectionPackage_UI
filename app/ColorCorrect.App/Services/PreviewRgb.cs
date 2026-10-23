using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ColorCorrect.App.Services;

internal static class PreviewRgb
{
    public static ReadOnlySpan<byte> Magic => "CCPRGB8\n"u8;

    public static bool TryReadHeader(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return TryReadHeader(stream, out width, out height);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryReadHeader(Stream stream, out int width, out int height)
    {
        width = 0;
        height = 0;
        Span<byte> header = stackalloc byte[16];
        var n = stream.Read(header);
        if (n < 16 || !header[..8].SequenceEqual(Magic))
        {
            return false;
        }

        width = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4));
        height = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12, 4));
        return width > 0 && height > 0;
    }

    public static Bitmap? TryLoad(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (!TryReadHeader(stream, out var width, out var height))
            {
                return null;
            }

            var rgb = new byte[checked(width * height * 3)];
            var read = 0;
            while (read < rgb.Length)
            {
                var n = stream.Read(rgb, read, rgb.Length - read);
                if (n <= 0)
                {
                    return null;
                }

                read += n;
            }

            return FromRgb(width, height, rgb);
        }
        catch
        {
            return null;
        }
    }

    public static Bitmap FromRgb(int width, int height, byte[] rgb)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
        using var fb = bitmap.Lock();
        var dst = new byte[fb.RowBytes * height];
        for (var y = 0; y < height; y++)
        {
            var srcRow = y * width * 3;
            var dstRow = y * fb.RowBytes;
            for (var x = 0; x < width; x++)
            {
                var si = srcRow + x * 3;
                var di = dstRow + x * 4;
                dst[di] = rgb[si + 2];
                dst[di + 1] = rgb[si + 1];
                dst[di + 2] = rgb[si];
                dst[di + 3] = 255;
            }
        }

        Marshal.Copy(dst, 0, fb.Address, dst.Length);
        return bitmap;
    }

    public static void WriteFromBitmap(string path, Bitmap bitmap)
    {
        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var bgra = new byte[checked(width * height * 4)];
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), bgra.Length, width * 4);
        }
        finally
        {
            handle.Free();
        }

        var rgb = new byte[checked(width * height * 3)];
        for (var i = 0; i < width * height; i++)
        {
            rgb[i * 3] = bgra[i * 4 + 2];
            rgb[i * 3 + 1] = bgra[i * 4 + 1];
            rgb[i * 3 + 2] = bgra[i * 4];
        }

        Write(path, width, height, rgb);
    }

    public static void Write(string path, int width, int height, byte[] rgb)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        using var stream = File.Create(path);
        stream.Write(Magic);
        Span<byte> wh = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(wh, (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(wh.Slice(4), (uint)height);
        stream.Write(wh);
        stream.Write(rgb);
    }
}
