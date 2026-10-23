using System.Text.Json;
using System.Text.Json.Serialization;

namespace ColorCorrect.Engine;

public sealed class SessionState
{
    public List<SessionItemDto> Items { get; set; } = [];
    public string ActivePath { get; set; } = "";
    private CorrectionOptions _options = new();
    [JsonIgnore] public bool HasOptions { get; private set; }
    public CorrectionOptions Options
    {
        get => _options;
        set { _options = value ?? new(); HasOptions = value is not null; }
    }
    public double Crop { get; set; } = 0.75;
    public string Theme { get; set; } = "Dark";
    public List<string> TrustedOutputDirs { get; set; } = [];
    public List<string> CatalogFolders { get; set; } = [];
    public bool ForceCpu { get; set; }
    public string AccelPolicy { get; set; } = "";
    public bool? ShowLattice { get; set; }
    public bool? ShowSample { get; set; }
    public bool? ShowDim { get; set; }
    public bool? ShowPlane { get; set; }
}

public sealed class SessionItemDto
{
    public CompletedResultContext? CompletedContext { get; set; }
    public AssetIdentity? SourceIdentity { get; set; }
    public List<ReferenceProfile> ReferenceProfiles { get; set; } = [];
    public List<ChartReferenceAssignment> ReferenceAssignments { get; set; } = [];
    public bool ReferencesNeedAssignment { get; set; }
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string HandshakeJson { get; set; } = "";
    public string CorrectedPath { get; set; } = "";
    public string ReportPath { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public int FaceChoice { get; set; }
    public bool UseAllFaces { get; set; }
    public bool IncludeInBatch { get; set; } = true;
    public bool PreviewSuppressed { get; set; }
    public string PreviewDir { get; set; } = "";
    // Read legacy sessions so retired video entries can be skipped safely.
    public bool IsVideo { get; set; }
    public int[]? FfcPlane { get; set; }
    public int[]? FfcWhiteSize { get; set; }
    public string FfcPlaneSource { get; set; } = "";
}

public static class SessionStore
{
    public static string AppDataDir()
    {
        // Additive QA/portable profile override; the existing default is unchanged.
        var configured = Environment.GetEnvironmentVariable("COLORCORRECT_DATA_DIR");
        var dir = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CCP-New")
            : Path.GetFullPath(configured);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string FilePath() => Path.Combine(AppDataDir(), "session.json");

    public static string SettingsPath() => Path.Combine(AppDataDir(), "settings.json");

    public static SessionState Load() => TryLoad().State;

    public static (SessionState State, string? Error) TryLoad() => TryLoadSettings();

    public static (SessionState State, string? Error) TryLoadSettings()
    {
        if (File.Exists(SettingsPath()))
        {
            return TryLoadFrom(SettingsPath());
        }

        return TryLoadFrom(FilePath());
    }

    public static bool SaveSettings(SessionState state) => Write(SettingsPath(), state);

    public static (SessionState State, string? Error) TryLoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return (new SessionState(), null);
            }

            var state = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(path), new JsonSerializerOptions { AllowDuplicateProperties = false }) ?? new SessionState();
            if (state.Items is null || state.Items.Any(item => item is null))
                throw new JsonException("Session items must be an array of objects.");
            foreach (var item in state.Items)
            {
                if (item.CompletedContext is { } context)
                {
                    if (context.Version != 1) throw new JsonException("Unsupported completed-result version in session.");
                    static void Identity(AssetIdentity? asset)
                    {
                        if (asset is null || string.IsNullOrWhiteSpace(asset.Path) || asset.Sha256 is not { Length: 64 }
                            || asset.Sha256.Any(c => !Uri.IsHexDigit(c)))
                            throw new JsonException("Completed results require a file path and a valid SHA-256 identity for every saved asset.");
                    }
                    Identity(context.Source); Identity(context.Model);
                    if (context.White is not null) Identity(context.White);
                    var options = context.Options();
                    if (options.DoFfc && context.White is null) throw new JsonException("An FFC result must retain its white-reference identity.");
                    if (options.DoFfc && !PathIdentity.Equal(Path.GetFullPath(options.WhiteImagePath), Path.GetFullPath(context.White!.Path)))
                        throw new JsonException("Saved FFC settings must use the result's captured white reference.");
                }
                if (item.ReferenceProfiles is null || item.ReferenceAssignments is null ||
                    item.ReferenceProfiles.Any(profile => profile is null) || item.ReferenceAssignments.Any(assignment => assignment is null))
                    throw new JsonException("Session reference profiles and assignments must be arrays of objects.");
                foreach (var profile in item.ReferenceProfiles) profile.Validate();
                if (item.ReferenceAssignments.Any(assignment => string.IsNullOrWhiteSpace(assignment.ChartId) || string.IsNullOrWhiteSpace(assignment.ProfileId) || assignment.ProfileRevision < 1))
                    throw new JsonException("Session reference assignments require chart/profile IDs and a positive revision.");
            }
            // Keep unavailable items and their reference/model identities for explicit relinking.
            RelocateResultAssets(path, state);
            return (state, null);
        }
        catch (Exception ex)
        {
            return (new SessionState(), ex.Message);
        }
    }

    public static bool Save(SessionState state) => Write(FilePath(), state);

    public static bool Write(string path, SessionState state)
    {
        string? tempPath = null;
        using var assets = new AssetWrites();
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Explicit session saves own their result assets; clearing transient runs must
            // not break a session saved yesterday. Settings-only saves contain no items.
            var durable = JsonSerializer.Deserialize<SessionState>(JsonSerializer.Serialize(state))!;
            PersistResultAssets(path, durable, assets);
            var json = JsonSerializer.Serialize(durable, new JsonSerializerOptions { WriteIndented = true });
            tempPath = path + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tempPath, path, null);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(tempPath, path, overwrite: true);
                }
                catch (IOException)
                {
                    File.Move(tempPath, path, overwrite: true);
                }
            }
            else
            {
                File.Move(tempPath, path);
            }

            assets.Commit();
            return true;
        }
        catch
        {
            // Quota / locked file — keep the in-memory session.
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best-effort cleanup after a failed atomic write.
                }
            }
        }
    }

    // Asset replacements must roll back too if the final JSON cannot commit.
    // Keep rollback copies if a restoration itself fails, for manual recovery.
    private sealed class AssetWrites : IDisposable
    {
        private readonly List<(string Path, string? Backup)> _changes = [];
        private bool _committed;

        public void Install(string temporary, string destination)
        {
            string? backup = null;
            if (File.Exists(destination))
            {
                backup = destination + $".{Guid.NewGuid():N}.rollback";
                try { File.Copy(destination, backup); }
                catch { if (File.Exists(backup)) File.Delete(backup); throw; }
            }
            _changes.Add((destination, backup));
            File.Move(temporary, destination, overwrite: true);
        }

        public void Commit() => _committed = true;

        public void Dispose()
        {
            foreach (var (path, backup) in _changes.AsEnumerable().Reverse())
            {
                try
                {
                    if (_committed) { if (backup is not null) File.Delete(backup); }
                    else if (backup is not null) File.Move(backup, path, overwrite: true);
                    else File.Delete(path);
                }
                catch { /* Preserve a failed rollback's backup rather than discard it. */ }
            }
        }
    }

    private static void PersistResultAssets(string sessionPath, SessionState state, AssetWrites writes)
    {
        if (state.Items.Count == 0) return;
        var assetsRoot = Path.GetFullPath(sessionPath) + ".assets";
        foreach (var item in state.Items)
        {
            if (item.CompletedContext is not { } context) continue;
            var keyOptions = context.Options();
            keyOptions.WhiteImagePath = keyOptions.OutputDir = keyOptions.LoadedModelPath = keyOptions.ModelInPath = "";
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new { Source = context.Source.Sha256, Model = context.Model.Sha256, White = context.White?.Sha256, Options = keyOptions }))));
            var dir = Path.Combine(assetsRoot, key);
            var identitiesPath = Path.Combine(dir, "asset-identities.v1.json");
            var identities = File.Exists(identitiesPath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(identitiesPath),
                    new JsonSerializerOptions { AllowDuplicateProperties = false }) ?? throw new InvalidDataException("Invalid saved asset identities.")
                : new Dictionary<string, string>();
            if (identities.Values.Any(hash => hash is not { Length: 64 } || hash.Any(c => !Uri.IsHexDigit(c))))
                throw new InvalidDataException("Invalid saved asset identity.");
            static bool Matches(string path, string hash) => File.Exists(path)
                && string.Equals(AssetIdentity.Capture(path).Sha256, hash, StringComparison.OrdinalIgnoreCase);

            string Keep(string source, string name, string? capturedHash = null)
            {
                if (string.IsNullOrWhiteSpace(source)) return source;
                var destination = Path.Combine(dir, name);
                var identityName = name.Replace('\\', '/');
                identities.TryGetValue(identityName, out var knownHash);
                var expected = capturedHash ?? knownHash;
                if (expected is not null && Matches(destination, expected))
                {
                    identities[identityName] = expected;
                    return destination;
                }
                if (!File.Exists(source))
                {
                    if (expected is not null && File.Exists(destination))
                        throw new InvalidDataException("A saved result asset is corrupt and its original is unavailable.");
                    return source; // Retain unresolved original paths, never bless an unverified copy.
                }
                var sourceHash = AssetIdentity.Capture(source).Sha256;
                expected ??= sourceHash;
                if (!string.Equals(sourceHash, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A completed result asset changed before it could be saved.");
                if (PathIdentity.Equal(Path.GetFullPath(source), Path.GetFullPath(destination)) || Matches(destination, expected))
                {
                    identities[identityName] = expected;
                    return destination;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temporary = destination + $".{Guid.NewGuid():N}.tmp";
                try
                {
                    File.Copy(source, temporary);
                    if (!Matches(temporary, expected)) throw new InvalidDataException("A result asset changed while being copied.");
                    writes.Install(temporary, destination);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                identities[identityName] = expected;
                return destination;
            }
            var model = Keep(context.Model.Path, "model.json", context.Model.Sha256);
            var white = context.White is { } w ? Keep(w.Path, "white" + Path.GetExtension(w.Path), w.Sha256) : null;
            var options = context.Options();
            if (white is not null) options.WhiteImagePath = white;
            if (PathIdentity.Equal(state.Options.LoadedModelPath, context.Model.Path)) state.Options.LoadedModelPath = model;
            if (PathIdentity.Equal(state.Options.ModelInPath, context.Model.Path)) state.Options.ModelInPath = model;
            item.CompletedContext = context with { Model = context.Model with { Path = model },
                White = context.White is null ? null : context.White with { Path = white! }, OptionsJson = JsonSerializer.Serialize(options) };
            item.ModelPath = model;
            item.ReportPath = Keep(item.ReportPath, "report.json");
            item.CorrectedPath = Keep(item.CorrectedPath, "corrected" + Path.GetExtension(item.CorrectedPath));
            if (Directory.Exists(item.PreviewDir) || identities.Keys.Any(name => name.StartsWith("previews/", StringComparison.Ordinal)))
            {
                var preview = Path.Combine(dir, "previews");
                var hasOwnedPreview = false;
                foreach (var stem in new[] { "original", "ffc", "gc", "wb", "cc", "corrected" })
                    foreach (var extension in new[] { ".rgb", ".png", ".jpg" })
                    {
                        // RGB is complete before the worker replies; its optional
                        // PNG encoder may still be running. Do not freeze a partial PNG.
                        if (extension == ".png" && (File.Exists(Path.Combine(item.PreviewDir, stem + ".rgb"))
                            || identities.ContainsKey("previews/" + stem + ".rgb"))) continue;
                        var source = Path.Combine(item.PreviewDir, stem + extension);
                        if (File.Exists(source) || identities.ContainsKey("previews/" + stem + extension))
                        {
                            var kept = Keep(source, Path.Combine("previews", stem + extension));
                            hasOwnedPreview |= File.Exists(kept) && PathIdentity.Equal(Path.GetDirectoryName(Path.GetFullPath(kept)), Path.GetFullPath(preview));
                        }
                    }
                if (hasOwnedPreview) item.PreviewDir = preview;
            }
            if (identities.Count > 0)
            {
                var json = JsonSerializer.Serialize(identities.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value));
                if (!File.Exists(identitiesPath) || File.ReadAllText(identitiesPath) != json)
                {
                    var temporary = identitiesPath + $".{Guid.NewGuid():N}.tmp";
                    try { File.WriteAllText(temporary, json); writes.Install(temporary, identitiesPath); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
            }
        }
    }

    private static void RelocateResultAssets(string sessionPath, SessionState state)
    {
        foreach (var item in state.Items)
        {
            if (item.CompletedContext is not { } context || File.Exists(context.Model.Path)) continue;
            var key = Path.GetFileName(Path.GetDirectoryName(context.Model.Path));
            if (key is null || key.Length != 64 || key.Any(c => !Uri.IsHexDigit(c))) continue;
            var dir = Path.Combine(Path.GetFullPath(sessionPath) + ".assets", key);
            var model = Path.Combine(dir, "model.json");
            if (!context.Model.Matches(model)) continue;
            var options = context.Options();
            var white = context.White;
            if (white is not null)
            {
                var replacement = Path.Combine(dir, "white" + Path.GetExtension(white.Path));
                if (white.Matches(replacement)) { white = white with { Path = replacement }; options.WhiteImagePath = replacement; }
            }
            item.CompletedContext = context with { Model = context.Model with { Path = model }, White = white, OptionsJson = JsonSerializer.Serialize(options) };
            if (PathIdentity.Equal(state.Options.LoadedModelPath, context.Model.Path)) state.Options.LoadedModelPath = model;
            if (PathIdentity.Equal(state.Options.ModelInPath, context.Model.Path)) state.Options.ModelInPath = model;
            item.ModelPath = model;
            if (!File.Exists(item.ReportPath) && File.Exists(Path.Combine(dir, "report.json"))) item.ReportPath = Path.Combine(dir, "report.json");
            if (!File.Exists(item.CorrectedPath) && File.Exists(Path.Combine(dir, "corrected" + Path.GetExtension(item.CorrectedPath)))) item.CorrectedPath = Path.Combine(dir, "corrected" + Path.GetExtension(item.CorrectedPath));
            if (!Directory.Exists(item.PreviewDir) && Directory.Exists(Path.Combine(dir, "previews"))) item.PreviewDir = Path.Combine(dir, "previews");
        }
    }
}
