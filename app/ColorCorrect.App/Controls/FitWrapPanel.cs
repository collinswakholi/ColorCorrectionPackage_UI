using System;
using Avalonia;
using Avalonia.Controls;

namespace ColorCorrect.App.Controls;

/// <summary>
/// WrapPanel that always measures against a finite width so children wrap
/// instead of spilling into a clipped sliver.
/// </summary>
public sealed class FitWrapPanel : WrapPanel
{
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Parent is Control parent)
        {
            parent.SizeChanged += OnParentSizeChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
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
        return base.MeasureOverride(new Size(width, availableSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = ResolveWidth(finalSize.Width);
        return base.ArrangeOverride(new Size(width, finalSize.Height));
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
