using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ColorCorrect.App.Controls;
using ColorCorrect.App.Services;
using ColorCorrect.App.ViewModels;

namespace ColorCorrect.App.Views;

public partial class MainWindow : Window
{
    private Window? _activeSettingsDialog;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);

        ToastService.ToastRequested += OnToastRequested;
        Closed += async (_, _) =>
        {
            ToastService.ToastRequested -= OnToastRequested;
            if (DataContext is MainWindowViewModel vm)
            {
                vm.LogEntries.CollectionChanged -= OnLogChanged;
                await vm.DisposeAsync();
            }
        };

        Opened += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.LogEntries.CollectionChanged -= OnLogChanged;
                vm.LogEntries.CollectionChanged += OnLogChanged;
            }

            ApplyLayoutScale(ClientSize);
            WireSidebarChrome();
        };
        SizeChanged += (_, e) => ApplyLayoutScale(e.NewSize);
    }

    private void WireSidebarChrome()
    {
        Sidebar.SizeChanged += (_, e) => ApplySidebarWidth(e.NewSize.Width);
        ApplySidebarWidth(Sidebar.Bounds.Width);

        DragDrop.SetAllowDrop(WhiteDrop, true);
        WhiteDrop.AddHandler(DragDrop.DragOverEvent, OnWhiteDragOver);
        WhiteDrop.AddHandler(DragDrop.DragLeaveEvent, OnWhiteDragLeave);
        WhiteDrop.AddHandler(DragDrop.DropEvent, OnWhiteDrop);
        DragDrop.SetAllowDrop(LoadedSection, true);
        LoadedSection.AddHandler(DragDrop.DragOverEvent, OnSessionDragOver);
        LoadedSection.AddHandler(DragDrop.DragLeaveEvent, OnSessionDragLeave);
        LoadedSection.AddHandler(DragDrop.DropEvent, OnSessionDrop);
    }

    private void ApplyLayoutScale(Size size)
    {
        var narrow = size.Width < 1280;
        var shortWindow = size.Height < 800;
        var compact = size.Width < 1180 || size.Height < 760;
        SetClass("narrow", narrow);
        SetClass("short", shortWindow);
        SetClass("compact", compact);

        if (MetricGrid is not null)
        {
            MetricGrid.Columns = narrow ? 2 : 4;
        }

        StackPlots(ColorPlots, ColorResidualCard, null, narrow, "1.2*,12,*");
        StackPlots(PlotHoverHost, RenditionCorrectedCard, PlotHoverLayer, narrow, "*,12,*");
        StackPlots(DeltaEPlots, DeltaEStageCard, null, narrow, "*,12,1.15*");
        if (AnalysisPanel is not null)
        {
            AnalysisPanel.MinHeight = shortWindow ? 150 : 220;
        }

        ApplyToastWidth(size.Width);
    }

    private void ApplyToastWidth(double windowWidth)
    {
        if (ToastHost is null)
        {
            return;
        }

        var sidebar = Sidebar?.Bounds.Width > 0 ? Sidebar.Bounds.Width : 320;
        ToastHost.MaxWidth = Math.Clamp(windowWidth - sidebar - 64, 180, 360);
    }

    private static double HorizontalSidebarPad(double width)
    {
        if (width < 300)
        {
            return 16;
        }

        if (width < 348)
        {
            return 20;
        }

        return 28;
    }

    private void SetClass(string name, bool on)
    {
        if (on)
        {
            if (!Classes.Contains(name))
            {
                Classes.Add(name);
            }
        }
        else
        {
            Classes.Remove(name);
        }
    }

    private static void StackPlots(Grid? host, Control? second, Control? overlay, bool stack, string wideColumns)
    {
        if (host is null || second is null)
        {
            return;
        }

        if (stack)
        {
            host.ColumnDefinitions = new ColumnDefinitions("*");
            host.RowDefinitions = new RowDefinitions("*,8,*");
            Grid.SetColumn(second, 0);
            Grid.SetRow(second, 2);
            if (overlay is not null)
            {
                Grid.SetColumnSpan(overlay, 1);
                Grid.SetRowSpan(overlay, 3);
            }
        }
        else
        {
            host.ColumnDefinitions = new ColumnDefinitions(wideColumns);
            host.RowDefinitions = new RowDefinitions("*");
            Grid.SetColumn(second, 2);
            Grid.SetRow(second, 0);
            if (overlay is not null)
            {
                Grid.SetColumnSpan(overlay, 3);
                Grid.SetRowSpan(overlay, 1);
            }
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LogScrollViewer.Offset = new Vector(0, LogScrollViewer.Extent.Height);
        }, DispatcherPriority.Background);
    }

    private void OnToastRequested(ToastRequest request)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var toast = new ToastNotification(request);
            ToastHost.Items.Add(toast);
            toast.Dismissed += (_, _) =>
                Dispatcher.UIThread.Post(() => ToastHost.Items.Remove(toast));
        });
    }

    private async void OpenHelp_Click(object? sender, RoutedEventArgs e)
    {
        if (_activeSettingsDialog is not null) return;
        await ShowSettingsDialogAsync(new HelpWindow(), sender as Control ?? HelpButton);
    }

    private async void OpenCcSettings_Click(object? sender, RoutedEventArgs e)
    {
        await ShowSettingsDialogAsync(new CcSettingsDialog { DataContext = DataContext }, sender as Control);
    }

    private void PreviousSession_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.PreviousSessionCommand.CanExecute(null))
        {
            vm.PreviousSessionCommand.Execute(null);
            BringActiveSessionIntoView();
        }
    }

    private void NextSession_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.NextSessionCommand.CanExecute(null))
        {
            vm.NextSessionCommand.Execute(null);
            BringActiveSessionIntoView();
        }
    }

    private void BringActiveSessionIntoView()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is not MainWindowViewModel { Active: { } active } vm)
            {
                return;
            }

            var index = vm.Session.IndexOf(active);
            LoadedThumbs.ContainerFromIndex(index)?.BringIntoView();
        }, DispatcherPriority.Background);
    }

    private async void OpenFfcSettings_Click(object? sender, RoutedEventArgs e)
    {
        await ShowSettingsDialogAsync(new FfcSettingsDialog { DataContext = DataContext }, sender as Control);
    }

    private async void OpenGcSettings_Click(object? sender, RoutedEventArgs e)
    {
        await ShowSettingsDialogAsync(new GcSettingsDialog { DataContext = DataContext }, sender as Control);
    }

    private async System.Threading.Tasks.Task ShowSettingsDialogAsync(Window dialog, Control? trigger)
    {
        trigger?.BringIntoView();
        _activeSettingsDialog = dialog;
        ModalScrim.IsVisible = true;
        try
        {
            await dialog.ShowDialog(this);
        }
        finally
        {
            if (ReferenceEquals(_activeSettingsDialog, dialog))
            {
                _activeSettingsDialog = null;
            }

            ModalScrim.IsVisible = false;
            if (trigger is not null)
            {
                Dispatcher.UIThread.Post(
                    () => trigger.Focus(NavigationMethod.Tab),
                    DispatcherPriority.Input);
            }
        }
    }

    private void OpenAccel_Click(object? sender, RoutedEventArgs e)
    {
        AccelMenu.PlacementTarget = sender as Control;
        AccelMenu.IsOpen = !AccelMenu.IsOpen;
        if (AccelMenu.IsOpen && DataContext is MainWindowViewModel vm)
        {
            vm.RefreshAccelCommand.Execute(null);
        }
    }

    private void Exit_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.FlushPersist();
        }

        Close();
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PrepareSaveMenu();
        }

        SaveMenu.IsOpen = !SaveMenu.IsOpen;
    }

    private void ApplySidebarWidth(double width)
    {
        if (width <= 0)
        {
            return;
        }

        SetClassOn(Sidebar, "narrow", width < 348);
        SetClassOn(Sidebar, "tight", width < 300);
        ApplyThumbTileSize(width);
        ApplyToastWidth(ClientSize.Width);
        var inner = Math.Max(72, width - HorizontalSidebarPad(width) - 4);
        if (AvailableThumbs is not null)
        {
            AvailableThumbs.MaxWidth = inner;
            AvailableThumbs.InvalidateMeasure();
        }

        if (LoadedThumbs is not null)
        {
            LoadedThumbs.MaxWidth = inner;
            LoadedThumbs.InvalidateMeasure();
        }

        if (PipelineChips is not null)
        {
            PipelineChips.MaxWidth = inner;
            PipelineChips.InvalidateMeasure();
        }

        if (ProcessModelHost is not null)
        {
            ProcessModelHost.MaxWidth = Math.Max(160, width - 36);
        }

        if (ModelSourceGrid is not null)
        {
            ModelSourceGrid.Columns = width < 300 ? 1 : 2;
        }

        if (ModelActionGrid is not null)
        {
            ModelActionGrid.Columns = width < 280 ? 1 : 3;
        }
        if (ApplyRow is null || RunButton is null || BatchButton is null)
        {
            return;
        }

        if (width < 300)
        {
            ApplyRow.ColumnDefinitions = new ColumnDefinitions("*");
            ApplyRow.RowDefinitions = new RowDefinitions("Auto,6,Auto");
            Grid.SetColumn(RunButton, 0);
            Grid.SetRow(RunButton, 0);
            Grid.SetColumn(BatchButton, 0);
            Grid.SetRow(BatchButton, 2);
            BatchButton.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            BatchButton.MinWidth = 0;
        }
        else
        {
            ApplyRow.ColumnDefinitions = new ColumnDefinitions("*,8,Auto");
            ApplyRow.RowDefinitions = new RowDefinitions("Auto");
            Grid.SetColumn(RunButton, 0);
            Grid.SetRow(RunButton, 0);
            Grid.SetColumn(BatchButton, 2);
            Grid.SetRow(BatchButton, 0);
            BatchButton.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            BatchButton.MinWidth = 72;
        }
    }

    private void ApplyThumbTileSize(double width)
    {
        var pad = width < 300 ? 16 : width < 348 ? 20 : 28;
        var gap = width < 348 ? 6.0 : 8.0;
        var preferred = width < 300 ? 64.0 : width < 348 ? 70.0 : 76.0;
        var inner = Math.Max(preferred, width - pad);
        var columns = Math.Max(1, (int)Math.Floor((inner + gap) / (preferred + gap)));
        var tile = Math.Floor((inner / columns) - gap);
        tile = Math.Clamp(tile, 52, 96);
        Sidebar.Resources["ThumbTileLength"] = tile;
    }

    private void BatchButton_Click(object? sender, RoutedEventArgs e)
    {
        BatchMenu.IsOpen = !BatchMenu.IsOpen;
    }

    private void CloseBatchMenu(object? sender, RoutedEventArgs e)
    {
        BatchMenu.IsOpen = false;
    }

    private void OnDropZonePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.OpenImagesCommand.Execute(null);
        }

        e.Handled = true;
    }

    private void OnRemoveThumbClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: SessionItemViewModel item } && DataContext is MainWindowViewModel vm)
        {
            vm.RemoveItemCommand.Execute(item);
        }

        e.Handled = true;
    }

    private async void OnSessionThumbPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button || sender is not Control { Tag: SessionItemViewModel item } control || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var mods = e.KeyModifiers;
        if (mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Shift))
        {
            vm.HandleSessionClick(item, mods.HasFlag(KeyModifiers.Control), mods.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
            return;
        }

        e.Handled = true;
        vm.HandleSessionClick(item, toggle: false, range: false);
        if (e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            await MaybeStartThumbDragAsync(e, control, () => CreatePathTransfer(vm.SessionDragPaths(item)))
                .ConfigureAwait(true);
        }
    }

    private void OnSessionThumbKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) ||
            sender is not Control { Tag: SessionItemViewModel item } ||
            DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        vm.HandleSessionClick(
            item,
            e.KeyModifiers.HasFlag(KeyModifiers.Control),
            e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        e.Handled = true;
    }

    private void OnCatalogThumbPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button ||
            sender is not Control { Tag: CatalogItemViewModel item } control ||
            DataContext is not MainWindowViewModel)
        {
            return;
        }

        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        BeginCatalogGesture(e, control, item, e.KeyModifiers.HasFlag(KeyModifiers.Control));
    }

    private void OnCatalogThumbKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) ||
            sender is not Control { Tag: CatalogItemViewModel item } ||
            DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        vm.HandleCatalogClick(item, e.KeyModifiers.HasFlag(KeyModifiers.Control));
        e.Handled = true;
    }

    private void OnCatalogWhiteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: CatalogItemViewModel item } && DataContext is MainWindowViewModel vm)
        {
            vm.UseWhitePath(item.Path);
        }

        e.Handled = true;
    }

    private void OnSessionWhiteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: SessionItemViewModel item } && DataContext is MainWindowViewModel vm)
        {
            vm.UseWhitePath(item.Path);
        }

        e.Handled = true;
    }

    private void OnRemoveCatalogFolderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: CatalogFolderViewModel folder } && DataContext is MainWindowViewModel vm)
        {
            vm.RemoveCatalogFolderCommand.Execute(folder);
        }

        e.Handled = true;
    }

    private long _lastPeekMs;

    private void OnStageMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control control || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - _lastPeekMs < 32)
        {
            return;
        }

        _lastPeekMs = now;
        var pixel = e.GetPosition(control);
        try
        {
            vm.PeekColor((int)pixel.X, (int)pixel.Y, pin: false);
            var host = StageFrame;
            var pos = e.GetPosition(host);
            vm.PlaceLoupe(pos.X, pos.Y, host.Bounds.Width, host.Bounds.Height);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnStageExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.HideLoupe();
        }
    }

    private void OnRenditionHover(object? sender, PlotHoverEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (e.Patch is null || sender is not Control plot)
        {
            vm.HidePlotHover();
            return;
        }

        var host = PlotHoverHost;
        var pos = plot.TranslatePoint(e.Local, host) ?? e.Local;
        vm.PeekPlot(e.Patch, e.Index, e.Corrected, pos.X, pos.Y, host.Bounds.Width, host.Bounds.Height);
    }

    private void OnStageContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control control || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (!e.TryGetPosition(control, out var point))
        {
            return;
        }

        vm.PeekColor((int)point.X, (int)point.Y, pin: true, showLoupe: false);
        var host = StageFrame;
        var pos = e.TryGetPosition(host, out var hostPos) ? hostPos : point;
        vm.PlaceLoupe(pos.X, pos.Y, host.Bounds.Width, host.Bounds.Height);
    }

    private void OnStageReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control control || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (e.InitialPressMouseButton != MouseButton.Left || OriginalStage.DidPan)
        {
            return;
        }

        var point = e.GetPosition(control);
        vm.PeekColor((int)point.X, (int)point.Y, pin: true);
        var host = StageFrame;
        var pos = e.GetPosition(host);
        vm.PlaceLoupe(pos.X, pos.Y, host.Bounds.Width, host.Bounds.Height);
        e.Handled = true;
    }

    private void OnStageFit(object? sender, RoutedEventArgs e)
    {
        OriginalStage.Fit();
        e.Handled = true;
    }

    private async void OnSaveDeltaEPlot(object? sender, RoutedEventArgs e)
    {
        await SavePlotsAsync("Save ΔE plot", "deltae.png", (PlotDeltaE, PlotKind.StageBars, ""));
        e.Handled = true;
    }

    private async void OnSaveColorPlots(object? sender, RoutedEventArgs e)
    {
        await SavePlotsAsync("Save color plots", "color.png",
            (PlotRgb, PlotKind.Scatter, "_scatter"),
            (PlotResidual, PlotKind.Residual, "_residual"));
        e.Handled = true;
    }

    private async void OnSaveRenditionPlots(object? sender, RoutedEventArgs e)
    {
        await SavePlotsAsync("Save rendition plots", "rendition.png",
            (PlotRenditionOriginal, PlotKind.RenditionOriginal, "_original"),
            (PlotRenditionCorrected, PlotKind.RenditionCorrected, "_corrected"));
        e.Handled = true;
    }

    private async System.Threading.Tasks.Task SavePlotsAsync(
        string title,
        string suggested,
        params (PatchPlotControl Control, PlotKind Kind, string Suffix)[] plots)
    {
        if (DataContext is not MainWindowViewModel vm || vm.Dialogs is null || vm.Report is null)
        {
            return;
        }

        var path = await vm.Dialogs.SavePlotAsync(title, suggested);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (plots.Length == 1)
            {
                PlotExport.Save(path, vm.Report, plots[0].Kind, plots[0].Control);
            }
            else
            {
                foreach (var plot in plots)
                {
                    PlotExport.Save(PlotExport.WithSuffix(path, plot.Suffix), vm.Report, plot.Kind, plot.Control);
                }
            }

            ToastService.ShowSuccess(plots.Length == 1
                ? $"Saved {Path.GetFileName(path)}"
                : $"Saved {plots.Length} plots");
        }
        catch (Exception ex)
        {
            ToastService.ShowError(ex.Message);
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1)
        {
            e.Handled = true;
            OpenHelp_Click(HelpButton, e);
            return;
        }

        if (e.Key == Key.Escape && _activeSettingsDialog is { } settingsDialog)
        {
            e.Handled = true;
            settingsDialog.Close();
            return;
        }

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            SaveMenu.IsOpen = false;
            BatchMenu.IsOpen = false;
            AccelMenu.IsOpen = false;
            vm.ClearPicker();
            e.Handled = true;
            return;
        }

        if (e.Source is TextBox)
        {
            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (alt && !control && !shift && e.Key is Key.Left or Key.Right)
        {
            var command = e.Key == Key.Left ? vm.PreviousSessionCommand : vm.NextSessionCommand;
            if (command.CanExecute(null))
            {
                command.Execute(null);
                BringActiveSessionIntoView();
            }

            e.Handled = true;
            return;
        }

        if (control && shift && e.Key == Key.O)
        {
            vm.OpenFolderCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (control && alt && e.Key == Key.O)
        {
            vm.LoadSessionCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (control && !shift && !alt && e.Key == Key.O)
        {
            vm.OpenImagesCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (control && shift && e.Key == Key.S)
        {
            vm.SaveSessionAsCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (control && !shift && e.Key == Key.S && SaveLink.IsEnabled)
        {
            vm.PrepareSaveMenu();
            SaveMenu.IsOpen = true;
            e.Handled = true;
            return;
        }

        if (control && !shift && e.Key == Key.B && vm.CanOpenBatch)
        {
            BatchMenu.IsOpen = true;
            e.Handled = true;
            return;
        }

        if (control && !shift && e.Key == Key.T)
        {
            vm.ToggleThemeCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (control && alt && e.Key == Key.A)
        {
            AccelMenu.PlacementTarget = TopBar;
            AccelMenu.IsOpen = true;
            vm.RefreshAccelCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5 && vm.ApplyCommand.CanExecute(null))
        {
            vm.ApplyCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete && vm.RemoveSelectedCommand.CanExecute(null))
        {
            vm.RemoveSelectedCommand.Execute(null);
            e.Handled = true;
            return;
        }

    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (IsInternalPaths(e) || _internalPathDrag)
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var ok = CanAcceptPaths(e);
        SetDropActive(e.DataTransfer.Contains(DataFormat.File));
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (IsInternalPaths(e) || _internalPathDrag)
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var ok = CanAcceptPaths(e);
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            SetDropActive(true);
        }

        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => SetDropActive(false);

    private void SetDropActive(bool on)
    {
        DragOverlay.IsVisible = on;
        if (on)
        {
            DropZone.Classes.Add("is-dragging");
        }
        else
        {
            DropZone.Classes.Remove("is-dragging");
        }
    }

    private void OnWhiteDragOver(object? sender, DragEventArgs e)
    {
        var ok = CanAcceptPaths(e);
        SetClassOn(WhiteDrop, "is-dragging", ok);
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWhiteDragLeave(object? sender, DragEventArgs e)
    {
        SetClassOn(WhiteDrop, "is-dragging", false);
    }

    private void OnSessionDragOver(object? sender, DragEventArgs e)
    {
        var ok = CanAcceptPaths(e);
        SetClassOn(LoadedSection, "is-dragging", ok);
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnSessionDragLeave(object? sender, DragEventArgs e)
    {
        SetClassOn(LoadedSection, "is-dragging", false);
    }

    private static void SetClassOn(StyledElement target, string name, bool on)
    {
        if (on)
        {
            if (!target.Classes.Contains(name))
            {
                target.Classes.Add(name);
            }
        }
        else
        {
            target.Classes.Remove(name);
        }
    }

    private void OnWhiteDrop(object? sender, DragEventArgs e)
    {
        SetDropActive(false);
        SetClassOn(WhiteDrop, "is-dragging", false);
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var paths = ReadDropPaths(e);
        if (paths.Count == 0)
        {
            return;
        }

        vm.UseWhitePath(paths[0]);
        e.Handled = true;
    }

    private void OnSessionDrop(object? sender, DragEventArgs e)
    {
        SetDropActive(false);
        SetClassOn(LoadedSection, "is-dragging", false);
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var paths = ReadDropPaths(e);
        if (paths.Count == 0)
        {
            return;
        }

        vm.AddPaths(paths, collapseLibrary: true);
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        SetDropActive(false);
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var paths = ReadDropPaths(e);
        if (paths.Count == 0 || IsInternalPaths(e) || _internalPathDrag)
        {
            return;
        }

        vm.AddPaths(paths);
        e.Handled = true;
    }

    private static bool IsInternalPaths(DragEventArgs e) =>
        e.DataTransfer.TryGetText() is { } text && text.StartsWith("ccp-files", StringComparison.Ordinal);

    private static bool CanAcceptPaths(DragEventArgs e) =>
        IsInternalPaths(e) || _internalPathDrag
        || (e.DataTransfer.Contains(DataFormat.File)
            && ReadDropPaths(e).Any(SupportedStillImage.IsSupportedFile));

    private static List<string> ReadDropPaths(DragEventArgs e)
    {
        var paths = new List<string>();
        if (e.DataTransfer.TryGetText() is { } text && text.StartsWith("ccp-files", StringComparison.Ordinal))
        {
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.Equals("ccp-files", StringComparison.Ordinal) || !File.Exists(line))
                {
                    continue;
                }

                paths.Add(line);
            }

            return paths;
        }

        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
        {
            return paths;
        }

        foreach (var item in files)
        {
            var path = item.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (Directory.Exists(path))
            {
                paths.AddRange(Directory.EnumerateFiles(path));
            }
            else
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    private const double ThumbDragThreshold = 6;
    private static bool _internalPathDrag;
    private Control? _catalogGestureHost;
    private CatalogItemViewModel? _catalogGestureItem;
    private IPointer? _catalogGesturePointer;
    private Point _catalogGestureOrigin;
    private bool _catalogGestureCtrl;
    private bool _catalogDidDrag;
    private bool _catalogGestureLive;

    private void BeginCatalogGesture(
        PointerPressedEventArgs e,
        Control host,
        CatalogItemViewModel item,
        bool ctrl)
    {
        EndCatalogGesture(commit: false, windowPoint: null);
        _catalogGestureHost = host;
        _catalogGestureItem = item;
        _catalogGesturePointer = e.Pointer;
        _catalogGestureOrigin = e.GetPosition(this);
        _catalogGestureCtrl = ctrl;
        _catalogDidDrag = false;
        _catalogGestureLive = true;
        e.Pointer.Capture(host);
        AddHandler(InputElement.PointerMovedEvent, OnCatalogGestureMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(InputElement.PointerReleasedEvent, OnCatalogGestureReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        host.AddHandler(InputElement.PointerCaptureLostEvent, OnCatalogGestureCaptureLost);
    }

    private void OnCatalogGestureMoved(object? sender, PointerEventArgs e)
    {
        if (!_catalogGestureLive || e.Pointer != _catalogGesturePointer)
        {
            return;
        }

        var pos = e.GetPosition(this);
        var delta = pos - _catalogGestureOrigin;
        if (!_catalogDidDrag && (Math.Abs(delta.X) >= ThumbDragThreshold || Math.Abs(delta.Y) >= ThumbDragThreshold))
        {
            _catalogDidDrag = true;
            Cursor = new Cursor(StandardCursorType.DragCopy);
        }

        if (_catalogDidDrag)
        {
            SetClassOn(LoadedSection, "is-dragging", Hits(LoadedSection, pos));
            SetClassOn(WhiteDrop, "is-dragging", Hits(WhiteDrop, pos));
        }
    }

    private void OnCatalogGestureReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_catalogGestureLive || e.Pointer != _catalogGesturePointer)
        {
            return;
        }

        EndCatalogGesture(commit: true, windowPoint: e.GetPosition(this));
    }

    private void OnCatalogGestureCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_catalogGestureLive)
        {
            EndCatalogGesture(commit: false, windowPoint: null);
        }
    }

    private void EndCatalogGesture(bool commit, Point? windowPoint)
    {
        if (!_catalogGestureLive)
        {
            return;
        }

        var host = _catalogGestureHost;
        var item = _catalogGestureItem;
        var ctrl = _catalogGestureCtrl;
        var dragged = _catalogDidDrag;
        var pointer = _catalogGesturePointer;
        _catalogGestureLive = false;
        _catalogGestureHost = null;
        _catalogGestureItem = null;
        _catalogGesturePointer = null;
        _catalogDidDrag = false;
        Cursor = Cursor.Default;
        SetClassOn(LoadedSection, "is-dragging", false);
        SetClassOn(WhiteDrop, "is-dragging", false);
        RemoveHandler(InputElement.PointerMovedEvent, OnCatalogGestureMoved);
        RemoveHandler(InputElement.PointerReleasedEvent, OnCatalogGestureReleased);
        host?.RemoveHandler(InputElement.PointerCaptureLostEvent, OnCatalogGestureCaptureLost);
        if (pointer is not null && pointer.Captured == host)
        {
            pointer.Capture(null);
        }

        if (DataContext is not MainWindowViewModel vm || item is null)
        {
            return;
        }

        if (commit && dragged && windowPoint is { } pos)
        {
            if (Hits(WhiteDrop, pos))
            {
                vm.UseWhitePath(item.Path);
                return;
            }

            if (Hits(LoadedSection, pos))
            {
                vm.AddPaths(vm.CatalogDragPaths(item), collapseLibrary: false);
            }

            return;
        }

        if (commit && !dragged)
        {
            vm.HandleCatalogClick(item, toggle: ctrl);
        }
    }

    private bool Hits(Control? target, Point windowPoint)
    {
        if (target is not { IsVisible: true, IsEffectivelyVisible: true })
        {
            return false;
        }

        var local = this.TranslatePoint(windowPoint, target);
        return local is { } point && new Rect(target.Bounds.Size).Contains(point);
    }

    private async System.Threading.Tasks.Task MaybeStartThumbDragAsync(
        PointerPressedEventArgs e,
        Control control,
        Func<DataTransfer> createTransfer)
    {
        if (await WaitForThumbDragAsync(e, control).ConfigureAwait(true))
        {
            await RunPathDragAsync(e, createTransfer).ConfigureAwait(true);
        }
    }

    private async System.Threading.Tasks.Task RunPathDragAsync(
        PointerPressedEventArgs e,
        Func<DataTransfer> createTransfer)
    {
        _internalPathDrag = true;
        try
        {
            await DragDrop.DoDragDropAsync(e, createTransfer(), DragDropEffects.Copy).ConfigureAwait(true);
        }
        finally
        {
            _internalPathDrag = false;
        }
    }

    private async System.Threading.Tasks.Task<bool> WaitForThumbDragAsync(
        PointerPressedEventArgs e,
        Control control)
    {
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            return false;
        }

        var origin = e.GetPosition(this);
        var pointer = e.Pointer;
        pointer.Capture(control);
        var finished = new System.Threading.Tasks.TaskCompletionSource<bool>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

        void Moved(object? _, PointerEventArgs ev)
        {
            if (ev.Pointer != pointer)
            {
                return;
            }

            var delta = ev.GetPosition(this) - origin;
            if (Math.Abs(delta.X) >= ThumbDragThreshold || Math.Abs(delta.Y) >= ThumbDragThreshold)
            {
                finished.TrySetResult(true);
            }
        }

        void Released(object? _, PointerReleasedEventArgs ev)
        {
            if (ev.Pointer == pointer)
            {
                finished.TrySetResult(false);
            }
        }

        void CaptureLost(object? _, PointerCaptureLostEventArgs ev) => finished.TrySetResult(false);

        AddHandler(InputElement.PointerMovedEvent, Moved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(InputElement.PointerReleasedEvent, Released, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        control.AddHandler(InputElement.PointerCaptureLostEvent, CaptureLost);
        try
        {
            if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            {
                return false;
            }

            var isDrag = await finished.Task.ConfigureAwait(true);
            if (pointer.Captured == control)
            {
                pointer.Capture(null);
            }

            return isDrag;
        }
        finally
        {
            RemoveHandler(InputElement.PointerMovedEvent, Moved);
            RemoveHandler(InputElement.PointerReleasedEvent, Released);
            control.RemoveHandler(InputElement.PointerCaptureLostEvent, CaptureLost);
            if (pointer.Captured == control)
            {
                pointer.Capture(null);
            }
        }
    }

    private static DataTransfer CreatePathTransfer(IReadOnlyList<string> paths)
    {
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText("ccp-files\n" + string.Join("\n", paths)));
        return transfer;
    }
}
