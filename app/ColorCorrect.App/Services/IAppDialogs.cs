using System.Collections.Generic;
using System.Threading.Tasks;

namespace ColorCorrect.App.Services;

public interface IAppDialogs
{
    Task<IReadOnlyList<string>> OpenImagesAsync();
    Task<IReadOnlyList<string>> OpenFolderImagesAsync();
    Task<string?> PickFolderAsync(string title);
    Task<string?> OpenJsonAsync(string title);
    Task<string?> SaveJsonAsync(string title, string suggestedName);
    Task<string?> SaveImageAsync(string suggestedName);
    Task<string?> SavePlotAsync(string title, string suggestedName);
    Task<string?> OpenWhiteAsync();
    Task<string?> OpenVideoAsync();
    Task<string?> SaveCubeAsync(string suggestedName);
    Task<bool> ConfirmAsync(string title, string message, string? confirmLabel = null, bool danger = false);
    Task SetClipboardAsync(string text);
}
