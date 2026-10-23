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
        if (File.Exists(Path.Combine(repoRoot, "runtime-support.json")))
        {
            var packagedRuns = Path.Combine(SessionStore.AppDataDir(), "runs");
            Directory.CreateDirectory(packagedRuns);
            return packagedRuns;
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
            if (PathIdentity.Equal(Path.GetFullPath(src), Path.GetFullPath(dest)))
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
            return full.StartsWith(root + Path.DirectorySeparatorChar, PathIdentity.Comparison)
                   || PathIdentity.Equal(full, root);
        }
        catch
        {
            return false;
        }
    }
}
