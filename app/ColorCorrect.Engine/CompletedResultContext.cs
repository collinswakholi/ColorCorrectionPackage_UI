using System.Security.Cryptography;
using System.Text.Json;

namespace ColorCorrect.Engine;

/// <summary>File identity is content based: changing a file at the same path is not a relink.</summary>
public sealed record AssetIdentity(string Path, string Sha256)
{
    public static AssetIdentity Capture(string path)
    {
        using var stream = File.OpenRead(path);
        return new(System.IO.Path.GetFullPath(path), Convert.ToHexString(SHA256.HashData(stream)));
    }

    public bool Matches(string? path = null)
    {
        try { return string.Equals(Capture(path ?? Path).Sha256, Sha256, StringComparison.OrdinalIgnoreCase); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

public static class PathIdentity
{
    public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static bool Equal(string? a, string? b) => Comparer.Equals(a, b);
}

/// <summary>A completed operation owns its model and white reference. UI edits configure only the next operation.</summary>
public sealed record CompletedResultContext
{
    public int Version { get; init; } = 1;
    public required AssetIdentity Source { get; init; }
    public required AssetIdentity Model { get; init; }
    public AssetIdentity? White { get; init; }
    public required string OptionsJson { get; init; }

    public CorrectionOptions Options() => JsonSerializer.Deserialize<CorrectionOptions>(OptionsJson, new JsonSerializerOptions { AllowDuplicateProperties = false })
        ?? throw new InvalidDataException("The completed result has no processing settings.");

    public static CompletedResultContext Capture(AssetIdentity source, string model, CorrectionOptions options)
    {
        if (!source.Matches()) throw new InvalidDataException("The source image changed during processing. Run it again before saving a result.");
        return new() { Source = source, Model = AssetIdentity.Capture(model),
            White = options.DoFfc ? AssetIdentity.Capture(options.WhiteImagePath) : null,
            OptionsJson = JsonSerializer.Serialize(options) };
    }

    public string? ReplayError(string inputPath)
    {
        if (Version != 1) return "This result uses an unsupported saved processing version.";
        if (!Source.Matches(inputPath)) return "The original image is missing or changed. Relink the matching image or process it again.";
        if (!Model.Matches()) return "This result's saved model is missing or changed. Relink its original model or process the image again.";
        if (White is not null && !White.Matches()) return "This result's white reference is missing or changed. Restore the matching reference or process the image again.";
        return null;
    }

    public CompletedResultContext RelinkSource(string path)
    {
        if (!Source.Matches(path)) throw new InvalidDataException("The selected image does not match this result's original image.");
        return this with { Source = Source with { Path = System.IO.Path.GetFullPath(path) } };
    }
}
