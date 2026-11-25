using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ColorCorrect.App.Controls;
using ColorCorrect.App.ViewModels;
using ColorCorrect.App.Views;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

/// <summary>
/// Pointer-only interactions the UI Automation battle test cannot reach without taking over the
/// mouse: file drops, catalog clicks, Ctrl-click selection, wheel zoom and double-click fit.
/// </summary>
public sealed class PointerInteractionTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static string Image(string name) => Path.Combine(Root, "test_images", name);

    private static (MainWindow Window, MainWindowViewModel Vm) Show()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false) { Job = { Busy = false } };
        PublicFixtureCatalog.Install(vm);
        var window = new MainWindow { Width = 1440, Height = 900, DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    private static void Pump(Func<bool> done, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!done() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Task.Delay(20).Wait();
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static Point Centre(Visual control, Visual window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
        ?? throw new InvalidOperationException("control is not in the window");

    private static void Drop(MainWindow window, Point at, params string[] paths)
    {
        var data = new DataTransfer();
        foreach (var path in paths)
        {
            var file = window.StorageProvider.TryGetFileFromPathAsync(path).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"storage provider cannot open {path}");
            data.Add(DataTransferItem.CreateFile(file));
        }
        window.DragDrop(at, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(at, RawDragEventType.DragOver, data, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(at, RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(MainWindow window, Point at, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.MouseMove(at, modifiers);
        window.MouseDown(at, MouseButton.Left, modifiers);
        window.MouseUp(at, MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void DroppingFilesOnTheWindowAddsThemToTheSession()
    {
        var (window, vm) = Show();
        Drop(window, new Point(900, 380), Image("ColorCard_Big.JPG"), Image("Blank.JPG"));
        Pump(() => vm.Session.Count == 2);
        Assert.Equal(new[] { "Blank.JPG", "ColorCard_Big.JPG" }, vm.Session.Select(item => item.Name).OrderBy(n => n));
        window.Close();
    }

    [AvaloniaFact]
    public void DroppingAWhiteStillOnTheWhiteWellSetsTheFfcReference()
    {
        var (window, vm) = Show();
        vm.Settings.DoFfc = true;
        Dispatcher.UIThread.RunJobs();
        var well = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "WhiteDrop");
        Assert.True(well.IsEffectivelyVisible, "the white well shows once FFC is on");
        Drop(window, Centre(well, window), Image("Blank.JPG"));
        Pump(() => vm.Workspace.HasWhiteRef);
        Assert.True(vm.Workspace.HasWhiteRef);
        Assert.Equal(Image("Blank.JPG"), vm.Settings.WhiteImagePath, ignoreCase: true);
        Assert.Empty(vm.Session); // a white dropped on the well is not added as a photo
        window.Close();
    }

    [AvaloniaFact]
    public void ClickingACatalogTileAddsItAndCtrlClickSelectsSeveralSessionTiles()
    {
        var (window, vm) = Show();
        vm.Layout.LibraryExpanded = true;
        Dispatcher.UIThread.RunJobs();
        var catalogTiles = window.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Classes.Contains("thumbCell") && p.IsEffectivelyVisible).ToList();
        Assert.True(catalogTiles.Count >= 2, $"catalog tiles: {catalogTiles.Count}");
        Click(window, Centre(catalogTiles[0], window));
        Pump(() => vm.Session.Count == 1);
        // Adding a photo re-lays out the sidebar, so find the next tile again after layout
        // settles (a slow runner clicked a stale position before) and pick one not yet added.
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var next = window.GetVisualDescendants().OfType<StackPanel>()
            .Where(p => p.Classes.Contains("thumbCell") && p.IsEffectivelyVisible)
            .First(p => p.Tag is CatalogItemViewModel item &&
                        !vm.Session.Any(s => string.Equals(s.Path, item.Path, StringComparison.OrdinalIgnoreCase)));
        // The tile can sit below the catalog's scrolled viewport once the session section
        // grows (taller fonts on macOS), where a click lands on whatever covers that point.
        next.BringIntoView();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var at = Centre(next, window);
        var hit = window.InputHitTest(at) as Visual;
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "CatalogScroll");
        var view = new Rect(scroll.TranslatePoint(default, window) ?? default, scroll.Bounds.Size);
        Assert.True(hit is not null && (hit == next || hit.GetVisualAncestors().Contains(next)),
            $"tile centre {at} in catalog view {view} hits {hit?.GetType().Name} '{(hit as Control)?.Name}'");
        Click(window, at);
        Pump(() => vm.Session.Count == 2);
        Assert.True(vm.Session.Count == 2,
            $"second catalog click on {(next.Tag as CatalogItemViewModel)?.Name} at {at} added nothing; session: " +
            string.Join(", ", vm.Session.Select(item => item.Name)) + "; log: " +
            string.Join(" | ", vm.LogEntries.TakeLast(6).Select(entry => entry.Message)));

        var sessionTiles = window.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Classes.Contains("sessionCell") && p.IsEffectivelyVisible).ToList();
        Assert.Equal(2, sessionTiles.Count);
        Click(window, Centre(sessionTiles[0], window));
        Click(window, Centre(sessionTiles[1], window), RawInputModifiers.Control);
        Pump(() => vm.Session.Count(item => item.IsSelected) == 2);
        Assert.Equal(2, vm.Session.Count(item => item.IsSelected));
        Assert.True(vm.Workspace.HasSelected);
        window.Close();
    }

    [AvaloniaFact]
    public void PointingAtTheImageDoesNotResizeTheStage()
    {
        // 0.4.0: the color readout appeared on hover and pushed the stage 48 px shorter,
        // so the fitted image jumped from 30% to 27% whenever the pointer entered it.
        var (window, vm) = Show();
        vm.AddPaths([Image("ColorCard_Big.JPG")]);
        Pump(() => vm.Workspace.HasImage);
        var stage = window.GetVisualDescendants().OfType<ZoomPanView>().First(z => z.IsEffectivelyVisible);
        Pump(() => stage.Bounds.Height > 0);
        // Wait until the preview has settled (the full image replaces a quick preview).
        var stable = 0;
        var last = stage.Scale;
        Pump(() => { stable = stage.Scale == last ? stable + 1 : 0; last = stage.Scale; return stable >= 15; }, 8000);
        var before = stage.Bounds.Size;
        window.MouseMove(Centre(stage, window), RawInputModifiers.None);
        Pump(() => vm.Picker.HasPick, 2000);
        Assert.Equal(before, stage.Bounds.Size);
        window.Close();
    }

    [AvaloniaFact]
    public void RightClickOnTheImageOpensTheCopyMenuAndRightDragPansWithoutIt()
    {
        // 0.4.0: the stage captured the pointer on every right press, so the release never
        // reached the stage and "Right-click a point to copy" opened nothing.
        var (window, vm) = Show();
        vm.AddPaths([Image("ColorCard_Big.JPG")]);
        Pump(() => vm.Workspace.HasImage);
        var stage = window.GetVisualDescendants().OfType<ZoomPanView>().First(z => z.IsEffectivelyVisible);
        Pump(() => stage.Bounds.Height > 0 && vm.Stage.Preview is not null, 8000);
        var menu = window.GetVisualDescendants().OfType<Grid>().Select(g => g.ContextMenu).First(m => m is not null)!;
        var at = Centre(stage, window);
        window.MouseMove(at, RawInputModifiers.None);
        window.MouseDown(at, MouseButton.Right, RawInputModifiers.None);
        window.MouseUp(at, MouseButton.Right, RawInputModifiers.None);
        Pump(() => menu.IsOpen, 2000);
        Assert.True(menu.IsOpen, "right-click must open the Copy RGB / Lab / hex menu");
        menu.Close();
        Dispatcher.UIThread.RunJobs();

        var before = stage.Scale;
        window.MouseWheel(at, new Vector(0, 3), RawInputModifiers.None);
        Pump(() => stage.Scale > before * 1.05);
        window.MouseDown(at, MouseButton.Right, RawInputModifiers.None);
        window.MouseMove(new Point(at.X + 80, at.Y + 40), RawInputModifiers.RightMouseButton);
        window.MouseUp(new Point(at.X + 80, at.Y + 40), MouseButton.Right, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(stage.DidPan, "a right-drag still pans");
        Assert.False(menu.IsOpen, "a pan must not open the menu");
        window.Close();
    }

    [AvaloniaFact]
    public void WheelZoomsTheStageAndTheFitButtonFitsItAgain()
    {
        var (window, vm) = Show();
        vm.AddPaths([Image("ColorCard_Big.JPG")]);
        Pump(() => vm.Workspace.HasImage);
        var stage = window.GetVisualDescendants().OfType<ZoomPanView>().First(z => z.IsEffectivelyVisible);
        Pump(() => stage.Bounds.Width > 0);
        // Wait until the full image has replaced the quick preview: its fit scale differs,
        // and a slow runner swapped it in between the two Fit clicks.
        Pump(() => vm.Stage.Preview is not null, 8000);
        var stable = 0;
        var last = stage.Scale;
        Pump(() => { stable = stage.Scale == last ? stable + 1 : 0; last = stage.Scale; return stable >= 15; }, 8000);
        var fit = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StageFitBtn");
        Click(window, Centre(fit, window));
        var fitted = stage.Scale;
        var centre = Centre(stage, window);
        window.MouseMove(centre, RawInputModifiers.None);
        window.MouseWheel(centre, new Vector(0, 3), RawInputModifiers.None);
        Pump(() => stage.Scale > fitted * 1.05);
        Assert.True(stage.Scale > fitted * 1.05, $"wheel did not zoom in: {fitted} -> {stage.Scale}");

        // The Fit button returns to the fitted view. (Headless input gives both clicks of a
        // double-click the same timestamp, so the double-tap gesture is not recognised here.)
        Click(window, Centre(fit, window));
        Pump(() => Math.Abs(stage.Scale - fitted) < 1e-6);
        Assert.Equal(fitted, stage.Scale, 6);
        window.Close();
    }
}
