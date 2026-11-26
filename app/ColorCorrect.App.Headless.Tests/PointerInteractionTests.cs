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

    private static bool Reaches(MainWindow window, Visual target, Point at) =>
        window.InputHitTest(at) is Visual hit && (hit == target || hit.GetVisualAncestors().Contains(target));

    private static string Describe(Visual? hit) =>
        hit is null ? "nothing" : hit.GetType().Name + (hit is Control { Name: { Length: > 0 } name } ? "#" + name : "");

    // Avalonia hit-tests input against the last frame it rendered, which can lag the layout on a
    // busy machine (a new tile, a late thumbnail). Render until the target is under its centre,
    // as a user only clicks what they can see, then send the input with nothing run in between.
    private static Point Ready(MainWindow window, Visual target, Point? point = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var at = point ?? Centre(target, window);
            if (Reaches(window, target, at))
            {
                return at;
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"{Describe(target)} at {at} never took pointer input; it hits {Describe(window.InputHitTest(at) as Visual)}");
            }

            Task.Delay(20).Wait();
        }
    }

    private static void Drop(MainWindow window, Visual target, params string[] paths) =>
        Drop(window, target, null, paths);

    private static void Drop(MainWindow window, Visual target, Point? point, params string[] paths)
    {
        var data = new DataTransfer();
        foreach (var path in paths)
        {
            var file = window.StorageProvider.TryGetFileFromPathAsync(path).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"storage provider cannot open {path}");
            data.Add(DataTransferItem.CreateFile(file));
        }
        var at = Ready(window, target, point);
        window.DragDrop(at, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(at, RawDragEventType.DragOver, data, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(at, RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(MainWindow window, Visual target, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var at = Ready(window, target);
        window.MouseMove(at, modifiers);
        window.MouseDown(at, MouseButton.Left, modifiers);
        window.MouseUp(at, MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void DroppingFilesOnTheWindowAddsThemToTheSession()
    {
        var (window, vm) = Show();
        Drop(window, window, new Point(900, 380), Image("ColorCard_Big.JPG"), Image("Blank.JPG"));
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
        Drop(window, well, Image("Blank.JPG"));
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
        Click(window, catalogTiles[0]);
        Pump(() => vm.Session.Count == 1);
        Click(window, catalogTiles[1]);
        Pump(() => vm.Session.Count == 2);
        Assert.Equal(2, vm.Session.Count);

        var sessionTiles = window.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Classes.Contains("sessionCell") && p.IsEffectivelyVisible).ToList();
        Assert.Equal(2, sessionTiles.Count);
        Click(window, sessionTiles[0]);
        Click(window, sessionTiles[1], RawInputModifiers.Control);
        Pump(() => vm.Session.Count(item => item.IsSelected) == 2);
        Assert.True(vm.Session.Count(item => item.IsSelected) == 2,
            "selected: " + string.Join(", ", vm.Session.Where(item => item.IsSelected).Select(item => item.Name)));
        Assert.True(vm.Workspace.HasSelected);
        window.Close();
    }

    [AvaloniaFact]
    public void EveryPointOfANewSessionTileIsClickable()
    {
        // 0.5.0 CI: session cells had no background, so until a thumbnail decoded the middle of
        // the cell (and always the caption and gaps) fell through to the list behind it.
        var (window, vm) = Show();
        vm.AddPaths([Image("ColorCard_Big.JPG"), Image("Sample_3572.JPG")], collapseLibrary: false);

        // Rendering lags layout on a busy machine, so allow a few frames; dead points never heal.
        string? problem = "no session tiles";
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (problem is not null && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            problem = DeadPoints(window);
            if (problem is not null) Task.Delay(20).Wait();
        }

        Assert.True(problem is null, problem);
        window.Close();
    }

    private static string? DeadPoints(MainWindow window)
    {
        var cells = window.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Classes.Contains("sessionCell") && p.IsEffectivelyVisible).ToList();
        if (cells.Count != 2) return $"{cells.Count} session tiles";
        foreach (var cell in cells)
        {
            var misses = 0;
            var instead = new System.Collections.Generic.HashSet<string>();
            for (var y = 1.0; y < cell.Bounds.Height - 1; y += 2)
            {
                for (var x = 1.0; x < cell.Bounds.Width - 1; x += 2)
                {
                    var at = cell.TranslatePoint(new Point(x, y), window)!.Value;
                    if (!Reaches(window, cell, at))
                    {
                        misses++;
                        instead.Add(Describe(window.InputHitTest(at) as Visual));
                    }
                }
            }

            if (misses > 0)
            {
                return $"{(cell.Tag as SessionItemViewModel)?.Name}: {misses} points of the {cell.Bounds.Size} tile do not reach it; they hit {string.Join(", ", instead)}";
            }
        }

        return null;
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
        window.MouseMove(Ready(window, stage), RawInputModifiers.None);
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
        var at = Ready(window, stage);
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
        // Until the preview decodes, the stage is sized from the file's pixel size (2560x1707);
        // the decoded preview is 2250x1500, so a fit measured earlier does not match a later one.
        Pump(() => stage.Bounds.Width > 0 && vm.Stage.Preview is not null, 8000);
        var fit = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StageFitBtn");
        Click(window, fit);
        var fitted = stage.Scale;
        var centre = Ready(window, stage);
        window.MouseMove(centre, RawInputModifiers.None);
        window.MouseWheel(centre, new Vector(0, 3), RawInputModifiers.None);
        Pump(() => stage.Scale > fitted * 1.05);
        Assert.True(stage.Scale > fitted * 1.05, $"wheel did not zoom in: {fitted} -> {stage.Scale}");

        // The Fit button returns to the fitted view. (Headless input gives both clicks of a
        // double-click the same timestamp, so the double-tap gesture is not recognised here.)
        Click(window, fit);
        Pump(() => Math.Abs(stage.Scale - fitted) < 1e-6);
        Assert.Equal(fitted, stage.Scale, 6);
        window.Close();
    }
}
