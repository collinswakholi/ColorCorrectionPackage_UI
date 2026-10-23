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
        cancellationToken.ThrowIfCancellationRequested();
        // Capture before the first await: caller edits must not change a queued calibration.
        options = JsonSerializer.Deserialize<CorrectionOptions>(JsonSerializer.Serialize(options))!;
        foreach (var profile in options.ReferenceProfiles) profile.Validate();
        var prepared = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var run = RunPaths.NewRunDir(_repoRoot);
            OwnWhiteReference(options, run);
            cancellationToken.ThrowIfCancellationRequested();
            var chart = Path.Combine(run, "chart.json");
            handshake.WriteForCorrection(chart, faceChoice, useAllFaces, crop);
            cancellationToken.ThrowIfCancellationRequested();
            var preview = Path.Combine(run, "previews");
            Directory.CreateDirectory(preview);
            var identity = AssetIdentity.Capture(inputPath);
            cancellationToken.ThrowIfCancellationRequested();
            return (Run: run, Chart: chart, Preview: preview, Source: identity);
        }, cancellationToken).ConfigureAwait(false);
        var (runDir, chartPath, previewDir, source) = prepared;
        var outputPath = Path.Combine(runDir, "corrected" + options.OutputExtension());
        var reportPath = Path.Combine(runDir, "report.json");
        var modelPath = Path.Combine(runDir, "model.json");
        if (options.ReferenceProfiles.Count > 0 || options.ReferenceAssignments.Count > 0)
        {
            await _worker.RequireCapabilityAsync("custom_references_v1", cancellationToken).ConfigureAwait(false);
            await _worker.RequireCapabilityAsync("chart_identity_v1", cancellationToken).ConfigureAwait(false);
        }
        if (options.FastMode) await _worker.RequireCapabilityAsync("combined_lut65_v1", cancellationToken).ConfigureAwait(false);

        var payload = BaseRun(inputPath, outputPath, reportPath, modelPath, previewDir, options);
        payload["cmd"] = "run";
        payload["write_output"] = options.WriteOutput;
        payload["preview_mode"] = string.IsNullOrWhiteSpace(options.PreviewMode) ? "corrected" : options.PreviewMode;
        payload["handshake"] = chartPath;
        payload["crop"] = crop;
        payload["face"] = faceChoice;
        payload["use_all"] = useAllFaces;
        if (options.ReferenceProfiles.Count > 0)
        {
            payload["reference_profiles"] = options.ReferenceProfiles.Select(p => p.Snapshot()).ToArray();
            payload["reference_assignments"] = options.ReferenceAssignments;
        }
        if (!string.IsNullOrWhiteSpace(options.WhiteImagePath))
        {
            payload["white"] = options.WhiteImagePath;
        }
        if (!string.IsNullOrWhiteSpace(options.CropRect))
        {
            payload["crop_rect"] = options.CropRect;
        }

        return await RunTypedAsync(payload, runDir, outputPath, reportPath, modelPath, previewDir, hasMetrics: true, options, inputPath, source, cancellationToken)
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
        cancellationToken.ThrowIfCancellationRequested();
        options = JsonSerializer.Deserialize<CorrectionOptions>(JsonSerializer.Serialize(options))!;
        inputPaths = inputPaths.ToArray();
        if (inputPaths.Count == 0)
        {
            return [];
        }

        var prepared = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(modelPath)) return null;
            var run = RunPaths.NewRunDir(_repoRoot, inputPaths.Count > 1 ? "apply-batch" : "apply");
            // Use the owned model for the request itself, not only for later previews.
            var ownedModel = Path.Combine(run, "model.json");
            File.Copy(modelPath, ownedModel);
            cancellationToken.ThrowIfCancellationRequested();
            OwnWhiteReference(options, run);
            cancellationToken.ThrowIfCancellationRequested();
            var identities = new AssetIdentity?[inputPaths.Count];
            for (var index = 0; index < inputPaths.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { identities[index] = AssetIdentity.Capture(inputPaths[index]); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var destination = string.IsNullOrWhiteSpace(options.OutputDir)
                ? run
                : Path.IsPathRooted(options.OutputDir)
                    ? Path.GetFullPath(options.OutputDir)
                    : Path.GetFullPath(Path.Combine(_repoRoot, options.OutputDir));
            var preview = Path.Combine(run, "previews");
            Directory.CreateDirectory(destination);
            Directory.CreateDirectory(preview);
            return new PreparedApply(run, ownedModel, destination, preview, identities);
        }, cancellationToken).ConfigureAwait(false);
        if (prepared is null)
            return inputPaths.Select(_ => new CorrectionOutcome { Ok = false, Error = "Load or fit a model first." }).ToArray();
        var runDir = prepared.Run;
        modelPath = prepared.Model;
        var destDir = prepared.Destination;
        var previewRoot = prepared.Preview;
        var sourceIdentities = prepared.Sources;

        try
        {
            if (options.FastMode) await _worker.RequireCapabilityAsync("combined_lut65_v1", cancellationToken).ConfigureAwait(false);
            var payload = new Dictionary<string, object?>
            {
                ["cmd"] = "apply",
                ["cpu_lut"] = options.CpuLut,
                ["lut_device"] = options.LutDevice,
                ["fast_mode"] = options.FastMode,
                ["policy"] = _worker.Policy,
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
            var matchedRows = MatchApplyResults(inputPaths, result);

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
                if (matchedRows[inputIndex] is not { } row)
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
                if (ok && options.WriteOutput && !File.Exists(outputPath))
                {
                    ok = false;
                    error = "The worker did not create the requested corrected image.";
                }
                CompletedResultContext? context = null;
                if (ok)
                {
                    try { context = CompletedResultContext.Capture(sourceIdentities[inputIndex] ?? throw new IOException("The source image is unavailable."), modelPath, options); }
                    catch (Exception ex) { ok = false; error = ex.Message; }
                }
                outcomes.Add(new CorrectionOutcome
                {
                    CompletedContext = context,
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
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return inputPaths.Select(_ => new CorrectionOutcome { Ok = false, Error = ex.Message, RunDir = runDir, Stderr = ex.Message }).ToArray();
        }
    }

    private sealed record PreparedApply(string Run, string Model, string Destination, string Preview, AssetIdentity?[] Sources);

    public async Task<bool> PreviewStageAsync(
        string inputPath,
        string modelPath,
        string previewDir,
        string stage,
        CorrectionOptions options,
        CancellationToken cancellationToken = default)
    {
        options = JsonSerializer.Deserialize<CorrectionOptions>(JsonSerializer.Serialize(options))!;
        if (!File.Exists(modelPath) || string.IsNullOrWhiteSpace(previewDir))
        {
            return false;
        }

        Directory.CreateDirectory(previewDir);
        try
        {
            if (options.FastMode) await _worker.RequireCapabilityAsync("combined_lut65_v1", cancellationToken).ConfigureAwait(false);
            var payload = new Dictionary<string, object?>
            {
                ["cmd"] = "preview_stage",
                ["cpu_lut"] = options.CpuLut,
                ["lut_device"] = options.LutDevice,
                ["fast_mode"] = options.FastMode,
                ["policy"] = _worker.Policy,
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
            ["cpu_lut"] = options.CpuLut,
            ["lut_device"] = options.LutDevice,
                ["fast_mode"] = options.FastMode,
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
        AssetIdentity source,
        CancellationToken cancellationToken)
    {
        JsonElement result;
        try
        {
            result = await _worker.SendAsync(payload, TimeSpan.FromMinutes(8), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
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
        var ok = engineOk && (options.WriteOutput ? File.Exists(outputPath) : previewCorrected || File.Exists(modelPath));
        var report = CorrectionReport.TryLoad(reportPath);
        var error = ok ? "" : (result.TryGetProperty("error", out var err) ? err.GetString() ?? "" : "run failed");
        if (engineOk && options.WriteOutput && !File.Exists(outputPath)) error = "The worker did not create the requested corrected image.";
        var hasDelta = result.TryGetProperty("has_delta_e", out var de) && de.ValueKind == JsonValueKind.True && de.GetBoolean();
        CompletedResultContext? context = null;
        if (ok)
        {
            try { context = CompletedResultContext.Capture(source, modelPath, options); }
            catch (Exception ex) { ok = false; error = ex.Message; }
        }
        if (ok && options.WriteOutput && File.Exists(outputPath))
        {
            Publish(options.OutputDir, inputPath, outputPath, reportPath, modelPath);
        }

        return new CorrectionOutcome
        {
            CompletedContext = context,
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

    private static void OwnWhiteReference(CorrectionOptions options, string runDir)
    {
        if (!options.DoFfc) return;
        if (!File.Exists(options.WhiteImagePath)) throw new InvalidDataException("Choose a readable white reference before using FFC.");
        var owned = Path.Combine(runDir, "white-reference" + Path.GetExtension(options.WhiteImagePath));
        File.Copy(options.WhiteImagePath, owned);
        options.WhiteImagePath = owned;
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

    internal static IReadOnlyList<JsonElement?> MatchApplyResults(IReadOnlyList<string> inputPaths, JsonElement result, StringComparer? comparer = null)
    {
        // Preserve one result for every occurrence, including repeated inputs.
        // Case-distinct paths must remain distinct on case-sensitive hosts.
        comparer ??= OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var queues = new Dictionary<string, Queue<JsonElement>>(comparer);
        if (result.TryGetProperty("results", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in list.EnumerateArray())
            {
                var image = row.TryGetProperty("image", out var img) ? img.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(image)) continue;
                if (!queues.TryGetValue(image, out var queue)) queues[image] = queue = new();
                queue.Enqueue(row.Clone());
            }
        }
        return inputPaths.Select(path => queues.TryGetValue(path, out var queue) && queue.TryDequeue(out var row)
            ? (JsonElement?)row : null).ToArray();
    }

    public static IReadOnlyList<string> BuildArtifactBases(IReadOnlyList<string> inputPaths)
    {
        var stems = inputPaths
            .Select(path => string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(path))
                ? "out"
                : Path.GetFileNameWithoutExtension(path))
            .ToArray();
        var totals = stems
            .GroupBy(stem => stem, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var reserved = new HashSet<string>(stems, StringComparer.OrdinalIgnoreCase);
        var allocated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return stems.Select(stem =>
        {
            if (totals[stem] <= 1)
            {
                allocated.Add(stem);
                return stem;
            }

            occurrences.TryGetValue(stem, out var occurrence);
            string candidate;
            do { candidate = $"{stem}_{++occurrence}"; }
            while (reserved.Contains(candidate) || !allocated.Add(candidate));
            occurrences[stem] = occurrence;
            return candidate;
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
