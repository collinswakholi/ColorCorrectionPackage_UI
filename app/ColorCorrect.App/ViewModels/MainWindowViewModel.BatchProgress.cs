using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ColorCorrect.Engine;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ColorCorrect.App.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty] private bool _batchPreviewResults = true;
    [ObservableProperty] private string _batchCurrentImageLabel = "";
    [ObservableProperty] private string _batchProgressSummary = "";
    private SessionItemViewModel? _batchCurrentItem;
    private int _batchProgressGeneration;
    private int _batchImageIndex;
    private int _batchImageCount;
    private string[] _combinedProgressKeys = [];

    public bool IsResultPreviewSuppressed => Active?.PreviewSuppressed == true;

    internal static CorrectionOptions CaptureBatchOptions(CorrectionOptions options, bool previews, string? appliedModel = null)
    {
        var captured = JsonSerializer.Deserialize<CorrectionOptions>(JsonSerializer.Serialize(options))!;
        captured.BatchPreviewResults = previews;
        captured.PreviewMode = previews ? "corrected" : "none";
        captured.WriteOutput = !previews;
        // Batch artifacts belong to each owned run; Save publishes them later.
        captured.OutputDir = "";
        if (!string.IsNullOrWhiteSpace(appliedModel))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(appliedModel));
            var model = document.RootElement;
            static bool Present(JsonElement model, string name) => model.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;
            captured.DoGc &= Present(model, "gc_model") || (model.TryGetProperty("gc_coeffs", out var coefficients)
                && coefficients.ValueKind == JsonValueKind.Array && coefficients.GetArrayLength() > 0);
            captured.DoWb &= Present(model, "wb_diag");
            if (model.TryGetProperty("correction", out var correction)) captured.DoCc &= correction.ValueKind != JsonValueKind.Null;
        }
        return captured;
    }

    internal static Task<(CorrectionOptions Options, string ModelPath)> CaptureBatchAssetsAsync(
        string repoRoot, CorrectionOptions options, string modelPath, bool applyModel, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capturedOptions = CaptureBatchOptions(options, options.BatchPreviewResults);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (applyModel || capturedOptions.DoFfc)
            {
                var assets = RunPaths.NewRunDir(repoRoot, "batch-assets");
                if (applyModel)
                {
                    var capturedModel = Path.Combine(assets, "model.json");
                    File.Copy(modelPath, capturedModel);
                    modelPath = capturedModel;
                    cancellationToken.ThrowIfCancellationRequested();
                    capturedOptions = CaptureBatchOptions(capturedOptions, capturedOptions.BatchPreviewResults, modelPath);
                }
                if (capturedOptions.DoFfc)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var capturedWhite = Path.Combine(assets, "white" + Path.GetExtension(capturedOptions.WhiteImagePath));
                    File.Copy(capturedOptions.WhiteImagePath, capturedWhite);
                    capturedOptions.WhiteImagePath = capturedWhite;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return (capturedOptions, modelPath);
        }, cancellationToken);
    }

    private void BeginBatchImage(SessionItemViewModel item, int index, int count, CorrectionOptions options, bool applyModel)
    {
        _batchProgressGeneration++;
        _batchCurrentItem = item;
        _batchImageIndex = index;
        _batchImageCount = count;
        _combinedProgressKeys = [];
        BeginCapturedPipeline(applyModel ? "apply" : "fit", options);
        BatchCurrentImageLabel = $"{index + 1} / {count} · {item.Name}";
        BatchLabel = BatchCurrentImageLabel;
        JobProgress = 0;
        BatchProgress = 100.0 * index / Math.Max(1, count);
        item.HasProcessingProgress = true;
        item.IsProcessing = true;
        item.ProcessingProgress = 0;
        item.ProcessingStage = "Preparing";
        item.ProcessingStatus = "Processing";
    }

    private void FinishBatchImage(SessionItemViewModel item, bool success, string? failure = null)
    {
        item.IsProcessing = false;
        item.ProcessingStatus = success ? "Complete" : failure ?? "Failed";
        item.ProcessingStage = item.ProcessingStatus;
        if (success) item.ProcessingProgress = 100;
        if (ReferenceEquals(_batchCurrentItem, item))
        {
            foreach (var step in PipelineSteps) step.IsActive = false;
            _batchProgressGeneration++;
            _batchCurrentItem = null;
            if (success) JobProgress = 100;
        }
    }

    private void UpdateBatchStageProgress()
    {
        if (_batchCurrentItem is not { } item) return;
        item.ProcessingStage = CurrentStage;
        item.ProcessingProgress = JobProgress;
        BatchProgress = 100.0 * (_batchImageIndex + JobProgress / 100) / Math.Max(1, _batchImageCount);
    }

    partial void OnBatchPreviewResultsChanged(bool value) { if (!_restoring) ScheduleSave(); }

    [RelayCommand]
    private async Task ShowResultPreviewAsync()
    {
        if (Busy || IsSaving || Active is not { PreviewSuppressed: true } item) return;
        if (!File.Exists(ResolveCorrectedViewPath(item)))
        {
            Status = "The corrected image is unavailable. Process the image again to create a preview.";
            return;
        }
        // This explicit request may decode the existing corrected artifact; it
        // does not change the captured fit or enable previews on other results.
        item.PreviewSuppressed = false;
        OnPropertyChanged(nameof(IsResultPreviewSuppressed));
        await LoadCorrectedPreviewAsync(item);
        StageName = "Corrected";
        ApplyStagePreview();
        NotifyStageRadios();
        ScheduleSave();
    }
}
