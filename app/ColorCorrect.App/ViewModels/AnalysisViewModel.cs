using System;
using System.Collections.ObjectModel;
using ColorCorrect.Engine;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ColorCorrect.App.ViewModels;

/// <summary>
/// The bottom analysis panels: ΔE summary tiles, per-stage and per-patch rows, the report
/// behind the plots, the selected tab and the empty / fitting / failed panel states.
/// </summary>
public partial class AnalysisViewModel : ViewModelBase
{
    [ObservableProperty] private CorrectionReport? _report;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeltaEPlaceholder), nameof(ColorPlaceholder), nameof(RenditionPlaceholder))]
    private bool _hasMetrics;

    [ObservableProperty] private string _meanAfter = "--";
    [ObservableProperty] private string _meanBefore = "--";
    [ObservableProperty] private string _meanQuality = "";
    [ObservableProperty] private string _medianAfter = "--";
    [ObservableProperty] private string _medianBefore = "--";
    [ObservableProperty] private string _medianQuality = "";
    [ObservableProperty] private string _maxAfter = "--";
    [ObservableProperty] private string _maxBefore = "--";
    [ObservableProperty] private string _maxQuality = "";
    [ObservableProperty] private double _meanAfterValue = double.NaN;
    [ObservableProperty] private double _medianAfterValue = double.NaN;
    [ObservableProperty] private double _maxAfterValue = double.NaN;
    [ObservableProperty] private string _modelMetric = "--";
    [ObservableProperty] private string _featureDetail = "--";
    [ObservableProperty] private string _validationSummary = "";
    [ObservableProperty] private string _patchSummary = "";
    [ObservableProperty] private string _statusDeltaE = "";

    /// <summary>Selected bottom tab: 0 Log, 1 ΔE Analysis, 2 Color Analysis, 3 Chart rendition.</summary>
    [ObservableProperty] private int _tab;

    /// <summary>Mirrors the shell's session state; only used for the empty-panel guidance.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeltaEPlaceholder), nameof(ColorPlaceholder), nameof(RenditionPlaceholder))]
    private bool _hasSession;

    /// <summary>True while a fit or model apply runs for the active still.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeltaEPlaceholder), nameof(ColorPlaceholder), nameof(RenditionPlaceholder))]
    private bool _isApplying;

    /// <summary>Reason the last fit or apply failed; empty after a success or a new run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeltaEPlaceholder), nameof(ColorPlaceholder), nameof(RenditionPlaceholder))]
    private string _failure = "";

    public ObservableCollection<PatchRowViewModel> Patches { get; } = [];

    public ObservableCollection<StageDeltaERow> StageDeltaERows { get; } = [];

    public string DeltaEPlaceholder => Placeholder("per-stage ΔE");

    public string ColorPlaceholder => Placeholder("the color scatter");

    public string RenditionPlaceholder => Placeholder("the chart rendition");

    /// <summary>One message per state: empty session, ready to fit, fitting, or failed.</summary>
    private string Placeholder(string what)
    {
        if (IsApplying) return $"Fitting… {char.ToUpperInvariant(what[0])}{what[1..]} appears here when the run finishes.";
        if (!string.IsNullOrWhiteSpace(Failure))
            return $"The last fit failed: {Failure} See the Log tab for details.";
        return HasSession
            ? $"Detect the chart, then select Fit + apply to see {what}."
            : $"Add a chart still, then select Fit + apply to see {what}.";
    }

    /// <summary>
    /// Show a correction report. <paramref name="hasCorrected"/> distinguishes a model apply
    /// without chart ΔE; <paramref name="defaultModel"/> names the model when the report does not.
    /// </summary>
    public void Apply(CorrectionReport? report, bool hasCorrected, Func<string> defaultModel)
    {
        Report = report;
        HasMetrics = report?.HasMetrics == true;
        Patches.Clear();
        StageDeltaERows.Clear();

        if (!HasMetrics || report is null)
        {
            MeanAfter = MedianAfter = MaxAfter = "--";
            MeanBefore = MedianBefore = MaxBefore = "--";
            MeanQuality = MedianQuality = MaxQuality = "";
            MeanAfterValue = MedianAfterValue = MaxAfterValue = double.NaN;
            ModelMetric = "--";
            FeatureDetail = hasCorrected && report is null
                ? "Model apply · no chart ΔE"
                : string.IsNullOrWhiteSpace(report?.Process) ? "--" : report.Process;
            PatchSummary = "";
            ValidationSummary = "";
            StatusDeltaE = "";
            return;
        }

        MeanAfter = report.MeanAfter.ToString("0.00");
        MedianAfter = report.MedianAfter.ToString("0.00");
        MaxAfter = report.MaxAfter.ToString("0.00");
        MeanAfterValue = report.MeanAfter;
        MedianAfterValue = report.MedianAfter;
        MaxAfterValue = report.MaxAfter;
        MeanBefore = $"was {report.MeanBefore:0.00}";
        MedianBefore = $"was {report.MedianBefore:0.00}";
        MaxBefore = $"was {report.MaxBefore:0.00}";
        MeanQuality = report.QualityLabel(report.MeanAfter);
        MedianQuality = report.QualityLabel(report.MedianAfter);
        MaxQuality = report.QualityLabel(report.MaxAfter);
        ModelMetric = string.IsNullOrWhiteSpace(report.Model) ? defaultModel() : report.Model;
        FeatureDetail = string.IsNullOrWhiteSpace(report.Process)
            ? (report.FeatureCount > 0 ? $"{report.FeatureCount} features" : "")
            : report.FeatureCount > 0
                ? $"{report.Process} · {report.FeatureCount} feat"
                : report.Process;
        ValidationSummary = double.IsFinite(report.ValidationMean)
            ? $"CC-only validation · {report.ValidationFolds}-fold mean {report.ValidationMean:0.00} · median {report.ValidationMedian:0.00} · max {report.ValidationMax:0.00}"
            : "Training fit shown · validation unavailable";
        PatchSummary = $"{report.Patches.Count} patches";
        StatusDeltaE = report.StatusLine();
        foreach (var row in report.Patches)
        {
            Patches.Add(new PatchRowViewModel(row));
        }

        foreach (var stage in report.EffectiveStages)
        {
            StageDeltaERows.Add(new StageDeltaERow(stage, report.QualityLabel(stage.MeanAfter)));
        }
    }
}
