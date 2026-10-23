using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ColorCorrect.App.ViewModels;
using ColorCorrect.App.Views;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class DocumentationRenderingTests
{
    [AvaloniaFact]
    public async Task ProcessIllustration_UsesAcceptanceFixturesAndShowsAllCorrectionStages()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "testdata", "acceptance")))
            root = root.Parent;
        Assert.NotNull(root);
        var corpus = Path.Combine(root.FullName, "testdata", "acceptance");
        var vm = new MainWindowViewModel(persistenceEnabled: false)
        {
            Busy = false, IsLightTheme = true, ProcessExpanded = true,
            ModelExpanded = false, LibraryExpanded = false,
        };
        PublicFixtureCatalog.Install(vm);
        var window = new MainWindow { Width = 1002, Height = 900, DataContext = vm };
        window.Show();
        try
        {
            var grid = Assert.IsType<Grid>(window.Sidebar.Parent);
            grid.ColumnDefinitions[0].Width = new GridLength(240);
            vm.UseWhitePath(Path.Combine(corpus, "blank.jpg"));
            vm.AddPaths([Path.Combine(corpus, "chart.jpg"), Path.Combine(corpus, "sample.jpg")]);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (vm.OriginalPreview is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.NotNull(vm.OriginalPreview);
            Dispatcher.UIThread.RunJobs();
            var processTop = window.ProcessToggle.TranslatePoint(default, window.SidebarScroll);
            Assert.NotNull(processTop);
            window.SidebarScroll.Offset = new Vector(0, window.SidebarScroll.Offset.Y + processTop.Value.Y - 8);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FfcSettingsButton.IsEffectivelyVisible);
            Assert.True(window.GcSettingsButton.IsEffectivelyVisible);
            Assert.True(window.CcSettingsButton.IsEffectivelyVisible);
            Assert.True(vm.DoFfc);
            Assert.True(vm.HasWhiteRef);
            // Documentation must not expose build-machine paths from the normal
            // operational log. This is a UI illustration, not a completed fit.
            vm.LogEntries.Clear();
            vm.Status = "Public samples · Blank.JPG is the FFC reference.";
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestResults", "UI"));
            Directory.CreateDirectory(output);
            frame.Save(Path.Combine(output, "docs-process-controls-still-only.png"));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
