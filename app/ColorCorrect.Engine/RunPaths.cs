namespace ColorCorrect.Engine;

public static class RunPaths
{
    public static string NewRunDir(string repoRoot, string? prefix = null)
    {
        var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            id = $"{prefix}-{id}";
        }

        var dir = Path.Combine(RunsRoot(repoRoot), id);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string RunsRoot(string repoRoot)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COLORCORRECT_DATA_DIR")))
        {
            return Path.Combine(SessionStore.AppDataDir(), "runs");
        }
        if (EnginePaths.LooksLikeRepo(repoRoot))
        {
            return Path.Combine(repoRoot, "assets", "runs");
        }

        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ColorCorrect", "runs");
        Directory.CreateDirectory(local);
        return local;
    }

    public static bool ContainsParentSegment(string path)
    {
        return path.Replace('/', Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part == "..");
    }

    public static void CopyIfNeeded(string src, string destDir, string destName)
    {
        if (!File.Exists(src) || string.IsNullOrWhiteSpace(destDir))
        {
            return;
        }

        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, destName);
        try
        {
            if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        catch
        {
            // Fall through to copy if path normalization fails.
        }

        File.Copy(src, dest, overwrite: true);
    }

    public static bool IsUnderRepo(string path, string repoRoot)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
