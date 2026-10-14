using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ColorCorrect.App.Services;
using ColorCorrect.App.ViewModels;
using ColorCorrect.App.Views;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class StillOnlyWorkflowTests
{
    [AvaloniaFact]
    public void MainWindow_HasStillImageImportAndNoVideoControls()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            Assert.NotNull(window.FindControl<Button>("ImageUpload"));
            Assert.NotNull(window.FindControl<Button>("FolderUpload"));
            Assert.Null(window.FindControl<Button>("VideoUpload"));
            Assert.Null(window.FindControl<Control>("VideoCompare"));
            Assert.Null(window.FindControl<Button>("VideoPlayBtn"));
            Assert.Equal("Add a still image, then Fit + apply.", vm.WorkflowHint);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void AddPaths_RejectsVideoAndRenamedVideoButKeepsStill()
    {
        var root = Path.Combine(Path.GetTempPath(), "colorcorrect-still-only-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        try
        {
            // MP4's file-type box, also disguised with a supported still extension.
            var mp4Header = new byte[] { 0, 0, 0, 24, 102, 116, 121, 112, 105, 115, 111, 109 };
            var clip = Path.Combine(root, "clip.MP4");
            var disguised = Path.Combine(root, "clip.jpg");
            File.WriteAllBytes(clip, mp4Header);
            File.WriteAllBytes(disguised, mp4Header);
            var still = Sample();
            vm.AddPaths([clip, still, disguised]);
            Assert.Equal(still, Assert.Single(vm.Session).Path);
            Assert.Contains("Skipped 2 unsupported", vm.Status);
            Assert.Contains("video is not supported", vm.Status);
            Assert.False(SupportedStillImage.IsSupportedFile(clip));
            Assert.False(SupportedStillImage.IsSupportedFile(disguised));
            Assert.True(SupportedStillImage.IsSupportedFile(still));
        }
        finally
        {
            foreach (var item in vm.Session) item.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaFact]
    public void RestoreSession_SkipsLegacyVideoFlagAndUnflaggedUnsupportedPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "colorcorrect-session-still-only-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        try
        {
            var clip = Path.Combine(root, "clip.mov");
            File.WriteAllBytes(clip, [0, 0, 0, 20, 102, 116, 121, 112]);
            var still = Sample();
            var state = new SessionState
            {
                ActivePath = clip,
                Items =
                [
                    new SessionItemDto { Path = still, IsVideo = true },
                    new SessionItemDto { Path = clip, IsVideo = false },
                    new SessionItemDto { Path = still, IncludeInBatch = false },
                ],
            };
            Assert.Equal(2, vm.RestoreWorkingItems(state));
            var item = Assert.Single(vm.Session);
            Assert.Equal(still, item.Path);
            Assert.Same(item, vm.Active);
            Assert.False(item.IncludeInBatch);
            Assert.Contains("Skipped 2 unsupported session", vm.Status);
            Assert.Contains("Video is not supported", vm.Status);
        }
        finally
        {
            foreach (var item in vm.Session) item.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaFact]
    public void RestoreSession_WithOnlyLegacyVideo_ClearsPreviousStillAndDisablesRun()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false) { Busy = false, WorkerReady = true };
        vm.AddPaths([Sample()]);
        Assert.True(vm.HasImage);
        Assert.NotNull(vm.Active);
        var skipped = vm.RestoreWorkingItems(new SessionState
        {
            Items = [new SessionItemDto { Path = Sample(), IsVideo = true }],
        });
        Assert.Equal(1, skipped);
        Assert.Empty(vm.Session);
        Assert.Null(vm.Active);
        Assert.False(vm.HasImage);
        Assert.False(vm.ShowStillPreview);
        Assert.Null(vm.Preview);
        Assert.Null(vm.OriginalPreview);
        Assert.False(vm.DetectCommand.CanExecute(null));
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.Contains("Video is not supported", vm.Status);
    }

    private static string Sample()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "test_images")))
            root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine(root.FullName, "test_images", "ColorCard_Big.JPG");
    }
}
