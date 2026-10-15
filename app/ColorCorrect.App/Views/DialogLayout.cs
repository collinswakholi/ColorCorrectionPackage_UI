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

        dialog.Icon ??= owner.Icon;

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

        var x = owner.Position.X + (int)Math.Round((owner.Bounds.Width - dialog.Width) / 2);
        var y = owner.Position.Y + (int)Math.Round((owner.Bounds.Height - dialog.Height) / 2);
        dialog.Position = new PixelPoint(x, y);

        if (dialog.FindControl<ScrollViewer>("SettingsScroll") is { } scroll)
        {
            scroll.MaxHeight = Math.Max(120, dialog.Height - 150);
        }

        dialog.InvalidateMeasure();
        dialog.InvalidateArrange();
    }
}
