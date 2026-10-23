using ColorCorrect.Engine;
using System.Text.Json;
using Xunit;

namespace ColorCorrect.Engine.Tests;

public sealed class ReliabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ccp-engine-tests-{Guid.NewGuid():N}");

    public ReliabilityTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void PackagedResourceRootWinsOverAnUnrelatedSourceWorkingDirectory()
    {
        var package = Path.Combine(_root, "package-Δ");
        var source = Path.Combine(_root, "source");
        foreach (var path in new[] { package, source })
        {
            Directory.CreateDirectory(Path.Combine(path, "models", "opt2"));
            Directory.CreateDirectory(Path.Combine(path, "refs"));
            File.WriteAllText(Path.Combine(path, "refs", "neutrals.json"), "{}");
        }
        var previousRoot = Environment.GetEnvironmentVariable("CCP_NEW_UI_ROOT");
        var previousDirectory = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(source);
            Environment.SetEnvironmentVariable("CCP_NEW_UI_ROOT", package);
            Assert.True(EnginePaths.TryFindRepoRoot(out var root));
            Assert.Equal(package, root);
            Assert.Equal(package, new SpeedyWorker(exePath: "not-started").RepoRoot);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
            Environment.SetEnvironmentVariable("CCP_NEW_UI_ROOT", previousRoot);
        }
    }

    [Fact]
    public void SessionWrite_IsAtomicAndRoundTrips()
    {
        var path = Path.Combine(_root, "session.json");
        var state = new SessionState { Theme = "Light", Crop = 0.42 };

        Assert.True(SessionStore.Write(path, state));
        var (loaded, error) = SessionStore.TryLoadFrom(path);

        Assert.Null(error);
        Assert.Equal("Light", loaded.Theme);
        Assert.Equal(0.42, loaded.Crop, 6);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void SessionWrite_ReportsFailureInsteadOfFalseSuccess()
    {
        var directoryTarget = Path.Combine(_root, "not-a-file");
        Directory.CreateDirectory(directoryTarget);

        Assert.False(SessionStore.Write(directoryTarget, new SessionState()));
    }

    [Fact]
    public void DuplicateImageStems_GetDistinctArtifactNamesAndSelectedFormat()
    {
        var names = CorrectionClient.BuildArtifactBases([
            "shoot-a/IMG_001.jpg",
            "shoot-b/img_001.png",
            "shoot-b/unique.tif",
        ]);

        Assert.Equal(["IMG_001_1", "img_001_2", "unique"], names);
        Assert.Equal(".png", new CorrectionOptions { OutputFormat = "png" }.OutputExtension());
        Assert.Equal(".jpg", new CorrectionOptions { OutputFormat = "jpg" }.OutputExtension());
    }

    [Fact]
    public void GeneratedArtifactSuffixes_DoNotOverwriteLiteralStemsOrDuplicateInputs()
    {
        (string[] Images, string[] Expected)[] cases = [
            (["a.jpg", "a.png", "a_1.jpg"], ["a_2", "a_3", "a_1"]),
            (["a_1.jpg", "a.jpg", "a.png"], ["a_1", "a_2", "a_3"]),
            (["a.jpg", "a.png", "A_1.jpg", "a_1.png", "a_1_1.jpg"], ["a_2", "a_3", "A_1_2", "a_1_3", "a_1_1"]),
            (["a.jpg", "a.jpg", "a_1.jpg", "a_2.jpg"], ["a_3", "a_4", "a_1", "a_2"]),
            (["Ä.jpg", "ä.png", "Ä_1.jpg"], ["Ä_2", "ä_3", "Ä_1"]),
        ];
        foreach (var (images, expected) in cases)
        {
            var names = CorrectionClient.BuildArtifactBases(images);
            Assert.Equal(expected, names);
            Assert.Equal(images.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BatchReplyMatchingPreservesEveryOccurrenceAndPlatformPathComparison(bool caseSensitive)
    {
        using var reply = JsonDocument.Parse("""
            {"results":[{"image":"a.jpg","output":"first.png"},{"image":"A.jpg","output":"upper.png"},{"image":"a.jpg","output":"repeat.png"}]}
            """);
        var rows = CorrectionClient.MatchApplyResults(["A.jpg", "a.jpg", "a.jpg", "missing.jpg"], reply.RootElement,
            caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        Assert.Equal(caseSensitive ? "upper.png" : "first.png", rows[0]!.Value.GetProperty("output").GetString());
        Assert.Equal(caseSensitive ? "first.png" : "upper.png", rows[1]!.Value.GetProperty("output").GetString());
        Assert.Equal("repeat.png", rows[2]!.Value.GetProperty("output").GetString());
        Assert.Null(rows[3]);
    }

    [Theory]
    [InlineData("100", "100")]
    [InlineData("100, 30", "[100, 30]")]
    [InlineData("[64, 32]", "[64, 32]")]
    [InlineData("[128, 64, 32]", "[128, 64, 32]")]
    public void HiddenLayerParser_AcceptsScalarAndArraySyntax(string text, string canonical)
    {
        Assert.True(CorrectionOptions.TryParseHiddenLayers(text, out var layers, out var error), error);
        Assert.Equal(canonical, CorrectionOptions.FormatHiddenLayers(layers));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[64, nope]")]
    [InlineData("[3, 32]")]
    [InlineData("[64, 64, 64, 64, 64]")]
    public void HiddenLayerParser_RejectsAmbiguousOrUnsafeArchitectures(string text)
    {
        Assert.False(CorrectionOptions.TryParseHiddenLayers(text, out var layers, out var error));
        Assert.Empty(layers);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void NeuralNetworkOptions_NormalizeLegacySettingsAndRoundTripNewControls()
    {
        var legacy = new CorrectionOptions { HiddenSize = 64, HiddenLayers = [] }.Normalize();
        Assert.Equal([64], legacy.HiddenLayers);

        var path = Path.Combine(_root, "nn-session.json");
        var options = new CorrectionOptions
        {
            Method = "nn",
            HiddenLayers = [128, 64, 32],
            NnOptimizer = "rmsprop",
            NnBatchNormalization = true,
            NnDropout = 0.25,
        }.Normalize();
        Assert.True(SessionStore.Write(path, new SessionState { Options = options }));
        var (loaded, error) = SessionStore.TryLoadFrom(path);

        Assert.Null(error);
        Assert.Equal([128, 64, 32], loaded.Options.HiddenLayers);
        Assert.Equal("rmsprop", loaded.Options.NnOptimizer);
        Assert.True(loaded.Options.NnBatchNormalization);
        Assert.Equal(0.25, loaded.Options.NnDropout, 6);
    }

    [Fact]
    public void SafeDefaultsAndValidationReport_AreExplicit()
    {
        Assert.Equal("poly", new CorrectionOptions().FfcFitMethod);
        var report = CorrectionReport.Parse("""
        {
          "model": "nn",
          "delta_e_2000": {
            "mean_before": 7.0,
            "mean_after": 0.4,
            "median_before": 6.0,
            "median_after": 0.3,
            "max_before": 14.0,
            "max_after": 2.0
          },
          "validation_delta_e_2000": {
            "folds": 5,
            "samples": 24,
            "mean": 2.8,
            "median": 2.6,
            "max": 7.2
          },
          "fit_warnings": ["Held-out error indicates overfit."]
        }
        """);

        Assert.NotNull(report);
        Assert.Equal(5, report.ValidationFolds);
        Assert.Equal(2.8, report.ValidationMean, 6);
        Assert.Contains("CC-only", report.StatusLine(), StringComparison.OrdinalIgnoreCase);
        Assert.Single(report.FitWarnings);
    }

    [Fact]
    public void ExecutableDiscovery_WalksFromNestedBuildFolderToRepoRoot()
    {
        var oldCwd = Directory.GetCurrentDirectory();
        var oldEngine = Environment.GetEnvironmentVariable("CCP_ENGINE_EXE");
        var oldLegacy = Environment.GetEnvironmentVariable("COLORCORRECT_EXE");
        try
        {
            Directory.CreateDirectory(Path.Combine(_root, "models", "opt2"));
            Directory.CreateDirectory(Path.Combine(_root, "refs"));
            File.WriteAllText(Path.Combine(_root, "refs", "neutrals.json"), "{}");
            var engineDir = Path.Combine(_root, "engine", "target", "release");
            Directory.CreateDirectory(engineDir);
            var engine = Path.Combine(engineDir, OperatingSystem.IsWindows() ? "ccp-engine.exe" : "ccp-engine");
            File.WriteAllBytes(engine, []);
            var nested = Path.Combine(_root, "app", "ColorCorrect.App", "bin", "Release", "net9.0");
            Directory.CreateDirectory(nested);
            Directory.SetCurrentDirectory(nested);
            Environment.SetEnvironmentVariable("CCP_ENGINE_EXE", null);
            Environment.SetEnvironmentVariable("COLORCORRECT_EXE", null);

            Assert.True(EnginePaths.TryFindExecutable(out var found));
            Assert.Equal(Path.GetFullPath(engine), Path.GetFullPath(found));
        }
        finally
        {
            Directory.SetCurrentDirectory(oldCwd);
            Environment.SetEnvironmentVariable("CCP_ENGINE_EXE", oldEngine);
            Environment.SetEnvironmentVariable("COLORCORRECT_EXE", oldLegacy);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Test cleanup must not hide the assertion result.
        }
    }
}
