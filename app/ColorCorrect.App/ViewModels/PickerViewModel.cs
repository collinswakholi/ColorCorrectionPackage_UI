using System;
using Avalonia.Media;
using ColorCorrect.App.Models;
using ColorCorrect.App.Services;
using ColorCorrect.Engine;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ColorCorrect.App.ViewModels;

/// <summary>
/// Color picker, loupe, pin and hover-card state for the stage and the rendition plots.
/// The shell samples the preview bitmap (<see cref="MainWindowViewModel.PeekColor"/>) and
/// hands the result to <see cref="Show"/>; everything shown about a pick lives here.
/// </summary>
public partial class PickerViewModel : ViewModelBase
{
    public const string IdleHint = "Hover for RGB / Lab · scroll to zoom · drag to pan";
    public const string ClearedHint = "Hover for RGB / Lab. Right-click a point to copy. Esc clears the pin.";

    private int _plotHoverIndex = -1;

    [ObservableProperty] private string _hint = IdleHint;
    [ObservableProperty] private string _pixel = "";
    [ObservableProperty] private string _rgb = "";
    [ObservableProperty] private string _lab = "";
    [ObservableProperty] private string _r = "";
    [ObservableProperty] private string _g = "";
    [ObservableProperty] private string _b = "";
    [ObservableProperty] private string _hex = "";
    [ObservableProperty] private string _l = "";
    [ObservableProperty] private string _a = "";
    [ObservableProperty] private string _labB = "";
    [ObservableProperty] private IBrush? _brush;
    [ObservableProperty] private bool _hasPick;
    [ObservableProperty] private bool _showLoupe;
    [ObservableProperty] private bool _showPin;
    [ObservableProperty] private double _pinX;
    [ObservableProperty] private double _pinY;
    [ObservableProperty] private int _loupePixelX;
    [ObservableProperty] private int _loupePixelY;
    [ObservableProperty] private double _loupeLeft;
    [ObservableProperty] private double _loupeTop;
    [ObservableProperty] private double _hoverCardLeft;
    [ObservableProperty] private double _hoverCardTop;

    /// <summary>Hover card for the stage image.</summary>
    public ColorHoverState StageHover { get; } = new();

    /// <summary>Hover card for the chart-rendition plots.</summary>
    public ColorHoverState PlotHover { get; } = new();

    /// <summary>The last picked color, in source-image coordinates (used for copying).</summary>
    public PickedColor? LastPick { get; private set; }

    /// <summary>Whether a right-click pin holds the current pick until cleared.</summary>
    public bool IsPinned { get; private set; }

    /// <summary>Preview-pixel coordinates of the current pick.</summary>
    public int PreviewX { get; private set; }

    /// <summary>Preview-pixel coordinates of the current pick.</summary>
    public int PreviewY { get; private set; }

    /// <summary>Show a sampled color. <paramref name="pinX"/>/<paramref name="pinY"/> are view coordinates for the pin marker.</summary>
    public void Show(PickedColor picked, int previewX, int previewY, bool showLoupe, bool pin, double pinX, double pinY)
    {
        LastPick = picked;
        PreviewX = previewX;
        PreviewY = previewY;
        HasPick = true;
        Hint = "";
        Pixel = picked.Pixel;
        Rgb = picked.Rgb;
        Lab = picked.Lab;
        R = picked.R.ToString();
        G = picked.G.ToString();
        B = picked.B.ToString();
        Hex = picked.Hex;
        L = picked.L.ToString("0.00");
        A = picked.A.ToString("0.00");
        LabB = picked.LabB.ToString("0.00");
        if (Brush is SolidColorBrush brush)
        {
            brush.Color = picked.Color;
        }
        else
        {
            Brush = new SolidColorBrush(picked.Color);
        }

        LoupePixelX = previewX;
        LoupePixelY = previewY;
        ShowLoupe = showLoupe;
        if (pin)
        {
            IsPinned = true;
            ShowPin = true;
            PinX = pinX;
            PinY = pinY;
        }
    }

    /// <summary>Position the loupe and its value card next to the pointer, inside the host.</summary>
    public void PlaceLoupe(double pointerX, double pointerY, double hostW, double hostH)
    {
        const double size = 88;
        const double offset = 18;
        var left = pointerX + offset;
        var top = pointerY - size - 8;
        if (left + size > hostW - 8)
        {
            left = pointerX - size - offset;
        }

        if (top < 8)
        {
            top = pointerY + offset;
        }

        LoupeLeft = Math.Max(0, left);
        LoupeTop = Math.Max(0, top);

        const double cardW = 252;
        const double cardH = 118;
        const double gap = 10;
        var cardLeft = LoupeLeft + size + gap;
        var cardTop = LoupeTop + (size - cardH) / 2;
        if (cardLeft + cardW > hostW - 8)
        {
            cardLeft = LoupeLeft - cardW - gap;
        }

        if (cardLeft < 8)
        {
            cardLeft = Math.Clamp(pointerX + offset, 8, Math.Max(8, hostW - cardW - 8));
            cardTop = LoupeTop + size + gap;
            if (cardTop + cardH > hostH - 8)
            {
                cardTop = LoupeTop - cardH - gap;
            }
        }

        HoverCardLeft = Math.Clamp(cardLeft, 8, Math.Max(8, hostW - cardW - 8));
        HoverCardTop = Math.Clamp(cardTop, 8, Math.Max(8, hostH - cardH - 8));
    }

    public void HideLoupe() => ShowLoupe = false;

    public void PeekPlot(PatchRow row, int index, bool corrected, double x, double y, double hostW, double hostH)
    {
        if (index != _plotHoverIndex || !PlotHover.IsVisible)
        {
            PlotHover.ShowPatch(row, corrected);
            _plotHoverIndex = index;
        }

        PlotHover.Place(x, y, hostW, hostH, 392, 206);
    }

    public void HidePlotHover()
    {
        _plotHoverIndex = -1;
        PlotHover.Hide();
    }

    /// <summary>Forget the pick and pin and hide every picker overlay.</summary>
    public void Clear()
    {
        LastPick = null;
        IsPinned = false;
        HasPick = false;
        ShowLoupe = false;
        ShowPin = false;
        StageHover.Hide();
        HidePlotHover();
        Hint = ClearedHint;
        Pixel = Rgb = Lab = "";
        R = G = B = Hex = "";
        L = A = LabB = "";
        Brush = null;
    }
}
