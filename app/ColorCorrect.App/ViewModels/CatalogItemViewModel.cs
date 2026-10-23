using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ColorCorrect.Engine;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ColorCorrect.App.ViewModels;

public partial class CatalogItemViewModel : ObservableObject, IDisposable
{
    private int _loadGen;

    public CatalogItemViewModel(SampleEntry entry)
    {
        Name = entry.Name;
        Path = entry.Path;
        Group = entry.Group;
        IsWhite = entry.IsWhite;
        _ = LoadThumbAsync();
    }

    public string Name { get; }
    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(Name);
    public string Path { get; }
    public string Group { get; }
    public bool IsWhite { get; }
    public string RoleHint => IsWhite
        ? "White still — click W or drag onto the white well"
        : "Click adds to session. Ctrl-click selects. Drag to Session or the white well.";

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private Bitmap? _thumb;

    public void Dispose()
    {
        _loadGen++;
        var old = Thumb;
        Thumb = null;
        old?.Dispose();
    }

    private async Task LoadThumbAsync()
    {
        var gen = ++_loadGen;
        Bitmap? bmp;
        try
        {
            bmp = await Task.Run(() =>
            {
                return ColorCorrect.App.Services.StillImageDecoder.Decode(Path, 72);
            }).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (bmp is null || gen != _loadGen)
        {
            bmp?.Dispose();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (gen != _loadGen)
            {
                bmp.Dispose();
                return;
            }

            var old = Thumb;
            Thumb = bmp;
            old?.Dispose();
        });
    }
}
