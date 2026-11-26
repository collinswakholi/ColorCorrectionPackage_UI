namespace ColorCorrect.Engine;

/// <summary>Per-user base folders. The result is always an absolute path.</summary>
public static class UserFolders
{
    /// <summary>The standard per-user data folder (%LOCALAPPDATA%, ~/.local/share, ~/Library/Application Support).</summary>
    /// <remarks>
    /// On Linux and macOS, GetFolderPath returns "" while that folder does not exist yet, as on a fresh
    /// account. Combined with a name, "" gave a relative path, so 0.5.0 and earlier wrote their data to
    /// the working directory and aborted when it was not writable (for example "/" from a menu).
    /// </remarks>
    public static string LocalData() => LocalData(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
        Environment.GetEnvironmentVariable("HOME"));

    public static string LocalData(string? standard, string? xdgDataHome, string? home)
    {
        if (!string.IsNullOrWhiteSpace(standard) && Path.IsPathFullyQualified(standard))
        {
            return standard;
        }

        if (!string.IsNullOrWhiteSpace(xdgDataHome) && Path.IsPathFullyQualified(xdgDataHome))
        {
            return xdgDataHome;
        }

        if (!string.IsNullOrWhiteSpace(home) && Path.IsPathFullyQualified(home))
        {
            return Path.Combine(home, ".local", "share");
        }

        throw new InvalidOperationException(
            "No per-user data folder is available. Set HOME, or set COLORCORRECT_DATA_DIR to a writable folder.");
    }
}
