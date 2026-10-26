using System;
using Avalonia;
using Avalonia.Controls;

namespace ColorCorrect.App.Views;

internal static class DialogLayout
{
    public static void FitToOwner(Window dialog)
    {
        if (dialog.Owner is not Window owner)
        {
            return;
        }

        var maxW = Math.Max(280, owner.ClientSize.Width - 16);
        var maxH = Math.Max(280, owner.ClientSize.Height - 16);
        var width = dialog.Width > 1 ? dialog.Width : 460;
        var height = dialog.Height > 1 ? dialog.Height : 480;

        dialog.SizeToContent = SizeToContent.Manual;
        dialog.MaxWidth = maxW;
        dialog.MaxHeight = maxH;
        dialog.MinWidth = Math.Min(280, maxW);
        dialog.MinHeight = Math.Min(240, maxH);
        dialog.Width = Math.Min(width, maxW);
        dialog.Height = Math.Min(height, maxH);

        // Window positions use physical pixels; widths and client sizes use DIPs.
        var x = owner.Position.X + (int)Math.Round((owner.Bounds.Width - dialog.Width) * owner.RenderScaling / 2);
        var y = owner.Position.Y + (int)Math.Round((owner.Bounds.Height - dialog.Height) * owner.RenderScaling / 2);
        dialog.Position = new PixelPoint(x, y);

        // The settings DockPanel gives its final ScrollViewer the remaining
        // finite height. An opening-time MaxHeight becomes stale after resize
        // and centers a clipped viewport inside an otherwise larger dialog.
        dialog.InvalidateMeasure();
        dialog.InvalidateArrange();
    }
}
