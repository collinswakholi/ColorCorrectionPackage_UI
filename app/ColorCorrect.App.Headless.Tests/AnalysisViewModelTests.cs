using Avalonia.Headless.XUnit;
using ColorCorrect.App.ViewModels;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class AnalysisViewModelTests
{
    [AvaloniaFact]
    public void ApplyFillsTilesRowsAndSummaries()
    {
        var analysis = new AnalysisViewModel();
        var report = new CorrectionReport
        {
            HasMetrics = true, Model = "rpcc2", Process = "FFC poly", FeatureCount = 6,
            MeanBefore = 7.1, MeanAfter = 1.43, MedianBefore = 7.28, MedianAfter = 1.26, MaxBefore = 11.88, MaxAfter = 4.0,
            ValidationMean = double.NaN,
            Stages = [new StageDeltaE("CC", 7.1, 1.43, 7.28, 1.26, 11.88, 4.0, 80, 1)],
        };
        analysis.Apply(report, hasCorrected: true, () => "unused");

        Assert.True(analysis.HasMetrics);
        Assert.Same(report, analysis.Report);
        Assert.Equal(("1.43", "1.26", "4.00"), (analysis.MeanAfter, analysis.MedianAfter, analysis.MaxAfter));
        Assert.Equal("was 7.10", analysis.MeanBefore);
        Assert.Equal(1.43, analysis.MeanAfterValue);
        Assert.Equal("rpcc2", analysis.ModelMetric);
        Assert.Equal("FFC poly · 6 feat", analysis.FeatureDetail);
        Assert.Equal("Training fit shown · validation unavailable", analysis.ValidationSummary);
        Assert.Single(analysis.StageDeltaERows);
        Assert.Equal("0 patches", analysis.PatchSummary);
    }

    [AvaloniaFact]
    public void ModelApplyWithoutChartAndClearingResetTheTiles()
    {
        var analysis = new AnalysisViewModel();
        analysis.Apply(new CorrectionReport { HasMetrics = true, MeanAfter = 2 }, hasCorrected: true, () => "ccm");
        Assert.Equal("ccm", analysis.ModelMetric); // report without a model name uses the default

        analysis.Apply(null, hasCorrected: true, () => "ccm");
        Assert.False(analysis.HasMetrics);
        Assert.Equal("Model apply · no chart ΔE", analysis.FeatureDetail);
        Assert.Equal(("--", "--", "--"), (analysis.MeanAfter, analysis.MedianAfter, analysis.MaxAfter));
        Assert.True(double.IsNaN(analysis.MaxAfterValue));
        Assert.Empty(analysis.StageDeltaERows);
        Assert.Equal("", analysis.StatusDeltaE);
    }

    [AvaloniaFact]
    public void ShellMirrorsSessionStateIntoThePanelGuidance()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        vm.HasSession = true;
        Assert.True(vm.Analysis.HasSession);
        Assert.StartsWith("Detect the chart", vm.Analysis.DeltaEPlaceholder);
        vm.HasSession = false;
        Assert.StartsWith("Add a chart still", vm.Analysis.DeltaEPlaceholder);
    }
}
