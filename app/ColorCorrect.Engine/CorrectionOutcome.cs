namespace ColorCorrect.Engine;

public sealed class CorrectionOutcome
{
    public CompletedResultContext? CompletedContext { get; init; }
    public bool Ok { get; init; }
    public bool HasChartMetrics { get; init; }
    public string Error { get; init; } = "";
    public string OutputPath { get; init; } = "";
    public string ReportPath { get; init; } = "";
    public string ModelPath { get; init; } = "";
    public string RunDir { get; init; } = "";
    public string PreviewDir { get; init; } = "";
    public string Stderr { get; init; } = "";
    public string FfcSkipped { get; init; } = "";
    public string ApplyBackend { get; init; } = "";
    public string PlaneBackend { get; init; } = "";
    public string DetectBackend { get; init; } = "";
    public CorrectionReport? Report { get; init; }
}
