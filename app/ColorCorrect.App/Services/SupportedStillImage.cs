namespace ColorCorrect.App.Services;

/// <summary>Restrict imports to supported still encodings, including renamed-file checks.</summary>
internal static class SupportedStillImage
{
    public static bool IsSupportedFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        var extension = Path.GetExtension(path);
        if (!(extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
              || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
              || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
              || extension.Equals(".tif", StringComparison.OrdinalIgnoreCase)
              || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[8];
            var length = stream.Read(header);
            var jpeg = length >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff;
            var png = length >= 8 && header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            var tiff = length >= 4 &&
                ((header[0] == (byte)'I' && header[1] == (byte)'I' && header[2] == 42 && header[3] == 0)
                 || (header[0] == (byte)'M' && header[1] == (byte)'M' && header[2] == 0 && header[3] == 42));
            return jpeg || png || tiff;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
