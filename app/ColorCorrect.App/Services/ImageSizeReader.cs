using System.Buffers.Binary;

namespace ColorCorrect.App.Services;

internal static class ImageSizeReader
{
    public static bool TryRead(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[24];
            var n = stream.Read(header);
            if (n >= 16 && header[..8].SequenceEqual(PreviewRgb.Magic))
            {
                width = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4));
                height = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12, 4));
                return width > 0 && height > 0;
            }

            if (n >= 24 && header[0] == 0x89 && header[1] == (byte)'P' && header[2] == (byte)'N' && header[3] == (byte)'G')
            {
                width = BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4));
                height = BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4));
                return width > 0 && height > 0;
            }

            if (n >= 2 && header[0] == 0xFF && header[1] == 0xD8)
            {
                if (StillImageDecoder.TrySize(path, out width, out height)) return true;
                stream.Position = 2;
                return TryReadJpeg(stream, out width, out height);
            }
        }
        catch
        {
            // Header parse is best-effort; callers fall back to the decoded bitmap.
        }

        return false;
    }

    private static bool TryReadJpeg(Stream stream, out int width, out int height)
    {
        width = 0;
        height = 0;
        Span<byte> two = stackalloc byte[2];
        while (stream.Read(two) == 2)
        {
            if (two[0] != 0xFF)
            {
                return false;
            }

            var marker = two[1];
            while (marker == 0xFF)
            {
                if (stream.Read(two.Slice(1, 1)) != 1)
                {
                    return false;
                }

                marker = two[1];
            }

            if (marker == 0xD8 || marker == 0xD9 || (marker >= 0xD0 && marker <= 0xD7))
            {
                continue;
            }

            if (stream.Read(two) != 2)
            {
                return false;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(two);
            if (length < 2)
            {
                return false;
            }

            var payload = length - 2;
            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                if (payload < 5)
                {
                    return false;
                }

                Span<byte> sof = stackalloc byte[5];
                if (stream.Read(sof) != 5)
                {
                    return false;
                }

                height = BinaryPrimitives.ReadUInt16BigEndian(sof.Slice(1, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(sof.Slice(3, 2));
                return width > 0 && height > 0;
            }

            stream.Seek(payload, SeekOrigin.Current);
        }

        return false;
    }
}
