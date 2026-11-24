using Avalonia.Headless.XUnit;
using Avalonia.Media;
using ColorCorrect.App.Services;
using ColorCorrect.App.ViewModels;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class PickerViewModelTests
{
    private static PickedColor Sample => new(12, 34, 200, 100, 50, 55.5, 30.25, 40.125);

    [AvaloniaFact]
    public void ShowFillsEveryReadoutAndPinsOnlyWhenAsked()
    {
        var picker = new PickerViewModel();
        Assert.Equal(PickerViewModel.IdleHint, picker.Hint);

        picker.Show(Sample, 5, 6, showLoupe: true, pin: false, pinX: 50, pinY: 60);
        Assert.True(picker.HasPick);
        Assert.Equal("", picker.Hint);
        Assert.Equal(("200", "100", "50"), (picker.R, picker.G, picker.B));
        Assert.Equal(("55.50", "30.25", "40.13"), (picker.L, picker.A, picker.LabB));
        Assert.Equal(Sample.Hex, picker.Hex);
        Assert.Equal((5, 6), (picker.LoupePixelX, picker.LoupePixelY));
        Assert.True(picker.ShowLoupe);
        Assert.False(picker.IsPinned);
        Assert.False(picker.ShowPin);
        var brush = Assert.IsType<SolidColorBrush>(picker.Brush);

        picker.Show(Sample with { R = 1 }, 7, 8, showLoupe: false, pin: true, pinX: 70, pinY: 80);
        Assert.Same(brush, picker.Brush); // reused, recolored
        Assert.True(picker.IsPinned);
        Assert.True(picker.ShowPin);
        Assert.Equal((70d, 80d), (picker.PinX, picker.PinY));
        Assert.Equal((7, 8), (picker.PreviewX, picker.PreviewY));
        Assert.Equal((byte)1, picker.LastPick!.Value.R);
    }

    [AvaloniaFact]
    public void ClearResetsPickPinAndOverlays()
    {
        var picker = new PickerViewModel();
        picker.Show(Sample, 1, 1, showLoupe: true, pin: true, pinX: 1, pinY: 1);
        picker.Clear();
        Assert.False(picker.HasPick);
        Assert.False(picker.IsPinned);
        Assert.False(picker.ShowLoupe);
        Assert.False(picker.ShowPin);
        Assert.Null(picker.LastPick);
        Assert.Null(picker.Brush);
        Assert.Equal(PickerViewModel.ClearedHint, picker.Hint);
        Assert.Equal("", picker.Hex);
        Assert.False(picker.StageHover.IsVisible);
        Assert.False(picker.PlotHover.IsVisible);
    }

    [AvaloniaFact]
    public void LoupeAndCardStayInsideTheHost()
    {
        var picker = new PickerViewModel();
        picker.PlaceLoupe(990, 5, 1000, 700); // top-right corner
        Assert.InRange(picker.LoupeLeft, 0, 1000 - 88);
        Assert.InRange(picker.LoupeTop, 0, 700 - 88);
        Assert.InRange(picker.HoverCardLeft, 8, 1000 - 252 - 8);
        Assert.InRange(picker.HoverCardTop, 8, 700 - 118 - 8);
    }

    [AvaloniaFact]
    public void ShellForwardsPickerCommandsToThePickerViewModel()
    {
        var vm = new MainWindowViewModel(persistenceEnabled: false);
        vm.Picker.Show(Sample, 1, 1, showLoupe: true, pin: true, pinX: 1, pinY: 1);
        vm.HideLoupe();
        Assert.False(vm.Picker.ShowLoupe);
        vm.ClearPicker();
        Assert.False(vm.Picker.HasPick);
        Assert.Equal(PickerViewModel.ClearedHint, vm.Picker.Hint);
    }
}
