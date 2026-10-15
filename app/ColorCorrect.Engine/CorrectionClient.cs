using System.Globalization;
using System.Text.Json;

namespace ColorCorrect.Engine;

public sealed class CorrectionClient
{
    private readonly SpeedyWorker _worker;
    private readonly string _repoRoot;

    public CorrectionClient(SpeedyWorker worker, string? repoRoot = null)
    {
        _worker = worker;
        _repoRoot = repoRoot ?? worker.RepoRoot;
    }

    public async Task<CorrectionOutcome> FitAsync(
        string inputPath,
        HandshakeDocument handshake,
        CorrectionOptions options,
        int faceChoice,
        bool useAllFaces,
        double crop,
        CancellationToken cancellationToken = default)
    {
        var runDir = RunPaths.NewRunDir(_repoRoot);
        var ext = options.OutputExtension();
        var outputPath = Path.Combine(runDir, "corrected" + ext);
        var reportPath = Path.Combine(runDir, "report.json");
        var modelPath = Path.Combine(runDir, "model.json");
        var chartPath = Path.Combine(runDir, "chart.json");
        var previewDir = Path.Combine(runDir, "previews");
        handshake.WriteForCorrection(chartPath, faceChoice, useAllFaces, crop);
        Directory.CreateDirectory(previewDir);

        var payload = BaseRun(inputPath, outputPath, reportPath, modelPath, previewDir, options);
        payload["cmd"] = "run";
        payload["write_output"] = options.WriteOutput;
        payload["preview_mode"] = string.IsNullOrWhiteSpace(options.PreviewMode) ? "corrected" : options.PreviewMode;
        payload["handshake"] = chartPath;
        payload["crop"] = crop;
        payload["face"] = faceChoice;
        payload["use_all"] = useAllFaces;
        if (!string.IsNullOrWhiteSpace(options.WhiteImagePath))
        {
            payload["white"] = options.WhiteImagePath;
        }
        if (!string.IsNullOrWhiteSpace(options.CropRect))
        {
            payload["crop_rect"] = options.CropRect;
        }

        return await RunTypedAsync(payload, runDir, outputPath, reportPath, modelPath, previewDir, hasMetrics: true, options, inputPath, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CorrectionOutcome> ApplyModelAsync(
        string inputPath,
        string modelPath,
        CorrectionOptions options,
        CancellationToken cancellationToken = default)
    {
        var list = await ApplyManyAsync([inputPath], modelPath, options, cancellationToken).ConfigureAwait(false);
        return list.Count > 0 ? list[0] : new CorrectionOutcome { Ok = false, Error = "apply failed" };
    }

    public async Task<IReadOnlyList<CorrectionOutcome>> ApplyManyAsync(
        IReadOnlyList<string> inputPaths,
        string modelPath,
        CorrectionOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(modelPath))
        {
            return inputPaths.Select(_ => new CorrectionOutcome { Ok = false, Error = "Load or fit a model first." }).ToArray();
        }

        if (inputPaths.Count == 0)
        {
            return [];
        }

        var runDir = RunPaths.NewRunDir(_repoRoot, inputPaths.Count > 1 ? "apply-batch" : "apply");
        var destDir = string.IsNullOrWhiteSpace(options.OutputDir)
            ? runDir
            : Path.IsPathRooted(options.OutputDir)
                ? Path.GetFullPath(options.OutputDir)
                : Path.GetFullPath(Path.Combine(_repoRoot, options.OutputDir));
        var previewRoot = Path.Combine(runDir, "previews");
        Directory.CreateDirectory(destDir);
        Directory.CreateDirectory(previewRoot);

        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["cmd"] = "apply",
                ["model"] = modelPath,
                ["images"] = inputPaths.ToArray(),
                ["output_dir"] = destDir,
                ["preview_dir"] = previewRoot,
                ["output_format"] = options.OutputFormat,
                ["write_output"] = options.WriteOutput,
                ["preview_mode"] = string.IsNullOrWhiteSpace(options.PreviewMode) ? "corrected" : options.PreviewMode,
                ["do_ffc"] = options.DoFfc,
                ["do_gc"] = options.DoGc,
                ["do_wb"] = options.DoWb,
                ["do_cc"] = options.DoCc,
                ["ffc"] = FfcPayload(options),
            };
            if (!string.IsNullOrWhiteSpace(options.WhiteImagePath))
            {
                payload["white"] = options.WhiteImagePath;
            }
            if (!string.IsNullOrWhiteSpace(options.CropRect))
            {
                payload["crop_rect"] = options.CropRect;
            }

            var timeout = TimeSpan.FromMinutes(Math.Clamp(8 + inputPaths.Count * 2, 8, 40));
            var result = await _worker.SendAsync(payload, timeout, cancellationToken).ConfigureAwait(false);
            var byPath = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (result.TryGetProperty("results", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in list.EnumerateArray())
                {
                    var image = row.TryGetProperty("image", out var img) ? img.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(image))
                    {
                        byPath[image] = row.Clone();
                    }
                }
            }

            var artifactBases = BuildArtifactBases(inputPaths);
            var outcomes = new List<CorrectionOutcome>(inputPaths.Count);
            for (var inputIndex = 0; inputIndex < inputPaths.Count; inputIndex++)
            {
                var inputPath = inputPaths[inputIndex];
                var artifactBase = artifactBases[inputIndex];
                var fallbackOut = Path.Combine(destDir, artifactBase + "_corrected" + options.OutputExtension());
                var fallbackPrev = inputPaths.Count > 1
                    ? Path.Combine(previewRoot, artifactBase)
                    : previewRoot;
                if (!byPath.TryGetValue(inputPath, out var row))
                {
                    outcomes.Add(new CorrectionOutcome
                    {
                        Ok = false,
                        Error = result.TryGetProperty("error", out var missing) ? missing.GetString() ?? "apply failed" : "apply failed",
                        ModelPath = modelPath,
                        RunDir = runDir,
                    });
                    continue;
                }

                var ok = row.TryGetProperty("ok", out var oneOk) && oneOk.GetBoolean();
                var outputPath = row.TryGetProperty("output", out var outEl) ? outEl.GetString() ?? fallbackOut : fallbackOut;
                var previewDir = row.TryGetProperty("preview_dir", out var prevEl) && prevEl.ValueKind == JsonValueKind.String
                    ? prevEl.GetString() ?? fallbackPrev
                    : fallbackPrev;
                var error = row.TryGetProperty("error", out var oneErr) ? oneErr.GetString() ?? "" : "";
                outcomes.Add(new CorrectionOutcome
                {
                    Ok = ok,
                    HasChartMetrics = false,
                    Error = ok ? "" : (string.IsNullOrWhiteSpace(error) ? "apply failed" : error),
                    OutputPath = ok ? outputPath : "",
                    ModelPath = modelPath,
                    RunDir = runDir,
                    PreviewDir = Directory.Exists(previewDir) ? previewDir : "",
                    FfcSkipped = JsonStr(row, "ffc_skipped"),
                    ApplyBackend = FirstNonEmpty(JsonStr(row, "apply_backend"), JsonStr(result, "apply_backend")),
                    PlaneBackend = FirstNonEmpty(JsonStr(row, "plane_backend"), JsonStr(result, "plane_backend")),
                });
            }

            return outcomes;
        }
        catch (Exception ex)
        {
            return inputPaths.Select(_ => new CorrectionOutcome { Ok = false, Error = ex.Message, RunDir = runDir, Stderr = ex.Message }).ToArray();
        }
    }

    public async Task<bool> PreviewStageAsync(
        string inputPath,
        string modelPath,
        string previewDir,
        string stage,
        CorrectionOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(modelPath) || string.IsNullOrWhiteSpace(previewDir))
        {
            return false;
        }

        Directory.CreateDirectory(previewDir);
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["cmd"] = "preview_stage",
                ["model"] = modelPath,
                ["image"] = inputPath,
                ["preview_dir"] = previewDir,
                ["stage"] = stage,
                ["write_output"] = false,
                ["preview_mode"] = stage,
                ["do_ffc"] = options.DoFfc,
                ["do_gc"] = options.DoGc,
                ["do_wb"] = options.DoWb,
                ["do_cc"] = options.DoCc,
                ["ffc"] = FfcPayload(options),
            };
            if (!string.IsNullOrWhiteSpace(options.WhiteImagePath))
            {
                payload["white"] = options.WhiteImagePath;
            }
            if (!string.IsNullOrWhiteSpace(options.CropRect))
            {
                payload["crop_rect"] = options.CropRect;
            }

            var result = await _worker.SendAsync(payload, TimeSpan.FromMinutes(8), cancellationToken).ConfigureAwait(false);
            return result.TryGetProperty("ok", out var okEl) && okEl.ValueKind != JsonValueKind.False && okEl.GetBoolean();
        }
        catch
        {
            return false;
        }
    }

    private Dictionary<string, object?> BaseRun(
        string inputPath,
        string outputPath,
        string reportPath,
        string modelPath,
        string previewDir,
        CorrectionOptions options)
    {
        return new Dictionary<string, object?>
        {
            ["policy"] = _worker.Policy,
            ["image"] = inputPath,
            ["output"] = outputPath,
            ["report"] = reportPath,
            ["model_out"] = modelPath,
            ["preview_dir"] = previewDir,
            ["write_output"] = options.WriteOutput,
            ["preview_mode"] = string.IsNullOrWhiteSpace(options.PreviewMode) ? "corrected" : options.PreviewMode,
            ["model"] = options.CliModel(),
            ["input_space"] = options.InputSpace,
            ["input_white"] = options.InputWhite,
            ["target_space"] = options.TargetSpace,
            ["target_white"] = options.TargetWhite,
            ["fit_samples"] = options.FitSamples,
            ["include_combinations"] = options.IncludeCombinations,
            ["do_ffc"] = options.DoFfc,
            ["do_sat"] = !options.SkipSaturated,
            ["do_gc"] = options.DoGc,
            ["do_wb"] = options.DoWb,
            ["do_cc"] = options.DoCc,
            ["skip_saturated"] = options.SkipSaturated,
            ["phase2_teacher"] = options.Phase2Teacher,
            ["ffc"] = FfcPayload(options),
            ["clip_lo"] = options.ClipLo,
            ["clip_hi"] = options.ClipHi,
            ["pls_components"] = options.PlsComponents,
            ["hidden_size"] = options.HiddenSize,
            ["hidden_layers"] = options.HiddenLayers,
            ["nn_optimizer"] = options.NnOptimizer,
            ["nn_batch_norm"] = options.NnBatchNormalization,
            ["nn_dropout"] = options.NnDropout,
            ["nn_lr"] = options.NnLr,
            ["nn_epochs"] = options.NnEpochs,
            ["nn_patience"] = options.NnPatience,
            ["nn_tol"] = options.NnTol,
            ["tol"] = options.NnTol,
            ["poly_degree"] = options.Degree,
        };
    }

    private async Task<CorrectionOutcome> RunTypedAsync(
        Dictionary<string, object?> payload,
        string runDir,
        string outputPath,
        string reportPath,
        string modelPath,
        string previewDir,
        bool hasMetrics,
        CorrectionOptions options,
        string inputPath,
        CancellationToken cancellationToken)
    {
        JsonElement result;
        try
        {
            result = await _worker.SendAsync(payload, TimeSpan.FromMinutes(8), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new CorrectionOutcome { Ok = false, Error = ex.Message, RunDir = runDir, Stderr = ex.Message };
        }

        if (result.TryGetProperty("output", out var outEl) && outEl.ValueKind == JsonValueKind.String)
        {
            outputPath = outEl.GetString() ?? outputPath;
        }
        if (result.TryGetProperty("report", out var repEl) && repEl.ValueKind == JsonValueKind.String)
        {
            reportPath = repEl.GetString() ?? reportPath;
        }
        if (result.TryGetProperty("model_out", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
        {
            modelPath = modelEl.GetString() ?? modelPath;
        }
        if (result.TryGetProperty("preview_dir", out var prevEl) && prevEl.ValueKind == JsonValueKind.String)
        {
            previewDir = prevEl.GetString() ?? previewDir;
        }

        var previewCorrected = PreviewExists(previewDir, "corrected");
        var engineOk = result.TryGetProperty("ok", out var okEl) && okEl.ValueKind != JsonValueKind.False && okEl.GetBoolean();
        var ok = engineOk && (File.Exists(outputPath) || previewCorrected || File.Exists(modelPath));
        if (ok && options.WriteOutput && File.Exists(outputPath))
        {
            Publish(options.OutputDir, inputPath, outputPath, reportPath, modelPath);
        }

        var report = CorrectionReport.TryLoad(reportPath);
        var error = ok ? "" : (result.TryGetProperty("error", out var err) ? err.GetString() ?? "" : "run failed");
        var hasDelta = result.TryGetProperty("has_delta_e", out var de) && de.ValueKind == JsonValueKind.True && de.GetBoolean();

        return new CorrectionOutcome
        {
            Ok = ok,
            HasChartMetrics = hasMetrics && hasDelta && report?.HasMetrics == true,
            Error = error,
            OutputPath = ok ? outputPath : "",
            ReportPath = File.Exists(reportPath) ? reportPath : "",
            ModelPath = File.Exists(modelPath) ? modelPath : "",
            RunDir = runDir,
            PreviewDir = Directory.Exists(previewDir) ? previewDir : "",
            Stderr = _worker.FormatRecentStderr(),
            FfcSkipped = JsonStr(result, "ffc_skipped"),
            ApplyBackend = JsonStr(result, "apply_backend"),
            PlaneBackend = JsonStr(result, "plane_backend"),
            Report = report,
        };
    }

    private static bool PreviewExists(string previewDir, string stem) =>
        File.Exists(Path.Combine(previewDir, stem + ".rgb"))
        || File.Exists(Path.Combine(previewDir, stem + ".png"))
        || File.Exists(Path.Combine(previewDir, stem + ".jpg"));

    private static string JsonStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? ""
            : "";

    private static string FirstNonEmpty(string a, string b) =>
        string.IsNullOrWhiteSpace(a) ? b : a;

    internal static IReadOnlyList<string> BuildArtifactBases(IReadOnlyList<string> inputPaths)
    {
        var stems = inputPaths
            .Select(path => string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(path))
                ? "out"
                : Path.GetFileNameWithoutExtension(path))
            .ToArray();
        var totals = stems
            .GroupBy(stem => stem, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return stems.Select(stem =>
        {
            if (totals[stem] <= 1)
            {
                return stem;
            }

            occurrences.TryGetValue(stem, out var occurrence);
            occurrence++;
            occurrences[stem] = occurrence;
            return $"{stem}_{occurrence}";
        }).ToArray();
    }

    private void Publish(string outputDir, string inputPath, string outputPath, string reportPath, string modelPath)
    {
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return;
        }

        var dest = Path.IsPathRooted(outputDir) ? outputDir : Path.GetFullPath(Path.Combine(_repoRoot, outputDir));
        Directory.CreateDirectory(dest);
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        RunPaths.CopyIfNeeded(outputPath, dest, stem + "_corrected" + Path.GetExtension(outputPath));
        if (File.Exists(reportPath))
        {
            RunPaths.CopyIfNeeded(reportPath, dest, stem + "_report.json");
        }

        if (File.Exists(modelPath))
        {
            RunPaths.CopyIfNeeded(modelPath, dest, stem + "_model.json");
        }
    }

    private static Dictionary<string, object?> FfcPayload(CorrectionOptions options) => new()
    {
        ["method"] = options.FfcFitMethod,
        ["bins"] = options.FfcBins,
        ["smooth_window"] = options.FfcSmoothWindow,
        ["degree"] = options.FfcDegree,
        ["nn_max_iter"] = options.FfcNnMaxIter,
        ["tol"] = options.FfcTol,
        ["cut_off"] = options.FfcCutOff,
        ["max_val"] = options.FfcMaxVal,
    };
}
