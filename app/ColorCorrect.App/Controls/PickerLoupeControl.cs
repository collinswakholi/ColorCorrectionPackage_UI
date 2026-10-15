using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ColorCorrect.App.Controls;

public sealed class PickerLoupeControl : Control
{
    public const int Radius = 5;
    public const int Zoom = 8;
    public const int Cells = Radius * 2 + 1;
    public const int PixelSize = Cells * Zoom;

    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<PickerLoupeControl, Bitmap?>(nameof(Source));

    public static readonly StyledProperty<int> PixelXProperty =
        AvaloniaProperty.Register<PickerLoupeControl, int>(nameof(PixelX));

    public static readonly StyledProperty<int> PixelYProperty =
        AvaloniaProperty.Register<PickerLoupeControl, int>(nameof(PixelY));

    private Color[] _cells = PixelSampler.EmptyNeighborhood(Radius);
    private readonly SolidColorBrush[] _cellBrushes = CreateBrushes();
    private readonly SolidColorBrush _innerRing = new(Color.FromArgb(204, 0, 0, 0));

    private static SolidColorBrush[] CreateBrushes()
    {
        var brushes = new SolidColorBrush[Cells * Cells];
        for (var i = 0; i < brushes.Length; i++)
        {
            brushes[i] = new SolidColorBrush();
        }

        return brushes;
    }

    static PickerLoupeControl()
    {
        AffectsRender<PickerLoupeControl>(SourceProperty, PixelXProperty, PixelYProperty);
    }

    public PickerLoupeControl()
    {
        Width = PixelSize;
        Height = PixelSize;
        MinWidth = PixelSize;
        MinHeight = PixelSize;
        ClipToBounds = true;
        IsHitTestVisible = false;
    }

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public int PixelX
    {
        get => GetValue(PixelXProperty);
        set => SetValue(PixelXProperty, value);
    }

    public int PixelY
    {
        get => GetValue(PixelYProperty);
        set => SetValue(PixelYProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == PixelXProperty || change.Property == PixelYProperty)
        {
            try
            {
                _cells = Source is { } bitmap
                    ? PixelSampler.SampleNeighborhood(bitmap, PixelX, PixelY, Radius)
                    : PixelSampler.EmptyNeighborhood(Radius);
            }
            catch (System.ObjectDisposedException)
            {
                _cells = PixelSampler.EmptyNeighborhood(Radius);
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(PixelSize, PixelSize);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var local = new Rect(0, 0, PixelSize, PixelSize);
        using (context.PushClip(local))
        {
            context.FillRectangle(Brushes.Black, local);
            for (var row = 0; row < Cells; row++)
            {
                for (var col = 0; col < Cells; col++)
                {
                    var i = row * Cells + col;
                    _cellBrushes[i].Color = _cells[i];
                    context.FillRectangle(_cellBrushes[i], new Rect(col * Zoom, row * Zoom, Zoom, Zoom));
                }
            }

            var cx = Radius * Zoom;
            context.DrawRectangle(new Pen(Brushes.White, 1), new Rect(cx + 0.5, cx + 0.5, Zoom - 1, Zoom - 1));
            context.DrawRectangle(new Pen(_innerRing, 1), new Rect(cx + 1.5, cx + 1.5, Zoom - 3, Zoom - 3));
        }
    }
}
