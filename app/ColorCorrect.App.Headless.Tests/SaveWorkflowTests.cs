using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ColorCorrect.App.Controls;
using ColorCorrect.App.Services;
using ColorCorrect.App.ViewModels;
using ColorCorrect.App.Views;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class SaveWorkflowTests
{
    [AvaloniaFact]
    public void PlotExport_JpegContainsJpegPixels_NotMislabeledPng()
    {
        var path = Path.Combine(Path.GetTempPath(), "colorcorrect-plot-test-" + Guid.NewGuid().ToString("N") + ".jpg");
        var visual = new Border { Width = 64, Height = 32, Background = Brushes.Red };
        var window = new Window { Width = 64, Height = 32, Content = visual };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            PlotExport.Save(path, new ColorCorrect.Engine.CorrectionReport(), PlotKind.Bars, visual);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 255, 216, 255 }, bytes[..3]);
            using var image = SkiaSharp.SKBitmap.Decode(bytes);
            Assert.NotNull(image);
            Assert.Equal(64, image.Width);
            Assert.Equal(32, image.Height);
            Assert.InRange((int)image.GetPixel(32, 16).Red, 250, 255);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            File.Delete(path);
        }
    }

    [AvaloniaTheory]
    [InlineData("png", false, true)]
    [InlineData("jpg", false, true)]
    [InlineData("png", true, true)]
    [InlineData("jpg", true, true)]
    [InlineData("png", false, false)]
    [InlineData("jpg", false, false)]
    [InlineData("png", true, false)]
    [InlineData("jpg", true, false)]
    public async Task Export_RawStagesAndCorrected_UseSelectedCodecAndDistinctNames(string format, bool correctedAlreadyAtDestination, bool stagesEnabled)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "colorcorrect-save-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var output = Path.Combine(scratch, "exports");
        var previews = Path.Combine(scratch, "previews");
        var rgb = Enumerable.Range(0, 16 * 16 * 3).Select(i => new byte[] { 80, 130, 180 }[i % 3]).ToArray();
        foreach (var stage in new[] { "ffc", "gc", "wb", "corrected" })
            PreviewRgb.Write(Path.Combine(previews, stage + ".rgb"), 16, 16, rgb);
        Directory.CreateDirectory(output);
        // A completed artifact may already occupy its requested destination.
        var corrected = correctedAlreadyAtDestination
            ? Path.Combine(output, $"sample_corrected.{format}")
            : Path.Combine(scratch, "full-corrected.png");
        using (var bitmap = PreviewRgb.FromRgb(16, 16, rgb)) BitmapExport.Save(bitmap, corrected);
        var vm = CreateViewModel(Path.Combine(scratch, "unused-report.json"), output);
        vm.SaveCorrected = vm.SaveFfc = vm.SaveGc = vm.SaveWb = vm.SaveCc = true;
        vm.DoFfc = vm.DoGc = vm.DoWb = vm.DoCc = stagesEnabled;
        vm.SaveReport = false;
        vm.SelectedOutputFormat = vm.OutputFormats.Single(option => option.Value == format);
        vm.Session[0].PreviewDir = previews;
        vm.Session[0].CorrectedPath = corrected;
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var dialogs = new DeferredConfirmation();
        vm.Dialogs = dialogs;
        try
        {
            var saving = vm.ExecuteSaveCommand.ExecuteAsync(null);
            await saving;
            Assert.False(dialogs.WasRequested); // The explicit destination needs no second approval.
            Assert.StartsWith($"Saved {(stagesEnabled ? 5 : 1)} file(s) as", vm.Status);
            Assert.Equal(stagesEnabled ? 5 : 1, Directory.GetFiles(output).Length);
            foreach (var stage in stagesEnabled ? new[] { "corrected", "ffc", "gc", "wb", "cc" } : new[] { "corrected" })
            {
                var path = Path.Combine(output, $"sample_{stage}.{format}");
                var bytes = await File.ReadAllBytesAsync(path);
                Assert.Equal(format == "png" ? new byte[] { 137, 80, 78, 71 } : new byte[] { 255, 216, 255 },
                    bytes[..(format == "png" ? 4 : 3)]);
                using var image = SkiaSharp.SKBitmap.Decode(bytes);
                Assert.NotNull(image);
                Assert.Equal(16, image.Width);
                Assert.Equal(16, image.Height);
                var pixel = image.GetPixel(8, 8);
                Assert.InRange((int)pixel.Red, 77, 83);
                Assert.InRange((int)pixel.Green, 127, 133);
                Assert.InRange((int)pixel.Blue, 177, 183);
            }
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Session[0].Dispose();
            Directory.Delete(scratch, recursive: true);
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Export_AfterAsyncOverwriteConfirmation_StaysOnUiThread(bool accept)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "colorcorrect-save-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var report = Path.Combine(scratch, "source-report.json");
        await File.WriteAllTextAsync(report, "{\"ok\":true}");
        var output = Path.Combine(scratch, "exports");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "sample_report.json"), "existing report");
        var vm = CreateViewModel(report, output);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var dialogs = new DeferredConfirmation();
        vm.Dialogs = dialogs;
        var changes = 0;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(vm.IsSaving)) return;
            Dispatcher.UIThread.VerifyAccess();
            changes++;
        };
        try
        {
            var saving = vm.ExecuteSaveCommand.ExecuteAsync(null);
            Assert.True(dialogs.WasRequested);
            Assert.False(saving.IsCompleted);
            await Task.Run(() => dialogs.Result.SetResult(accept));
            await saving;
            Assert.False(vm.IsSaving);
            Assert.False(vm.ShowSaveProgress);
            if (accept)
            {
                Assert.Equal(2, changes);
                Assert.Equal("{\"ok\":true}", await File.ReadAllTextAsync(Path.Combine(output, "sample_report.json")));
                Assert.StartsWith("Saved 1 file(s)", vm.Status);
            }
            else
            {
                Assert.Equal(2, changes);
                Assert.Equal("existing report", await File.ReadAllTextAsync(Path.Combine(output, "sample_report.json")));
            }
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Session[0].Dispose();
            Directory.Delete(scratch, recursive: true);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_UnavailableReferenceReportsPartialFailureWhileKeepingTheCompletedResult(bool changed)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "colorcorrect-partial-save-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        var report = Path.Combine(scratch, "report.json"); var output = Path.Combine(scratch, "exports");
        var photo = Path.Combine(scratch, "sample.png"); var model = Path.Combine(scratch, "model.json"); var white = Path.Combine(scratch, "white.png");
        await File.WriteAllTextAsync(report, "{\"ok\":true}"); await File.WriteAllTextAsync(photo, "source");
        await File.WriteAllTextAsync(model, "{}"); await File.WriteAllTextAsync(white, "white A");
        var vm = CreateViewModel(report, output); var item = vm.Session[0]; vm.SaveCorrected = true;
        item.ModelPath = model;
        item.CompletedContext = ColorCorrect.Engine.CompletedResultContext.Capture(ColorCorrect.Engine.AssetIdentity.Capture(photo), model,
            new() { DoFfc = true, WhiteImagePath = white });
        var context = item.CompletedContext;
        if (changed) await File.WriteAllTextAsync(white, "white B"); else File.Delete(white);
        var window = new MainWindow { DataContext = vm }; window.Show();
        try
        {
            await vm.ExecuteSaveCommand.ExecuteAsync(null);
            Assert.StartsWith("Saved 1 file(s); 1 file(s) failed", vm.Status);
            Assert.Equal("{\"ok\":true}", await File.ReadAllTextAsync(Path.Combine(output, "sample_report.json")));
            Assert.False(File.Exists(Path.Combine(output, "sample_corrected.jpg")));
            Assert.Contains(vm.LogEntries, entry => entry.Message.Contains("corrected image is unavailable"));
            Assert.True(item.IsCorrected); Assert.Same(context, item.CompletedContext); Assert.Equal(model, item.ModelPath);
            Assert.False(vm.IsSaving); Assert.False(vm.ShowSaveProgress);
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); item.Dispose(); Directory.Delete(scratch, true); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_UnwritableOutput_ReportsErrorAndRestoresUiState(bool failSingleFile)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "colorcorrect-save-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var report = Path.Combine(scratch, "source-report.json");
        await File.WriteAllTextAsync(report, "{\"ok\":true}");
        var output = failSingleFile ? Path.Combine(scratch, "exports") : Path.Combine(scratch, "a-file-not-a-directory");
        var sentinel = output;
        if (failSingleFile)
        {
            // A directory occupies the destination filename, so only the copy fails.
            var blocked = Path.Combine(output, "sample_report.json");
            Directory.CreateDirectory(blocked);
            sentinel = Path.Combine(blocked, "keep.txt");
        }
        await File.WriteAllTextAsync(sentinel, "keep");
        var vm = CreateViewModel(report, output);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var dialogs = new DeferredConfirmation();
        vm.Dialogs = dialogs;
        try
        {
            var saving = vm.ExecuteSaveCommand.ExecuteAsync(null);
            await saving;
            Assert.False(dialogs.WasRequested);
            Assert.StartsWith("Save failed:", vm.Status);
            Assert.False(vm.IsSaving);
            Assert.False(vm.ShowSaveProgress);
            Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Session[0].Dispose();
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static MainWindowViewModel CreateViewModel(string report, string output)
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false)
        {
            Busy = false,
            OutputDir = output,
            SaveCorrected = false,
            SaveFfc = false,
            SaveGc = false,
            SaveWb = false,
            SaveCc = false,
            SaveReport = true,
        };
        vm.Session.Add(new SessionItemViewModel(Path.Combine(Path.GetDirectoryName(report)!, "sample.png"))
        {
            IsCorrected = true,
            IncludeInSave = true,
            ReportPath = report,
        });
        return vm;
    }

    private sealed class DeferredConfirmation : IAppDialogs
    {
        public TaskCompletionSource<bool> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasRequested { get; private set; }
        public Task<bool> ConfirmAsync(string title, string message, string? confirmLabel = null, bool danger = false)
        {
            Dispatcher.UIThread.VerifyAccess();
            WasRequested = true;
            return Result.Task;
        }
        public Task<IReadOnlyList<string>> OpenImagesAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> OpenFolderImagesAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> OpenJsonAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> SaveJsonAsync(string title, string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> SaveImageAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> SavePlotAsync(string title, string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> OpenWhiteAsync() => Task.FromResult<string?>(null);
        public Task<string?> SaveCubeAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task SetClipboardAsync(string text) => Task.CompletedTask;
    }
}
