using System.Globalization;
using System.Text.Json;

namespace ColorCorrect.Engine;

public readonly record struct Pt(double X, double Y);

public sealed record Quad(Pt A, Pt B, Pt C, Pt D)
{
    public IEnumerable<Pt> Points
    {
        get
        {
            yield return A;
            yield return B;
            yield return C;
            yield return D;
        }
    }

    public Quad ScaleAboutCenter(double factor)
    {
        if (!double.IsFinite(factor) || Math.Abs(factor - 1) < 1e-9)
        {
            return this;
        }

        var cx = (A.X + B.X + C.X + D.X) / 4;
        var cy = (A.Y + B.Y + C.Y + D.Y) / 4;
        return new Quad(Scale(A), Scale(B), Scale(C), Scale(D));

        Pt Scale(Pt p) => new(cx + (p.X - cx) * factor, cy + (p.Y - cy) * factor);
    }

    public static Quad? TryParse(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            return TryParse(el.GetString());
        }

        if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() != 4)
        {
            return null;
        }

        var pts = new Pt[4];
        var i = 0;
        foreach (var item in el.EnumerateArray())
        {
            if (!TryPoint(item, out var pt))
            {
                return null;
            }

            pts[i++] = pt;
        }

        return new Quad(pts[0], pts[1], pts[2], pts[3]);
    }

    public static Quad? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
        {
            return null;
        }

        var pts = new Pt[4];
        for (var i = 0; i < 4; i++)
        {
            var xy = parts[i].Split(',', StringSplitOptions.TrimEntries);
            if (xy.Length != 2 ||
                !double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                return null;
            }

            pts[i] = new Pt(x, y);
        }

        return new Quad(pts[0], pts[1], pts[2], pts[3]);
    }

    private static bool TryPoint(JsonElement el, out Pt pt)
    {
        pt = default;
        if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() >= 2)
        {
            pt = new Pt(el[0].GetDouble(), el[1].GetDouble());
            return true;
        }

        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty("x", out var x) &&
            el.TryGetProperty("y", out var y))
        {
            pt = new Pt(x.GetDouble(), y.GetDouble());
            return true;
        }

        return false;
    }
}
