namespace ColorCorrect.Engine;

public static class ColorSpaces
{
    public static readonly (string Id, string Label)[] RgbSpaces =
    [
        ("srgb", "sRGB"),
        ("linear-srgb", "Linear sRGB"),
        ("display-p3", "Display P3"),
        ("adobe-rgb-1998", "Adobe RGB 1998"),
        ("rec2020", "Rec. 2020"),
        ("linear-rec2020", "Linear Rec. 2020"),
        ("prophoto-rgb", "ProPhoto RGB"),
        ("acescg", "ACEScg"),
    ];

    public static readonly (string Id, string Label)[] WhitePoints =
    [
        ("d50", "D50"),
        ("d55", "D55"),
        ("d60", "D60"),
        ("d65", "D65"),
        ("d75", "D75"),
        ("a", "A"),
        ("c", "C"),
        ("e", "E"),
    ];

    public static readonly (string Id, string Label)[] Methods =
    [
        ("rpcc", "Root polynomial"),
        ("ccm", "3x3 CCM"),
        ("linear", "Polynomial regression"),
        ("pls", "PLS"),
        ("nn", "Neural net"),
    ];

    public static readonly (string Id, string Label)[] FfcFitMethods =
    [
        ("linear", "Linear"),
        ("poly", "Polynomial"),
        ("pls", "PLS"),
        ("nn", "Neural net"),
    ];

    public static string FfcMethodLabel(string? id)
    {
        var key = (id ?? "linear").Trim().ToLowerInvariant();
        foreach (var (methodId, label) in FfcFitMethods)
        {
            if (methodId == key)
            {
                return label;
            }
        }

        return string.IsNullOrWhiteSpace(id) ? "Linear" : id;
    }

    public static string ChartLabel(string? type)
    {
        return type switch
        {
            "classic" or "classic_4x6" => "Classic 24",
            "spyder" or "spyder_4x6" => "SpyderCHECKR 24",
            "digital-sg" or "digital_sg_10x14" => "Digital SG 140",
            "passport" => "Passport / Classic 24",
            null or "" => "chart",
            _ => type,
        };
    }
}
