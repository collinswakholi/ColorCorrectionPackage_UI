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
    [InlineData("png")]
    [InlineData("jpg")]
    public async Task Export_RawStagesAndCorrected_UseSelectedCodecAndDistinctNames(string format)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "colorcorrect-save-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var output = Path.Combine(scratch, "exports");
        var previews = Path.Combine(scratch, "previews");
        var rgb = Enumerable.Range(0, 16 * 16 * 3).Select(i => new byte[] { 80, 130, 180 }[i % 3]).ToArray();
        foreach (var stage in new[] { "ffc", "gc", "wb", "corrected" })
            PreviewRgb.Write(Path.Combine(previews, stage + ".rgb"), 16, 16, rgb);
        var corrected = Path.Combine(scratch, "full-corrected.png");
        using (var bitmap = PreviewRgb.FromRgb(16, 16, rgb)) bitmap.Save(corrected);
        var vm = CreateViewModel(Path.Combine(scratch, "unused-report.json"), output);
        vm.SaveCorrected = vm.SaveFfc = vm.SaveGc = vm.SaveWb = vm.SaveCc = true;
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
            await Task.Run(() => dialogs.Result.SetResult(true));
            await saving;
            Assert.StartsWith("Saved 5 file(s)", vm.Status);
            Assert.Equal(5, Directory.GetFiles(output).Length);
            foreach (var stage in new[] { "corrected", "ffc", "gc", "wb", "cc" })
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
    public async Task Export_AfterAsyncOutsideFolderConfirmation_StaysOnUiThread(bool accept)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "colorcorrect-save-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var report = Path.Combine(scratch, "source-report.json");
        await File.WriteAllTextAsync(report, "{\"ok\":true}");
        var output = Path.Combine(scratch, "exports");
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
                Assert.Equal(0, changes);
                Assert.False(Directory.Exists(output));
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
            Assert.True(dialogs.WasRequested);
            await Task.Run(() => dialogs.Result.SetResult(true));
            await saving;
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
        public Task<string?> OpenVideoAsync() => Task.FromResult<string?>(null);
        public Task<string?> SaveCubeAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task SetClipboardAsync(string text) => Task.CompletedTask;
    }
}
