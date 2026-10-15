using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.Engine.Tests;

public sealed class ReportDisplayTests
{
    [Fact]
    public void DisplayColorsAreIndependentOfOutputPixelEncoding()
    {
        var report = CorrectionReport.Parse("""
            {"patches":[{"name":"gray",
            "measured_srgb":[0.214,0.214,0.214],"measured_display_srgb":[0.5,0.5,0.5],
            "reference_srgb":[0.4,0.4,0.4],"reference_display_srgb":[0.5,0.5,0.5],
            "corrected_srgb":[0.214,0.214,0.214],"corrected_display_srgb":[0.5,0.5,0.5]}]}
            """);
        var patch = Assert.Single(report!.Patches);
        Assert.Equal(new[] { .5, .5, .5 }, patch.Measured);
        Assert.Equal(new[] { .5, .5, .5 }, patch.Reference);
        Assert.Equal(new[] { .5, .5, .5 }, patch.Corrected);
    }

    [Fact]
    public void LegacyReportsRemainReadable()
    {
        var report = CorrectionReport.Parse("""
            {"patches":[{"name":"gray","measured_srgb":[0.1,0.2,0.3],
            "reference_srgb":[0.4,0.5,0.6],"corrected_srgb":[0.7,0.8,0.9]}]}
            """);
        var patch = Assert.Single(report!.Patches);
        Assert.Equal(new[] { .1, .2, .3 }, patch.Measured);
        Assert.Equal(new[] { .4, .5, .6 }, patch.Reference);
        Assert.Equal(new[] { .7, .8, .9 }, patch.Corrected);
    }
}
