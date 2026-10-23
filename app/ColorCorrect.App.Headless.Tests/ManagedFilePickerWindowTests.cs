using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ColorCorrect.App.Services;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class ManagedFilePickerWindowTests
{
    [AvaloniaFact]
    public async Task EscapeCancelsTheActualManagedStorageProviderFromThePathBox()
    {
        var owner = new Window { Width = 1000, Height = 750 };
        owner.Show();
        // App startup used the production registration helper. Resolve its real
        // registered provider rather than injecting options/factories here.
        var provider = owner.StorageProvider;
        Assert.Equal("ManagedStorageProvider", provider.GetType().Name);
        var result = provider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Cancel picker regression" });
        Dispatcher.UIThread.RunJobs();
        var opened = Assert.Single(owner.OwnedWindows);
        try
        {
            var picker = Assert.IsType<ManagedFilePickerWindow>(opened);
            Assert.True(picker.Width <= 900 && picker.Height <= 620);
            var path = picker.GetVisualDescendants().OfType<TextBox>().First();
            path.Focus();
            Assert.True(path.IsFocused);
            EventHandler<KeyEventArgs> childHandlesEscape = (_, args) =>
            {
                if (args.Key == Key.Escape) args.Handled = true;
            };
            path.KeyDown += childHandlesEscape;
            picker.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.False(result.IsCompleted, "a child-consumed Escape must not dismiss the picker");
            Assert.True(picker.IsVisible);
            path.KeyDown -= childHandlesEscape;
            picker.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(result, await Task.WhenAny(result, Task.Delay(500)));
            Assert.Empty(await result);
            Assert.Empty(owner.OwnedWindows);
        }
        finally
        {
            opened.Close();
            owner.Close();
        }
    }

    [Theory]
    [InlineData(1440, 900, 1, 3840, 2160, 900, 620)]
    [InlineData(1440, 900, 1.5, 3840, 2160, 900, 620)]
    [InlineData(1440, 900, 2, 3840, 2160, 900, 620)]
    [InlineData(520, 380, 1, 1920, 1080, 488, 348)]
    [InlineData(1440, 900, 2, 1024, 768, 512, 384)]
    [InlineData(280, 240, 2, 1920, 1080, 248, 208)]
    public void PickerFitsOwnerAndPhysicalWorkingAreaAtEachScale(
        double ownerWidth, double ownerHeight, double scale, int screenWidth, int screenHeight,
        double expectedWidth, double expectedHeight)
    {
        var work = new PixelRect(-screenWidth, 0, screenWidth, screenHeight);
        var placement = ManagedFilePickerWindow.Place(new Size(ownerWidth, ownerHeight),
            new PixelPoint(-screenWidth + 80, -60), scale, work);
        Assert.Equal(expectedWidth, placement.Size.Width);
        Assert.Equal(expectedHeight, placement.Size.Height);
        Assert.True(placement.Position.X >= work.X && placement.Position.Y >= work.Y);
        Assert.True(placement.Position.X + placement.Size.Width * scale <= work.Right + 0.5);
        Assert.True(placement.Position.Y + placement.Size.Height * scale <= work.Bottom + 0.5);
        Assert.True(placement.Maximum.Width <= ownerWidth && placement.Maximum.Height <= ownerHeight);
    }

    [Fact]
    public void InvalidOwnerMeasurementsHaveFiniteVisibleFallback()
    {
        var placement = ManagedFilePickerWindow.Place(new Size(double.NaN, 0),
            new PixelPoint(-9000, 9000), double.NaN, new PixelRect(0, 0, 800, 600));
        Assert.True(double.IsFinite(placement.Size.Width) && double.IsFinite(placement.Size.Height));
        Assert.InRange(placement.Size.Width, 1, 800);
        Assert.InRange(placement.Size.Height, 1, 600);
        Assert.InRange(placement.Position.X, 0, 800 - (int)placement.Size.Width);
        Assert.InRange(placement.Position.Y, 0, 600 - (int)placement.Size.Height);
    }

    [AvaloniaFact]
    public void WideContentCannotExpandManagedPickerAndOwnerlessOpeningIsSafe()
    {
        var owner = new Window { Width = 720, Height = 500 };
        owner.Show();
        var picker = new ManagedFilePickerWindow { Content = new TextBlock { Text = new string('W', 4000) } };
        var closed = picker.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SizeToContent.Manual, picker.SizeToContent);
        Assert.True(picker.Width <= owner.ClientSize.Width && picker.Height <= owner.ClientSize.Height);
        Assert.True(picker.Width <= 900 && picker.Height <= 620);
        picker.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(closed.IsCompleted);
        owner.Close();

        var unowned = new ManagedFilePickerWindow();
        unowned.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(900, unowned.Width);
        Assert.Equal(620, unowned.Height);
        unowned.Close();
    }
}
