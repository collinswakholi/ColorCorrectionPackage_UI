using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace ColorCorrect.App.Controls;

/// <summary>
/// Wraps square-ish tiles and sizes each column so a row exactly fills
/// the available width — no leftover sliver, no clipped third tile.
/// </summary>
public sealed class TileWrapPanel : Panel
{
    public static readonly StyledProperty<double> MinTileProperty =
        AvaloniaProperty.Register<TileWrapPanel, double>(nameof(MinTile), 76);

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<TileWrapPanel, double>(nameof(Gap), 8);

    public double MinTile
    {
        get => GetValue(MinTileProperty);
        set => SetValue(MinTileProperty, value);
    }

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    static TileWrapPanel()
    {
        AffectsMeasure<TileWrapPanel>(MinTileProperty, GapProperty);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Parent is Control parent)
        {
            parent.SizeChanged += OnParentSizeChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (Parent is Control parent)
        {
            parent.SizeChanged -= OnParentSizeChanged;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnParentSizeChanged(object? sender, SizeChangedEventArgs e) => InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = ResolveWidth(availableSize.Width);
        var (columns, tile) = ColumnsAndTile(width);
        var gap = Gap;
        double y = 0;
        double rowHeight = 0;
        var col = 0;

        foreach (var child in Children)
        {
            ApplyTile(child, tile);
            child.Measure(new Size(tile, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            col++;
            if (col == columns)
            {
                y += rowHeight + gap;
                rowHeight = 0;
                col = 0;
            }
        }

        if (col > 0)
        {
            y += rowHeight;
        }
        else if (y > 0)
        {
            y -= gap;
        }

        return new Size(width, Math.Max(0, y));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = ResolveWidth(finalSize.Width);
        var (columns, tile) = ColumnsAndTile(width);
        var gap = Gap;
        double x = 0;
        double y = 0;
        double rowHeight = 0;
        var col = 0;

        foreach (var child in Children)
        {
            ApplyTile(child, tile);
            var height = child.DesiredSize.Height;
            child.Arrange(new Rect(x, y, tile, height));
            rowHeight = Math.Max(rowHeight, height);
            col++;
            x += tile + gap;
            if (col == columns)
            {
                x = 0;
                y += rowHeight + gap;
                rowHeight = 0;
                col = 0;
            }
        }

        return finalSize;
    }

    private (int Columns, double Tile) ColumnsAndTile(double width)
    {
        var gap = Math.Max(0, Gap);
        var min = Math.Max(32, MinTile);
        var columns = Math.Max(1, (int)Math.Floor((width + gap) / (min + gap)));
        var tile = Math.Floor((width - gap * (columns - 1)) / columns);
        tile = Math.Clamp(tile, 32, width);
        return (columns, tile);
    }

    private static void ApplyTile(Control child, double tile)
    {
        child.Width = tile;
        foreach (var grid in child.GetVisualDescendants().OfType<Grid>())
        {
            if (grid.Classes.Contains("thumbFrame"))
            {
                grid.Width = tile;
                grid.Height = tile;
            }
        }
    }

    private double ResolveWidth(double offered)
    {
        if (!double.IsInfinity(offered) && !double.IsNaN(offered) && offered > 1)
        {
            return offered;
        }

        if (Bounds.Width > 1)
        {
            return Bounds.Width;
        }

        if (Parent is Control parent && parent.Bounds.Width > 1)
        {
            return parent.Bounds.Width;
        }

        return 200;
    }
}
