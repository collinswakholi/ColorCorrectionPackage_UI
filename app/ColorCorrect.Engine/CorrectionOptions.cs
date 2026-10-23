namespace ColorCorrect.Engine;

public sealed class CorrectionOptions
{
    /// Use CPU for LUT application without changing the GPU inference provider.
    public bool CpuLut { get; set; }
    /// Image-correction device; chart/plane detection has its own GPU provider.
    public string LutDevice { get; set; } = "auto";
    public bool FastMode { get; set; } = true;
    public bool BatchPreviewResults { get; set; } = true;
    public List<ReferenceProfile> ReferenceProfiles { get; set; } = [];
    public List<ChartReferenceAssignment> ReferenceAssignments { get; set; } = [];
    public string Method { get; set; } = "rpcc";
    public int Degree { get; set; } = 2;
    public bool IncludeCombinations { get; set; } = true;
    public int FitSamples { get; set; } = 1;
    public bool SkipSaturated { get; set; }
    public string InputSpace { get; set; } = "srgb";
    public string InputWhite { get; set; } = "d65";
    public string TargetSpace { get; set; } = "srgb";
    public string TargetWhite { get; set; } = "d65";
    public string OutputFormat { get; set; } = "jpg";
    public string OutputDir { get; set; } = "";
    public int PlsComponents { get; set; }
    /// <summary>Legacy first hidden-layer width. Kept for older saved sessions and workers.</summary>
    public int HiddenSize { get; set; } = 100;
    public List<int> HiddenLayers { get; set; } = [];
    public string NnOptimizer { get; set; } = "adam";
    public bool NnBatchNormalization { get; set; }
    public double NnDropout { get; set; }
    public double NnLr { get; set; } = 0.001;
    public int NnEpochs { get; set; } = 10000;
    public int NnPatience { get; set; } = 1500;
    /// CCP_rs `cc_kwargs.tol`. NN early-stop and PLS NIPALS. Default 1e-6.
    public double NnTol { get; set; } = 1e-6;
    public string ModelInPath { get; set; } = "";
    public string LoadedModelPath { get; set; } = "";
    public bool? UseLoadedModel { get; set; }
    public bool DoFfc { get; set; }
    public bool DoGc { get; set; } = true;
    public bool DoWb { get; set; } = true;
    public bool DoCc { get; set; } = true;
    public string WhiteImagePath { get; set; } = "";
    public string CropRect { get; set; } = "";
    public string FfcFitMethod { get; set; } = "poly";
    public int FfcBins { get; set; } = 50;
    public int FfcSmoothWindow { get; set; } = 5;
    public int FfcDegree { get; set; } = 3;
    public int FfcNnMaxIter { get; set; } = 10000;
    /// CCP_rs `ffc_kwargs.tol`. FFC NN early-stop and PLS NIPALS. Default 1e-8.
    public double FfcTol { get; set; } = 1e-8;
    /// CCP_rs FFC `transform_extremity` cut-off. Default 1.3.
    public double FfcCutOff { get; set; } = 1.3;
    /// CCP_rs FFC `transform_extremity` ceiling. Default 1.8.
    public double FfcMaxVal { get; set; } = 1.8;
    /// CCP_rs sat-check working floor for recovered RGB. Default -0.2.
    public double ClipLo { get; set; } = -0.2;
    /// CCP_rs sat-check working ceiling for recovered RGB. Default 1.3.
    public double ClipHi { get; set; } = 1.3;
    public bool Phase2Teacher { get; set; } = true;
    /// Full-res JPEG/PNG. Interactive Fit/Apply and Batch leave this false; Save materializes.
    public bool WriteOutput { get; set; }
    /// Engine preview set: none | corrected | all | ffc | gc | wb | cc
    public string PreviewMode { get; set; } = "corrected";

    public CorrectionOptions Normalize()
    {
        if (Method is "rpcc3" or "finlayson" or "conv" or "root-polynomial-3")
        {
            Degree = Math.Max(Degree, 3);
        }

        Method = CanonicalMethod(Method);
        HiddenLayers = NormalizeHiddenLayers(HiddenLayers, HiddenSize);
        HiddenSize = HiddenLayers[0];
        NnOptimizer = CanonicalOptimizer(NnOptimizer);
        NnDropout = double.IsFinite(NnDropout) ? Math.Clamp(NnDropout, 0.0, 0.8) : 0.0;
        return this;
    }

    public static bool TryParseHiddenLayers(string? text, out List<int> layers, out string error)
    {
        layers = [];
        error = "";
        var value = (text ?? "").Trim();
        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            value = value[1..^1].Trim();
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Enter one to four layer widths, for example 100 or [128, 64, 32].";
            return false;
        }

        var tokens = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 1 or > 4)
        {
            error = "Use between one and four hidden layers.";
            return false;
        }

        foreach (var token in tokens)
        {
            if (!int.TryParse(token, out var width) || width is < 4 or > 512)
            {
                error = $"“{token}” is invalid. Each layer must be an integer from 4 to 512.";
                layers.Clear();
                return false;
            }

            layers.Add(width);
        }

        return true;
    }

    public static string FormatHiddenLayers(IEnumerable<int>? layers)
    {
        var normalized = NormalizeHiddenLayers(layers, 100);
        return normalized.Count == 1 ? normalized[0].ToString() : $"[{string.Join(", ", normalized)}]";
    }

    public static string CanonicalOptimizer(string? optimizer) => optimizer?.Trim().ToLowerInvariant() switch
    {
        "sgd" or "momentum" => "sgd",
        "rmsprop" or "rms" => "rmsprop",
        _ => "adam",
    };

    public static string CanonicalMethod(string? method) => method switch
    {
        "pls" => "pls",
        "nn" or "mlp" => "nn",
        "ccm" or "3x3" => "ccm",
        "rpcc" or "rpcc2" or "rpcc3" or "finlayson" or "conv"
            or "root-polynomial-2" or "root-polynomial-3" => "rpcc",
        _ => "linear",
    };

    public string CliModel()
    {
        return CanonicalMethod(Method) switch
        {
            "ccm" => "ccm",
            "rpcc" => Degree >= 3 ? "rpcc3" : "rpcc2",
            "pls" => "pls",
            "nn" => "nn",
            _ => "linear",
        };
    }

    public string Summary()
    {
        return CanonicalMethod(Method) switch
        {
            "ccm" => "3x3 CCM",
            "rpcc" => $"Root polynomial · degree {Math.Clamp(Degree, 2, 3)}",
            "pls" => $"PLS · degree {Degree}{ComboSuffix()} · k={PlsComponents}",
            "nn" => $"Neural net · degree {Degree}{ComboSuffix()} · h={FormatHiddenLayers(HiddenLayers)} · {CanonicalOptimizer(NnOptimizer).ToUpperInvariant()}" +
                    (NnBatchNormalization ? " · BN" : "") +
                    (NnDropout > 0 ? $" · drop {NnDropout:0.##}" : ""),
            _ => $"Linear · degree {Degree}{ComboSuffix()}",
        };
    }

    public string OutputExtension() => OutputFormat.Equals("jpg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png";

    private string ComboSuffix() => IncludeCombinations ? "" : " · no combinations";

    private static List<int> NormalizeHiddenLayers(IEnumerable<int>? layers, int fallback)
    {
        var result = (layers ?? [])
            .Take(4)
            .Select(width => Math.Clamp(width, 4, 512))
            .ToList();
        if (result.Count == 0)
        {
            result.Add(Math.Clamp(fallback <= 0 ? 100 : fallback, 4, 512));
        }

        return result;
    }
}
