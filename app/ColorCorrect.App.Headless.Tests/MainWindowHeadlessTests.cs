using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ColorCorrect.App.ViewModels;
using ColorCorrect.App.Views;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class MainWindowHeadlessTests
{
    [AvaloniaFact]
    public async Task Sidebar_NarrowExpandedControls_AreScrollableAndReadable()
    {
        foreach (var light in new[] { false, true })
        {
            var (window, vm) = ShowMain(1002, 672, light);
            try
            {
                // Exercise the real 240 px splitter minimum, not just the default sidebar width.
                var grid = Assert.IsType<Grid>(window.Sidebar.Parent);
                grid.ColumnDefinitions[0].Width = new GridLength(240);
                vm.LibraryExpanded = true;
                vm.ProcessExpanded = true;
                vm.ModelExpanded = true;
                vm.DoFfc = true;
                vm.AddPaths([TestImagePath("ColorCard_Big.JPG"), TestImagePath("ColorCard_Combo.JPG"),
                    TestImagePath("Sample_3572.JPG"), TestImagePath("Sample_3573.JPG")]);
                vm.LibraryExpanded = true; // Adding media deliberately closes Catalog; reopen for this stress case.
                // A cold decode is asynchronous. Never compare a transient blank
                // canvas to the populated reference, or depend on a warm disk cache.
                var previewDeadline = DateTime.UtcNow.AddSeconds(10);
                while (vm.OriginalPreview is null && DateTime.UtcNow < previewDeadline)
                {
                    await Task.Delay(20);
                    Dispatcher.UIThread.RunJobs();
                }
                Assert.NotNull(vm.OriginalPreview);
                Dispatcher.UIThread.RunJobs();

                AssertCriticalWorkspaceGeometry(window);
                Assert.True(window.SidebarScroll.Extent.Width <= window.SidebarScroll.Viewport.Width + 0.5);
                Assert.True(window.SidebarScroll.Extent.Height > window.SidebarScroll.Viewport.Height);
                Assert.True(window.SessionScroll.Extent.Width > window.SessionScroll.Viewport.Width);
                Assert.False(window.SessionScroll.AllowAutoHide);
                Assert.True(window.CatalogScroll.Extent.Width <= window.CatalogScroll.Viewport.Width + 0.5);
                var processHeader = window.ProcessToggle.GetVisualDescendants()
                    .OfType<Avalonia.Controls.Primitives.ToggleButton>().First();
                Assert.True(processHeader.IsChecked);
                var chevron = processHeader.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
                    .Single(path => path.Name == "SectionChevron");
                Assert.True(chevron.IsVisible);
                processHeader.IsChecked = false;
                Dispatcher.UIThread.RunJobs();
                Assert.False(chevron.IsVisible);
                Assert.False(vm.ProcessExpanded);
                processHeader.IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                Assert.True(chevron.IsVisible);
                Assert.True(vm.ProcessExpanded);
                foreach (var button in new[] { window.ImageUpload, window.FolderUpload,
                    window.FfcSettingsButton, window.GcSettingsButton, window.CcSettingsButton })
                {
                    Assert.True(button.Bounds.Width >= 28, $"{button.Name} has a usable target");
                    Assert.True(button.Focusable);
                }
                foreach (var label in window.Sidebar.GetVisualDescendants().OfType<TextBlock>()
                    .Where(text => text.Classes.Contains("sectionLabel")))
                {
                    Assert.True(label.FontSize >= 13);
                    Assert.Equal(0, label.LetterSpacing);
                }
                VisualRegression.Match(window, $"sidebar-populated-min-{(light ? "light" : "dark")}");
                window.CatalogScroll.BringIntoView();
                Dispatcher.UIThread.RunJobs();
                VisualRegression.Match(window, $"sidebar-catalog-min-{(light ? "light" : "dark")}");
                window.SidebarScroll.ScrollToEnd();
                Dispatcher.UIThread.RunJobs();
                Assert.True(window.SidebarScroll.Offset.Y > 0);
                VisualRegression.Match(window, $"sidebar-process-min-{(light ? "light" : "dark")}");
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }
    }

    [AvaloniaFact]
    public void MainWindow_MinimumViewport_RemainsUsableAndMatchesBaseline()
    {
        var (window, _) = ShowMain(1002, 672, light: false);
        try
        {
            Assert.Contains("narrow", window.Classes);
            Assert.Contains("short", window.Classes);
            Assert.Contains("compact", window.Classes);
            AssertCriticalWorkspaceGeometry(window);
            VisualRegression.Match(window, "main-empty-min-dark");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void MainWindow_WideDarkViewport_RemainsUsableAndMatchesBaseline()
    {
        var (window, _) = ShowMain(1440, 900, light: false);
        try
        {
            Assert.DoesNotContain("narrow", window.Classes);
            Assert.DoesNotContain("short", window.Classes);
            Assert.DoesNotContain("compact", window.Classes);
            AssertCriticalWorkspaceGeometry(window);
            VisualRegression.Match(window, "main-empty-wide-dark");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void MainWindow_WideLightViewport_RemainsUsableAndMatchesBaseline()
    {
        var (window, _) = ShowMain(1440, 900, light: true);
        try
        {
            AssertCriticalWorkspaceGeometry(window);
            VisualRegression.Match(window, "main-empty-wide-light");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void SessionTile_EnterSelectsAndActivatesItem()
    {
        var (window, vm) = ShowMain(1200, 760, light: false);
        try
        {
            var image = TestImagePath();
            vm.AddPaths([image]);
            Dispatcher.UIThread.RunJobs();
            var item = Assert.Single(vm.Session);

            var tile = window.LoadedThumbs.GetVisualDescendants()
                .OfType<StackPanel>()
                .First(control => control.Classes.Contains("sessionCell"));
            Assert.True(tile.Focus());
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.Same(item, vm.Active);
            Assert.True(item.IsSelected);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void SessionNavigation_BrowsesImagesAndStopsAtBoundaries()
    {
        var (window, vm) = ShowMain(1200, 760, light: false);
        try
        {
            vm.AddPaths([
                TestImagePath("ColorCard_Big.JPG"),
                TestImagePath("ColorCard_Combo.JPG"),
                TestImagePath("Sample_3572.JPG"),
            ]);
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.ShowSessionNavigation);
            Assert.Equal("3 of 3 · Sample_3572", vm.ActivePositionLabel);
            Assert.True(vm.PreviousSessionCommand.CanExecute(null));
            Assert.False(vm.NextSessionCommand.CanExecute(null));

            vm.PreviousSessionCommand.Execute(null);
            Assert.Equal("ColorCard_Combo.JPG", vm.Active?.Name);
            Assert.Equal("2 of 3 · ColorCard_Combo", vm.ActivePositionLabel);
            Assert.True(vm.NextSessionCommand.CanExecute(null));

            vm.PreviousSessionCommand.Execute(null);
            Assert.Equal("ColorCard_Big.JPG", vm.Active?.Name);
            Assert.False(vm.PreviousSessionCommand.CanExecute(null));
            Assert.True(window.PreviousImageButton.IsEffectivelyVisible);
            Assert.True(window.NextImageButton.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void NeuralNetworkSettings_ValidateArchitecturesAndExposeTrainingControls()
    {
        var (window, vm) = ShowMain(1200, 760, light: false);
        try
        {
            vm.SelectedMethod = vm.Methods.Single(method => method.Value == "nn");
            vm.HiddenLayersText = "[128, 64, 32]";
            vm.SelectedNnOptimizer = vm.NnOptimizers.Single(item => item.Value == "rmsprop");
            vm.NnBatchNormalization = true;
            vm.NnDropout = 0.25m;

            Assert.True(vm.ShowNn);
            Assert.True(vm.NnSettingsValid);
            Assert.False(vm.HasHiddenLayersError);
            Assert.Equal("rmsprop", vm.SelectedNnOptimizer.Value);

            vm.HiddenLayersText = "[128, nope]";
            Assert.False(vm.NnSettingsValid);
            Assert.True(vm.HasHiddenLayersError);
            Assert.NotEmpty(vm.HiddenLayersError);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ThemeToggle_ShowsTheIconForTheDestinationTheme()
    {
        var (window, vm) = ShowMain(1200, 760, light: true);
        try
        {
            Assert.True(window.MoonThemeIcon.IsEffectivelyVisible);
            Assert.False(window.SunThemeIcon.IsEffectivelyVisible);

            vm.ToggleThemeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.MoonThemeIcon.IsEffectivelyVisible);
            Assert.True(window.SunThemeIcon.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void SessionBatchDefaults_ExcludeGeneratedOutputsAndFfcWhite()
    {
        var (window, vm) = ShowMain(1200, 760, light: false);
        var temp = Path.Combine(Path.GetTempPath(), $"colorcorrect-headless-{Guid.NewGuid():N}");
        try
        {
            var raw = TestImagePath("ColorCard_Big.JPG");
            Directory.CreateDirectory(temp);
            var corrected = Path.Combine(temp, "ColorCard_Big_corrected.jpg");
            File.Copy(raw, corrected);
            vm.AddPaths([raw, corrected]);
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.Session.Single(item => item.Path == raw).IncludeInBatch);
            Assert.False(vm.Session.Single(item => item.Path == corrected).IncludeInBatch);

            vm.UseWhitePath(raw);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.Session.Single(item => item.Path == raw).IncludeInBatch);
        }
        finally
        {
            vm.ClearSessionCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.Close();
            Dispatcher.UIThread.RunJobs();
            for (var attempt = 0; attempt < 20 && Directory.Exists(temp); attempt++)
            {
                try
                {
                    Directory.Delete(temp, recursive: true);
                }
                catch (IOException) when (attempt < 19)
                {
                    Thread.Sleep(25);
                    Dispatcher.UIThread.RunJobs();
                }
            }
        }
    }

    [AvaloniaFact]
    public void Import_RelinksMovedFfcWhiteByUniqueFileName_AndExcludesItFromBatch()
    {
        var (window, vm) = ShowMain(1200, 760, light: false);
        try
        {
            var blank = TestImagePath("Blank.JPG");
            vm.WhiteImagePath = Path.Combine(Path.GetTempPath(), "moved-colorcorrect-fixture", "Blank.JPG");

            vm.AddPaths([blank]);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(Path.GetFullPath(blank), vm.WhiteImagePath);
            Assert.False(Assert.Single(vm.Session).IncludeInBatch);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void SettingsDialogs_FocusFirstField_CycleTabs_AndEscapeCloses()
    {
        var (owner, vm) = ShowMain(1002, 672, light: false);
        try
        {
            AssertDialog(owner, vm, new CcSettingsDialog(), "ModelSelect");
            AssertDialog(owner, vm, new FfcSettingsDialog(), "FfcMethodSelect");
            AssertDialog(owner, vm, new GcSettingsDialog(), "DoneButton");
        }
        finally
        {
            owner.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void CcSettings_MinimumOwner_FitsAndMatchesBaseline()
    {
        var (owner, vm) = ShowMain(1002, 672, light: false);
        var dialog = new CcSettingsDialog { DataContext = vm };
        try
        {
            dialog.Show(owner);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(OperatingSystem.IsWindows(), dialog.ShowInTaskbar);
            Assert.Same(owner.Icon, dialog.Icon);
            Assert.True(dialog.Bounds.Width <= owner.ClientSize.Width);
            Assert.True(dialog.Bounds.Height <= owner.ClientSize.Height);
            VisualRegression.Match(dialog, "cc-settings-min-dark");
        }
        finally
        {
            dialog.Close();
            owner.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void CcSettings_NeuralNetworkControls_FitAndMatchBaseline()
    {
        var (owner, vm) = ShowMain(1002, 672, light: false);
        vm.SelectedMethod = vm.Methods.Single(method => method.Value == "nn");
        vm.HiddenLayersText = "[128, 64, 32]";
        vm.SelectedNnOptimizer = vm.NnOptimizers.Single(item => item.Value == "rmsprop");
        vm.NnBatchNormalization = true;
        vm.NnDropout = 0.25m;
        var dialog = new CcSettingsDialog { DataContext = vm };
        try
        {
            dialog.Show(owner);
            Dispatcher.UIThread.RunJobs();
            Assert.True(dialog.HiddenLayersInput.IsEffectivelyVisible);
            Assert.True(dialog.NnOptimizerSelect.IsEffectivelyVisible);
            Assert.True(dialog.CcSettingsDoneBtn.IsEnabled);
            Assert.True(dialog.Bounds.Width <= owner.ClientSize.Width);
            Assert.True(dialog.Bounds.Height <= owner.ClientSize.Height);
            VisualRegression.Match(dialog, "cc-settings-nn-min-dark");
        }
        finally
        {
            dialog.Close();
            owner.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void GcSettings_MinimumOwner_ShowsAllGuidanceWithoutScrolling()
    {
        var (owner, vm) = ShowMain(1002, 672, light: false);
        var dialog = new GcSettingsDialog { DataContext = vm };
        try
        {
            dialog.Show(owner);
            Dispatcher.UIThread.RunJobs();

            Assert.True(dialog.Bounds.Width <= owner.ClientSize.Width);
            Assert.True(dialog.Bounds.Height <= owner.ClientSize.Height);
            Assert.True(dialog.SettingsScroll.Extent.Height <= dialog.SettingsScroll.Viewport.Height + 0.5);
            Assert.True(dialog.DoneButton.IsEffectivelyVisible);
        }
        finally
        {
            dialog.Close();
            owner.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void SplashWindow_UsesColorCorrectStudioBranding()
    {
        var splash = new SplashWindow();
        try
        {
            splash.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("ColorCorrect Studio splash", splash.Title);
            Assert.Contains(
                splash.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "ColorCorrect Studio");
            var logo = Assert.Single(splash.GetVisualDescendants().OfType<Image>());
            Assert.NotNull(logo.Source);
            Assert.True(logo.Bounds.Width >= 60);
            Assert.True(logo.Bounds.Height >= 60);
        }
        finally
        {
            splash.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static (MainWindow Window, MainWindowViewModel ViewModel) ShowMain(double width, double height, bool light)
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false)
        {
            // The real main window appears after the splash has finished worker startup.
            // Render the post-splash idle state instead of the transient constructor state.
            Busy = false,
            IsLightTheme = light,
        };
        var window = new MainWindow
        {
            Width = width,
            Height = height,
            DataContext = vm,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("ColorCorrect Studio", window.Title);
        Assert.Contains(
            window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "ColorCorrect Studio");
        Assert.Contains(
            window.GetVisualDescendants().OfType<Image>(),
            image => image.Source is not null && image.Bounds.Width >= 28 && image.Bounds.Height >= 28);
        return (window, vm);
    }

    private static void AssertCriticalWorkspaceGeometry(MainWindow window)
    {
        Assert.True(window.RunButton.IsEffectivelyVisible);
        Assert.True(window.BatchButton.IsEffectivelyVisible);
        Assert.True(window.Sidebar.IsEffectivelyVisible);
        Assert.True(window.AnalysisPanel.IsEffectivelyVisible);
        Assert.True(window.RunButton.Bounds.Width >= 80);
        Assert.True(window.RunButton.Bounds.Height >= 32);
        Assert.True(window.AnalysisPanel.Bounds.Height >= 140);
        Assert.True(window.StageFrame.Bounds.Width > 100);
        Assert.True(window.StageFrame.Bounds.Height > 100);
    }

    private static void AssertDialog(Window owner, MainWindowViewModel vm, Window dialog, string firstFieldName)
    {
        dialog.DataContext = vm;
        dialog.Show(owner);
        Dispatcher.UIThread.RunJobs();

        var first = dialog.FindControl<Control>(firstFieldName);
        Assert.NotNull(first);
        Assert.Same(first, dialog.FocusManager?.GetFocusedElement());
        Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(dialog));

        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(dialog.IsVisible);
    }

    private static string TestImagePath(string name = "ColorCard_Big.JPG")
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var path = Path.Combine(root, "test_images", name);
        Assert.True(File.Exists(path), $"Missing test image: {path}");
        return path;
    }
}
