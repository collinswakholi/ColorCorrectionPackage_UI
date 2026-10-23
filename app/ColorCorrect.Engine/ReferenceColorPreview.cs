namespace ColorCorrect.Engine;

/// <summary>Display-only RGB conversion. Never used as fitting or metric truth.</summary>
public static class ReferenceColorPreview
{
    private static readonly double[] Bradford = [0.8951, 0.2664, -0.1614, -0.7502, 1.7135, 0.0367, 0.0389, -0.0685, 1.0296];
    public static double[] DisplaySrgb(ReferenceProfile profile, double[] values)
    {
        var sourceWhite = White(profile.WhitePoint);
        double[] xyz;
        if (profile.ColorSpace == "lab")
        {
            var fy = (values[0] + 16) / 116;
            double F(double v) => v > 6.0 / 29 ? v * v * v : (116 * v - 16) / (24389.0 / 27);
            xyz = [F(fy + values[1] / 500) * sourceWhite[0], F(fy), F(fy - values[2] / 200) * sourceWhite[2]];
        }
        else if (profile.ColorSpace == "xyz") xyz = values.Select(v => v / profile.Scale).ToArray();
        else
        {
            var linear = values.Select(v => Decode(v / profile.Scale, profile.ColorSpace)).ToArray();
            xyz = Mul(Matrix(profile.ColorSpace, sourceWhite), linear);
        }
        var targetWhite = White("d65");
        var a = Mul(Bradford, sourceWhite); var b = Mul(Bradford, targetWhite); var cone = Mul(Bradford, xyz);
        xyz = Mul(Inverse(Bradford), Enumerable.Range(0, 3).Select(i => cone[i] * b[i] / a[i]).ToArray());
        var rgb = Mul(Inverse(Matrix("srgb", targetWhite)), xyz);
        return rgb.Select(v => Math.Clamp(v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(Math.Max(0, v), 1 / 2.4) - 0.055, 0, 1)).ToArray();
    }
    private static double Decode(double v, string space) => space switch
    {
        "linear-srgb" or "linear-rec2020" or "acescg" => v,
        "adobe-rgb-1998" => Math.Pow(v, 563.0 / 256),
        "prophoto-rgb" => v <= 16.0 / 512 ? v / 16 : Math.Pow(v, 1.8),
        "rec2020" => v < 0.0812428582986315 ? v / 4.5 : Math.Pow((v + 0.099296826809442) / 1.099296826809442, 1 / 0.45),
        _ => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4),
    };
    private static double[] White(string id)
    {
        var (x, y) = id switch
        {
            "d50" => (0.34567, 0.35850), "d55" => (0.33242, 0.34743), "d60" => (0.32168, 0.33767),
            "d75" => (0.29902, 0.31485), "a" => (0.44757, 0.40745), "c" => (0.31006, 0.31616),
            "e" => (1.0 / 3, 1.0 / 3), _ => (0.31271, 0.32902),
        };
        return [x / y, 1, (1 - x - y) / y];
    }
    private static double[] Matrix(string id, double[] white)
    {
        double[] xy = id switch
        {
            "display-p3" => [0.68, 0.32, 0.265, 0.69, 0.15, 0.06],
            "adobe-rgb-1998" => [0.64, 0.33, 0.21, 0.71, 0.15, 0.06],
            "rec2020" or "linear-rec2020" => [0.708, 0.292, 0.17, 0.797, 0.131, 0.046],
            "prophoto-rgb" => [0.7347, 0.2653, 0.1596, 0.8404, 0.0366, 0.0001],
            "acescg" => [0.713, 0.293, 0.165, 0.830, 0.128, 0.044],
            _ => [0.64, 0.33, 0.30, 0.60, 0.15, 0.06],
        };
        var m = new double[9];
        for (var c = 0; c < 3; c++) { m[c] = xy[c * 2] / xy[c * 2 + 1]; m[3 + c] = 1; m[6 + c] = (1 - xy[c * 2] - xy[c * 2 + 1]) / xy[c * 2 + 1]; }
        var scale = Mul(Inverse(m), white);
        for (var r = 0; r < 3; r++) for (var c = 0; c < 3; c++) m[r * 3 + c] *= scale[c];
        return m;
    }
    private static double[] Mul(double[] m, double[] v) => Enumerable.Range(0, 3).Select(r => m[r * 3] * v[0] + m[r * 3 + 1] * v[1] + m[r * 3 + 2] * v[2]).ToArray();
    private static double[] Inverse(double[] m)
    {
        var a = m[0]; var b = m[1]; var c = m[2]; var d = m[3]; var e = m[4]; var f = m[5]; var g = m[6]; var h = m[7]; var i = m[8];
        var det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        return new[] { e * i - f * h, c * h - b * i, b * f - c * e, f * g - d * i, a * i - c * g, c * d - a * f, d * h - e * g, b * g - a * h, a * e - b * d }.Select(v => v / det).ToArray();
    }
}
