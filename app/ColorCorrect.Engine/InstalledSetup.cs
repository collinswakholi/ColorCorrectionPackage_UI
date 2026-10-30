using System.Text.Json;

namespace ColorCorrect.Engine;

/// <summary>Per-user component selection written by the platform setup helper.</summary>
public static class InstalledSetup
{
    public static string StatePath => Path.Combine(OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ColorCorrectStudio")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "colorcorrect"), "setup-state.json");

    public static string? ComponentAllowlist(string? path = null)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path ?? StatePath));
            if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!json.RootElement.TryGetProperty("components", out var components) || components.ValueKind != JsonValueKind.Array) return null;
            var selected = components.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!).Where(value => value is "cpu" or "webgpu" or "directml")
                .Prepend("cpu").Distinct(StringComparer.Ordinal).ToArray();
            return string.Join(",", selected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
