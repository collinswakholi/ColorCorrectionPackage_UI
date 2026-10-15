using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using ColorCorrect.App.Views;

namespace ColorCorrect.App.Services;

public sealed class WindowDialogs : IAppDialogs
{
    private static readonly string[] ImageExts = [".jpg", ".jpeg", ".png", ".tif", ".tiff"];

    private readonly Window _window;

    public WindowDialogs(Window window)
    {
        _window = window;
    }

    public async Task<IReadOnlyList<string>> OpenImagesAsync()
    {
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open images",
            AllowMultiple = true,
            FileTypeFilter = [ImageType()],
        });
        return LocalPaths(files);
    }

    public async Task<IReadOnlyList<string>> OpenFolderImagesAsync()
    {
        var folders = await _window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open folder of images",
            AllowMultiple = false,
        });
        if (folders.Count == 0)
        {
            return [];
        }

        var path = folders[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return [];
        }

        return Directory.EnumerateFiles(path)
            .Where(file => ImageExts.Contains(Path.GetExtension(file), System.StringComparer.OrdinalIgnoreCase))
            .OrderBy(file => file, System.StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await _window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public async Task<string?> OpenJsonAsync(string title)
    {
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [JsonType()],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> SaveJsonAsync(string title, string suggestedName)
    {
        var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            FileTypeChoices = [JsonType()],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> SaveImageAsync(string suggestedName)
    {
        var ext = Path.GetExtension(suggestedName);
        var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save corrected image",
            SuggestedFileName = suggestedName,
            DefaultExtension = string.IsNullOrWhiteSpace(ext) ? "png" : ext.TrimStart('.'),
            FileTypeChoices = [ImageType()],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> SavePlotAsync(string title, string suggestedName)
    {
        var ext = Path.GetExtension(suggestedName);
        var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = string.IsNullOrWhiteSpace(ext) ? "png" : ext.TrimStart('.'),
            FileTypeChoices =
            [
                new FilePickerFileType("PNG") { Patterns = ["*.png"] },
                new FilePickerFileType("JPEG") { Patterns = ["*.jpg", "*.jpeg"] },
                new FilePickerFileType("SVG") { Patterns = ["*.svg"] },
            ],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> OpenWhiteAsync()
    {
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "White reference",
            AllowMultiple = false,
            FileTypeFilter = [ImageType()],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> SaveCubeAsync(string suggestedName)
    {
        var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export LUT (GC+WB+CC, no FFC)",
            SuggestedFileName = suggestedName,
            DefaultExtension = "cube",
            FileTypeChoices = [new FilePickerFileType("IRIDAS cube") { Patterns = ["*.cube"] }],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<bool> ConfirmAsync(string title, string message, string? confirmLabel = null, bool danger = false)
    {
        var dialog = new ConfirmDialog(title, message, confirmLabel ?? "Continue", danger);
        var result = await dialog.ShowDialog<bool?>(_window);
        return result == true;
    }

    public async Task SetClipboardAsync(string text)
    {
        if (_window.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private static FilePickerFileType ImageType() => new("JPEG or PNG")
    {
        Patterns = ["*.jpg", "*.jpeg", "*.png", "*.tif", "*.tiff", "*.JPG", "*.JPEG", "*.PNG", "*.TIF", "*.TIFF"],
    };

    private static FilePickerFileType JsonType() => new("JSON")
    {
        Patterns = ["*.json"],
    };

    private static List<string> LocalPaths(IReadOnlyList<IStorageFile> files)
    {
        var list = new List<string>();
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                list.Add(path);
            }
        }

        return list;
    }
}
