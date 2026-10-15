using System.Text.Json;
using System.Text.Json.Serialization;

namespace ColorCorrect.Engine;

public sealed class SessionState
{
    public List<SessionItemDto> Items { get; set; } = [];
    public string ActivePath { get; set; } = "";
    private CorrectionOptions _options = new();
    // Older image-only session files omit Options. Do not replace a user's
    // current workflow with defaults when loading one of those files.
    [JsonIgnore]
    public bool HasOptions { get; private set; }
    public CorrectionOptions Options
    {
        get => _options;
        set
        {
            _options = value ?? new();
            HasOptions = value is not null;
        }
    }
    public double Crop { get; set; } = 0.75;
    public string Theme { get; set; } = "Dark";
    public List<string> TrustedOutputDirs { get; set; } = [];
    public List<string> CatalogFolders { get; set; } = [];
    public bool ForceCpu { get; set; }
    public string AccelPolicy { get; set; } = "";
    public bool CudaOfferDismissed { get; set; }
    public bool? ShowLattice { get; set; }
    public bool? ShowSample { get; set; }
    public bool? ShowDim { get; set; }
    public bool? ShowPlane { get; set; }
}

public sealed class SessionItemDto
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string HandshakeJson { get; set; } = "";
    public string CorrectedPath { get; set; } = "";
    public string ReportPath { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public int FaceChoice { get; set; }
    public bool UseAllFaces { get; set; }
    public bool IncludeInBatch { get; set; } = true;
    public string PreviewDir { get; set; } = "";
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

            var state = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(path)) ?? new SessionState();
            state.Items = state.Items.Where(item => File.Exists(item.Path)).ToList();
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
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
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
}
