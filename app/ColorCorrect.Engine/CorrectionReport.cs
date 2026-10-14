using System.Text.Json;

namespace ColorCorrect.Engine;

public sealed class CorrectionReport
{
    public bool HasMetrics { get; init; }
    public string Model { get; init; } = "";
    public string Process { get; init; } = "";
    public bool DoFfc { get; init; }
    public bool DoGc { get; init; }
    public bool DoWb { get; init; }
    public bool DoCc { get; init; }
    public string FfcMethod { get; init; } = "";
    public int FeatureCount { get; init; }
    public int SaturatedPatches { get; init; }
    public double MeanBefore { get; init; }
    public double MeanAfter { get; init; }
    public double MedianBefore { get; init; }
    public double MedianAfter { get; init; }
    public double MaxBefore { get; init; }
    public double MaxAfter { get; init; }
    public int ValidationFolds { get; init; }
    public int ValidationSamples { get; init; }
    public double ValidationMean { get; init; }
    public double ValidationMedian { get; init; }
    public double ValidationMax { get; init; }
    public IReadOnlyList<string> FitWarnings { get; init; } = [];
    public IReadOnlyList<PatchRow> Patches { get; init; } = [];
    public IReadOnlyList<StageDeltaE> Stages { get; init; } = [];
    public FfcPlane? FfcPlane { get; init; }

    public IReadOnlyList<StageDeltaE> EffectiveStages
    {
        get
        {
            if (Stages.Count > 0)
            {
                return Stages;
            }

            if (!HasMetrics)
            {
                return [];
            }

            var improve = MeanBefore > 1e-12 && double.IsFinite(MeanBefore) && double.IsFinite(MeanAfter)
                ? (MeanBefore - MeanAfter) / MeanBefore * 100.0
                : 0;
            return
            [
                new StageDeltaE(
                    "Corrected",
                    MeanBefore,
                    MeanAfter,
                    MedianBefore,
                    MedianAfter,
                    MaxBefore,
                    MaxAfter,
                    improve,
                    double.NaN),
            ];
        }
    }

    public string QualityLabel(double after)
    {
        if (!double.IsFinite(after)) return "";
        if (after < 1) return "Excellent";
        if (after < 2) return "Very good";
        if (after < 3.5) return "Good";
        if (after < 5) return "Fair";
        return "Poor";
    }

    public string StatusLine()
    {
        if (!HasMetrics) return "";
        var fit = $"fit ΔE {MeanAfter:0.00}";
        return double.IsFinite(ValidationMean)
            ? $"{fit} · {ValidationFolds}-fold held-out ΔE {ValidationMean:0.00}"
            : $"{fit} · median {MedianAfter:0.00} · max {MaxAfter:0.00}";
    }

    public static CorrectionReport? TryLoad(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    internal static CorrectionReport? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var delta = root.TryGetProperty("delta_e_2000", out var d) ? d : default;
        var validation = root.TryGetProperty("validation_delta_e_2000", out var cv) ? cv : default;
        var patches = new List<PatchRow>();
        var stages = new List<StageDeltaE>();
        if (root.TryGetProperty("patches", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in arr.EnumerateArray())
            {
                patches.Add(new PatchRow(
                    GetStr(p, "name"),
                    GetNum(p, "before_delta_e"),
                    GetNum(p, "after_delta_e"),
                    p.TryGetProperty("saturated", out var sat) && sat.ValueKind == JsonValueKind.True,
                    ReadDisplayRgb(p, "measured"),
                    ReadDisplayRgb(p, "reference"),
                    ReadDisplayRgb(p, "corrected")));
            }
        }

        if (root.TryGetProperty("stages", out var stageArr) && stageArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in stageArr.EnumerateArray())
            {
                var before = GetNum(s, "mean_before");
                var after = GetNum(s, "mean_after");
                var improve = GetNum(s, "improvement_percent");
                if (!double.IsFinite(improve) && before > 1e-12 && double.IsFinite(before) && double.IsFinite(after))
                {
                    improve = (before - after) / before * 100.0;
                }

                stages.Add(new StageDeltaE(
                    GetStr(s, "name"),
                    before,
                    after,
                    GetNum(s, "median_before"),
                    GetNum(s, "median_after"),
                    GetNum(s, "max_before"),
                    GetNum(s, "max_after"),
                    improve,
                    GetNum(s, "elapsed_secs")));
            }
        }

        return new CorrectionReport
        {
            HasMetrics = delta.ValueKind == JsonValueKind.Object
                         && double.IsFinite(GetNum(delta, "mean_after")),
            Model = GetStr(root, "model"),
            Process = GetStr(root, "process"),
            DoFfc = GetFlag(root, "do_ffc"),
            DoGc = GetFlag(root, "do_gc"),
            DoWb = GetFlag(root, "do_wb"),
            DoCc = GetFlag(root, "do_cc"),
            FfcMethod = GetStr(root, "ffc_method"),
            FeatureCount = root.TryGetProperty("feature_count", out var fc) && fc.TryGetInt32(out var nFc) ? nFc : 0,
            SaturatedPatches = root.TryGetProperty("saturated_patches", out var sp) && sp.TryGetInt32(out var nSp) ? nSp : 0,
            MeanBefore = GetNum(delta, "mean_before"),
            MeanAfter = GetNum(delta, "mean_after"),
            MedianBefore = GetNum(delta, "median_before"),
            MedianAfter = GetNum(delta, "median_after"),
            MaxBefore = GetNum(delta, "max_before"),
            MaxAfter = GetNum(delta, "max_after"),
            ValidationFolds = validation.ValueKind == JsonValueKind.Object && validation.TryGetProperty("folds", out var folds) && folds.TryGetInt32(out var nFolds) ? nFolds : 0,
            ValidationSamples = validation.ValueKind == JsonValueKind.Object && validation.TryGetProperty("samples", out var samples) && samples.TryGetInt32(out var nSamples) ? nSamples : 0,
            ValidationMean = GetNum(validation, "mean"),
            ValidationMedian = GetNum(validation, "median"),
            ValidationMax = GetNum(validation, "max"),
            FitWarnings = root.TryGetProperty("fit_warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array
                ? warnings.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? "").ToArray()
                : [],
            Patches = patches,
            Stages = stages,
            FfcPlane = FfcPlane.TryParse(root),
        };
    }

    private static bool GetFlag(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string GetStr(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static double GetNum(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v))
        {
            return double.NaN;
        }

        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : double.NaN;
    }

    private static double[] ReadRgb(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return arr.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.Number && x.TryGetDouble(out var n) ? n : 0)
            .Take(3)
            .ToArray();
    }

    private static double[] ReadDisplayRgb(JsonElement el, string role)
    {
        // New workers provide display sRGB independently of numeric output encoding.
        // Fall back for old session reports that predate the explicit display fields.
        var display = ReadRgb(el, role + "_display_srgb");
        return display.Length == 3 ? display : ReadRgb(el, role + "_srgb");
    }
}

public sealed record StageDeltaE(
    string Name,
    double MeanBefore,
    double MeanAfter,
    double MedianBefore,
    double MedianAfter,
    double MaxBefore,
    double MaxAfter,
    double ImprovementPercent,
    double ElapsedSecs);

public sealed record PatchRow(
    string Name,
    double Before,
    double After,
    bool Saturated,
    double[] Measured,
    double[] Reference,
    double[] Corrected);
