using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ColorCorrect.App.Views;

namespace ColorCorrect.App.Services;

// Used only by the WSL managed storage provider. Explicit dimensions prevent
// long file names/paths from sizing a new picker to the whole virtual desktop.
internal sealed class ManagedFilePickerWindow : Window
{
    public ManagedFilePickerWindow()
    {
        Width = 900;
        Height = 620;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
    }

    protected override void OnOpened(EventArgs e)
    {
        if (Owner is Window owner)
        {
            var screen = Screens.ScreenFromWindow(owner) ?? Screens.Primary;
            if (screen is not null)
            {
                var placement = Place(owner.ClientSize, owner.Position, owner.RenderScaling, screen.WorkingArea);
                MaxWidth = placement.Maximum.Width;
                MaxHeight = placement.Maximum.Height;
                MinWidth = Math.Min(480, placement.Size.Width);
                MinHeight = Math.Min(320, placement.Size.Height);
                Width = placement.Size.Width;
                Height = placement.Size.Height;
                Position = placement.Position;
            }
            else
            {
                DialogLayout.FitToOwner(this);
            }
        }
        base.OnOpened(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && !e.Handled)
        {
            // Closing lets the storage provider cancel its own view model and
            // return an empty selection. Child controls retain handled Escape.
            e.Handled = true;
            Close();
        }
    }

    internal static (Size Size, Size Maximum, PixelPoint Position) Place(
        Size ownerSize, PixelPoint ownerPosition, double scaling, PixelRect workArea)
    {
        if (!double.IsFinite(scaling) || scaling <= 0) scaling = 1;
        var ownerWidth = double.IsFinite(ownerSize.Width) && ownerSize.Width > 0 ? ownerSize.Width : 900;
        var ownerHeight = double.IsFinite(ownerSize.Height) && ownerSize.Height > 0 ? ownerSize.Height : 620;
        var screenWidth = Math.Max(1, workArea.Width);
        var screenHeight = Math.Max(1, workArea.Height);
        var maxWidth = Math.Max(1 / scaling, Math.Min(Math.Max(1 / scaling, ownerWidth - 32), screenWidth / scaling));
        var maxHeight = Math.Max(1 / scaling, Math.Min(Math.Max(1 / scaling, ownerHeight - 32), screenHeight / scaling));
        var width = Math.Min(900, maxWidth);
        var height = Math.Min(620, maxHeight);
        var pixelsWide = Math.Max(1, (int)Math.Round(width * scaling));
        var pixelsHigh = Math.Max(1, (int)Math.Round(height * scaling));
        var x = ownerPosition.X + (int)Math.Round((ownerWidth - width) * scaling / 2);
        var y = ownerPosition.Y + (int)Math.Round((ownerHeight - height) * scaling / 2);
        x = Math.Clamp(x, workArea.X, workArea.X + screenWidth - pixelsWide);
        y = Math.Clamp(y, workArea.Y, workArea.Y + screenHeight - pixelsHigh);
        return (new Size(width, height), new Size(maxWidth, maxHeight), new PixelPoint(x, y));
    }
}
