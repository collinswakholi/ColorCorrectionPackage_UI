using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using ColorCorrect.Engine;

namespace ColorCorrect.App;

/// <summary>
/// Records that the window opened and the engine became ready, in
/// diagnostics/startup.json. Clean-install checks and support use it to tell
/// "started and ready" apart from "still running but stuck".
/// </summary>
public static class StartupRecord
{
    public static string PathFor(string dataDir) => Path.Combine(dataDir, "diagnostics", "startup.json");

    public static void WriteReady(string device)
    {
        try
        {
            var path = PathFor(SessionStore.AppDataDir());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                engine = "ready",
                device,
                version,
                os = Environment.OSVersion.VersionString,
                time = DateTimeOffset.Now.ToString("O"),
            }));
            // A successful start supersedes an earlier failure report.
            File.Delete(Path.Combine(SessionStore.AppDataDir(), "startup-error.txt"));
        }
        catch (Exception)
        {
            // Diagnostics must never get in the way of the app.
        }
    }
}
