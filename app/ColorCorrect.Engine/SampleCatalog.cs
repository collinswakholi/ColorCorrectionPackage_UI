namespace ColorCorrect.Engine;

public sealed record SampleEntry(string Name, string Path, string Group, bool IsWhite);

public static class SampleCatalog
{
    private static readonly string[] Roots =
    [
        Path.Combine("assets", "test_images", "github"),
        Path.Combine("assets", "test_images", "internet"),
        Path.Combine("assets", "test_images"),
        Path.Combine("testdata", "samples", "github"),
        Path.Combine("testdata", "samples"),
    ];

    private static readonly string[] Exts = [".jpg", ".jpeg", ".png"];

    public static bool LooksLikeWhite(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        name = name.Replace('_', '-').Replace(' ', '-');
        return name.Equals("blank", StringComparison.OrdinalIgnoreCase)
               || name.Equals("white", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("blank-", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("white-", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("white-ref", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("-blank", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("-white", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("ffc-", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<SampleEntry> Load(
        string repoRoot,
        IEnumerable<string>? extraFolders = null,
        int max = 160)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<SampleEntry>();
        foreach (var relative in Roots)
        {
            AddFolder(Path.Combine(repoRoot, relative), Path.GetFileName(relative), seen, list, max);
        }

        if (extraFolders is not null)
        {
            foreach (var folder in extraFolders)
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    continue;
                }

                AddFolder(folder, Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), seen, list, max);
            }
        }

        return list;
    }

    private static void AddFolder(
        string dir,
        string group,
        HashSet<string> seen,
        List<SampleEntry> list,
        int max)
    {
        if (!Directory.Exists(dir) || list.Count >= max)
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(file);
            if (!Exts.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Path.GetFileName(file);
            if (name.Contains("_corrected", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("synth_", StringComparison.OrdinalIgnoreCase) ||
                !seen.Add(CanonicalPath(file)))
            {
                continue;
            }

            var white = LooksLikeWhite(name);
            list.Add(new SampleEntry(name, CanonicalPath(file), white ? "Whites" : group, white));
            if (list.Count >= max)
            {
                return;
            }
        }
    }

    private static string CanonicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            var file = new FileInfo(full);
            var dir = ResolveDirectory(file.Directory);
            return string.IsNullOrWhiteSpace(dir) ? full : Path.GetFullPath(Path.Combine(dir, file.Name));
        }
        catch
        {
            return full;
        }
    }

    private static string? ResolveDirectory(DirectoryInfo? dir)
    {
        if (dir is null)
        {
            return null;
        }

        var suffix = new Stack<string>();
        for (var current = dir; current is not null; current = current.Parent)
        {
            FileSystemInfo? target = null;
            try
            {
                target = current.ResolveLinkTarget(returnFinalTarget: true);
            }
            catch
            {
                // Keep walking toward the root.
            }

            if (target is not null)
            {
                var resolved = target.FullName;
                while (suffix.Count > 0)
                {
                    resolved = Path.Combine(resolved, suffix.Pop());
                }

                return resolved;
            }

            suffix.Push(current.Name);
        }

        return dir.FullName;
    }
}
