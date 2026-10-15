using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.Engine.Tests;

public sealed class ReliabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ccp-engine-tests-{Guid.NewGuid():N}");

    public ReliabilityTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void DataDirectoryOverride_IsIsolatedAndDefaultIsPreserved()
    {
        var previous = Environment.GetEnvironmentVariable("COLORCORRECT_DATA_DIR");
        try
        {
            var isolated = Path.Combine(_root, "isolated-profile");
            Environment.SetEnvironmentVariable("COLORCORRECT_DATA_DIR", isolated);
            Assert.Equal(isolated, SessionStore.AppDataDir());
            Assert.Equal(Path.Combine(isolated, "settings.json"), SessionStore.SettingsPath());
            Assert.True(SessionStore.SaveSettings(new SessionState { Theme = "Light" }));
            Assert.Equal("Light", SessionStore.TryLoadSettings().State.Theme);
            Environment.SetEnvironmentVariable("COLORCORRECT_DATA_DIR", null);
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CCP-New"), SessionStore.AppDataDir());
        }
        finally { Environment.SetEnvironmentVariable("COLORCORRECT_DATA_DIR", previous); }
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

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"Options\":null}", false)]
    [InlineData("{\"Options\":{\"Method\":\"nn\"}}", true)]
    public void SessionOptions_DistinguishSavedWorkflowFromOlderImageOnlyFiles(string json, bool hasOptions)
    {
        var path = Path.Combine(_root, "optional-options.json");
        File.WriteAllText(path, json);
        var (loaded, error) = SessionStore.TryLoadFrom(path);
        Assert.Null(error);
        Assert.NotNull(loaded.Options);
        Assert.Equal(hasOptions, loaded.HasOptions);
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
        Assert.Contains("held-out", report.StatusLine(), StringComparison.OrdinalIgnoreCase);
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
            var nested = Path.Combine(_root, "app", "ColorCorrect.App", "bin", "Release", "net10.0");
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

    [Fact]
    public void LibTorchDiscovery_UsesBundledRuntimeButHonorsExplicitOverride()
    {
        var oldRoot = Environment.GetEnvironmentVariable("CCP_LIBTORCH_ROOT");
        var oldLegacy = Environment.GetEnvironmentVariable("LIBTORCH_PATH");
        try
        {
            Environment.SetEnvironmentVariable("CCP_LIBTORCH_ROOT", null);
            Environment.SetEnvironmentVariable("LIBTORCH_PATH", null);
            var appDir = Path.Combine(_root, "portable-app");
            var bundled = Path.Combine(appDir, "libtorch");
            Directory.CreateDirectory(bundled);
            File.WriteAllText(Path.Combine(bundled, "libtorch.json"), "{\"kind\":\"cpu\"}");

            var addon = Path.Combine(_root, "cuda-addon");
            Assert.Equal(bundled, EnginePaths.LibTorchRoot(appDir, addon));

            var nestedApp = Path.Combine(appDir, "app");
            Directory.CreateDirectory(nestedApp);
            Assert.Equal(bundled, EnginePaths.LibTorchRoot(nestedApp, addon));
            Directory.CreateDirectory(Path.Combine(addon, "lib"));
            File.WriteAllText(Path.Combine(addon, "lib", "torch_cuda.dll"), "fixture");
            File.WriteAllText(Path.Combine(addon, "libtorch.json"), "{\"kind\":\"cuda\",\"version\":\"2.9.1\"}");
            Assert.Equal(addon, EnginePaths.LibTorchRoot(nestedApp, addon));

            var custom = Path.Combine(_root, "custom-libtorch");
            Environment.SetEnvironmentVariable("CCP_LIBTORCH_ROOT", custom);
            Assert.Equal(custom, EnginePaths.LibTorchRoot(appDir, addon));
            Assert.NotEqual(custom, LibTorchCudaInstaller.DestRoot());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CCP_LIBTORCH_ROOT", oldRoot);
            Environment.SetEnvironmentVariable("LIBTORCH_PATH", oldLegacy);
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
