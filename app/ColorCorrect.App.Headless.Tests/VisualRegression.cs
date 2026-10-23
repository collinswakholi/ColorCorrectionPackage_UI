using Avalonia.Controls;
using Avalonia.Headless;
using SkiaSharp;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

internal static class VisualRegression
{
    private const byte ChannelThreshold = 18;
    private const double MaxMeanChannelError = 3.0;
    private const double MaxDifferentPixelFraction = 0.035;

    public static void Match(Window window, string snapshotName)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        using var actualPng = new MemoryStream();
        frame.Save(actualPng);
        actualPng.Position = 0;
        using var actual = SKBitmap.Decode(actualPng);
        Assert.NotNull(actual);
        Assert.True(HasMeaningfulContent(actual), "Rendered frame is blank or nearly transparent.");

        var platform = OperatingSystem.IsWindows()
            ? "win"
            : OperatingSystem.IsMacOS() ? "mac" : "linux";
        var fileName = $"{snapshotName}-{platform}.png";
        // Retain the actual frame even on mismatch so a reference is never refreshed blind.
        var reviewDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestResults", "UI"));
        Directory.CreateDirectory(reviewDirectory);
        frame.Save(Path.Combine(reviewDirectory, fileName));
        var sourceDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Baselines"));
        var sourcePath = Path.Combine(sourceDirectory, fileName);
        var deployedPath = Path.Combine(AppContext.BaseDirectory, "Baselines", fileName);

        if (string.Equals(Environment.GetEnvironmentVariable("CCP_UPDATE_UI_BASELINES"), "1", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(sourceDirectory);
            frame.Save(sourcePath);
            return;
        }

        // A baseline is deliberately platform-specific. Structural assertions still run on a
        // newly supported OS until its reviewed reference image is added.
        var baselinePath = File.Exists(deployedPath) ? deployedPath : sourcePath;
        if (!File.Exists(baselinePath))
        {
            var missing = $"SKIPPED visual comparison: missing reviewed baseline {fileName}; actual frame retained in {reviewDirectory}.";
            Console.WriteLine(missing);
            File.AppendAllText(Path.Combine(reviewDirectory, "missing-baselines.log"), missing + Environment.NewLine);
            Assert.False(Environment.GetEnvironmentVariable("CCP_REQUIRE_UI_BASELINES") == "1", missing);
            return;
        }

        using var expected = SKBitmap.Decode(baselinePath);
        Assert.NotNull(expected);
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);

        var expectedPixels = expected.Pixels;
        var actualPixels = actual.Pixels;
        long totalChannelError = 0;
        var differentPixels = 0;
        for (var i = 0; i < expectedPixels.Length; i++)
        {
            var e = expectedPixels[i];
            var a = actualPixels[i];
            var db = Math.Abs(e.Blue - a.Blue);
            var dg = Math.Abs(e.Green - a.Green);
            var dr = Math.Abs(e.Red - a.Red);
            var da = Math.Abs(e.Alpha - a.Alpha);
            totalChannelError += db + dg + dr + da;
            if (Math.Max(Math.Max(db, dg), Math.Max(dr, da)) > ChannelThreshold)
            {
                differentPixels++;
            }
        }

        var meanChannelError = totalChannelError / (expectedPixels.Length * 4.0);
        var differentFraction = differentPixels / (double)expectedPixels.Length;
        Assert.True(
            meanChannelError <= MaxMeanChannelError && differentFraction <= MaxDifferentPixelFraction,
            $"Visual regression for {fileName}: mean channel error {meanChannelError:0.###}, " +
            $"different pixels {differentFraction:P2}. Update only after reviewing the new frame.");
    }

    private static bool HasMeaningfulContent(SKBitmap bitmap)
    {
        var pixels = bitmap.Pixels;
        if (pixels.Length == 0)
        {
            return false;
        }

        var opaque = 0;
        var minLuma = 255;
        var maxLuma = 0;
        foreach (var pixel in pixels)
        {
            if (pixel.Alpha > 240)
            {
                opaque++;
            }

            var luma = (pixel.Red * 54 + pixel.Green * 183 + pixel.Blue * 19) >> 8;
            minLuma = Math.Min(minLuma, luma);
            maxLuma = Math.Max(maxLuma, luma);
        }

        return opaque >= pixels.Length * 0.95 && maxLuma - minLuma >= 40;
    }
}
