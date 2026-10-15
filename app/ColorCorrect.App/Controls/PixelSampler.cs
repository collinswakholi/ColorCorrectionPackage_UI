using System;
using System.Buffers;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ColorCorrect.App.Controls;

public readonly record struct PickedColor(int X, int Y, byte R, byte G, byte B, double L, double A, double LabB)
{
    public string Pixel => $"{X}, {Y}";
    public string Hex => $"#{R:x2}{G:x2}{B:x2}";
    public string Rgb => $"RGB  {R}, {G}, {B}  {Hex}";
    public string Lab => $"Lab  {L:0.00}, {A:0.00}, {LabB:0.00}";
    public string CopyLine => $"{X}, {Y}  RGB {R}, {G}, {B}  {Hex}  Lab {L:0.00}, {A:0.00}, {LabB:0.00}";
    public Color Color => Color.FromRgb(R, G, B);
}

public static class PixelSampler
{
    [ThreadStatic] private static byte[]? t_pixel;

    public static bool TrySample(Bitmap bitmap, int x, int y, out PickedColor picked)
    {
        picked = default;
        var size = bitmap.PixelSize;
        if (x < 0 || y < 0 || x >= size.Width || y >= size.Height)
        {
            return false;
        }

        t_pixel ??= new byte[4];
        var handle = GCHandle.Alloc(t_pixel, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), 4, 4);
            var b = t_pixel[0];
            var g = t_pixel[1];
            var r = t_pixel[2];
            var (l, a, labB) = SrgbToLab(r, g, b);
            picked = new PickedColor(x, y, r, g, b, l, a, labB);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            handle.Free();
        }
    }

    public static Color[] EmptyNeighborhood(int radius)
    {
        var cells = radius * 2 + 1;
        var colors = new Color[cells * cells];
        Array.Fill(colors, Color.FromRgb(18, 18, 18));
        return colors;
    }

    public static Color[] SampleNeighborhood(Bitmap bitmap, int cx, int cy, int radius)
    {
        var cells = radius * 2 + 1;
        var colors = EmptyNeighborhood(radius);
        var size = bitmap.PixelSize;
        var x0 = cx - radius;
        var y0 = cy - radius;
        var srcX = Math.Max(0, x0);
        var srcY = Math.Max(0, y0);
        var srcW = Math.Min(size.Width, x0 + cells) - srcX;
        var srcH = Math.Min(size.Height, y0 + cells) - srcY;
        if (srcW <= 0 || srcH <= 0)
        {
            return colors;
        }

        var stride = srcW * 4;
        var length = stride * srcH;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(srcX, srcY, srcW, srcH), handle.AddrOfPinnedObject(), length, stride);
            for (var row = 0; row < srcH; row++)
            {
                for (var col = 0; col < srcW; col++)
                {
                    var offset = (row * srcW + col) * 4;
                    var dest = (srcY + row - y0) * cells + (srcX + col - x0);
                    colors[dest] = Color.FromRgb(buffer[offset + 2], buffer[offset + 1], buffer[offset]);
                }
            }
        }
        catch
        {
            // Keep the fallback neighborhood if the bitmap cannot be sampled.
        }
        finally
        {
            handle.Free();
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return colors;
    }

    public static (double L, double A, double B) SrgbToLab(byte r, byte g, byte b)
    {
        var rl = SrgbToLinear(r / 255.0);
        var gl = SrgbToLinear(g / 255.0);
        var bl = SrgbToLinear(b / 255.0);
        var x = rl * 0.4124564 + gl * 0.3575761 + bl * 0.1804375;
        var y = rl * 0.2126729 + gl * 0.7151522 + bl * 0.0721750;
        var z = rl * 0.0193339 + gl * 0.1191920 + bl * 0.9503041;
        var fx = LabF(x / 0.95047);
        var fy = LabF(y / 1.00000);
        var fz = LabF(z / 1.08883);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static double SrgbToLinear(double c) =>
        c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static double LabF(double t) =>
        t > 0.008856 ? Math.Pow(t, 1.0 / 3.0) : (7.787 * t) + (16.0 / 116.0);
}
