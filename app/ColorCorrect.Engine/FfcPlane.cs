using System.Text.Json;

namespace ColorCorrect.Engine;

/// Crop used when FFC fitted a gain map on the white reference.
public sealed class FfcPlane
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int WhiteWidth { get; init; }
    public int WhiteHeight { get; init; }
    public string Source { get; init; } = "";

    public bool IsValid => Width > 0 && Height > 0;

    public string Badge => Source switch
    {
        "detected" => "FFC plane",
        "crop_rect" => "FFC plane · manual",
        "full" => "FFC plane · full frame",
        _ => "FFC plane",
    };

    public (double X, double Y, double W, double H) MapToSource(int sourceW, int sourceH)
    {
        var ww = Math.Max(1, WhiteWidth > 0 ? WhiteWidth : sourceW);
        var wh = Math.Max(1, WhiteHeight > 0 ? WhiteHeight : sourceH);
        var sx = sourceW / (double)ww;
        var sy = sourceH / (double)wh;
        return (X * sx, Y * sy, Width * sx, Height * sy);
    }

    public static FfcPlane? TryLoad(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return TryParse(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    public static FfcPlane? TryParse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !TryReadRect(root, "ffc_plane", out var x, out var y, out var w, out var h))
        {
            return null;
        }

        var ww = 0;
        var wh = 0;
        if (root.TryGetProperty("ffc_white_size", out var size) && size.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var n in size.EnumerateArray())
            {
                if (n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var v))
                {
                    if (i == 0) ww = v;
                    if (i == 1) wh = v;
                }

                i++;
            }
        }

        var source = root.TryGetProperty("ffc_plane_source", out var src) && src.ValueKind == JsonValueKind.String
            ? src.GetString() ?? ""
            : "";
        return new FfcPlane
        {
            X = x,
            Y = y,
            Width = w,
            Height = h,
            WhiteWidth = ww,
            WhiteHeight = wh,
            Source = source,
        };
    }

    private static bool TryReadRect(JsonElement root, string name, out int x, out int y, out int w, out int h)
    {
        x = y = w = h = 0;
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var nums = new List<int>(4);
        foreach (var n in arr.EnumerateArray())
        {
            if (n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var v))
            {
                nums.Add(v);
            }
        }

        if (nums.Count < 4 || nums[2] <= 0 || nums[3] <= 0)
        {
            return false;
        }

        x = nums[0];
        y = nums[1];
        w = nums[2];
        h = nums[3];
        return true;
    }
}
