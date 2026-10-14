namespace ColorCorrect.Engine;

public static class EnginePaths
{
    public static bool TryFindRepoRoot(out string root)
    {
        var starts = CandidateStarts().ToList();
        foreach (var start in starts.Distinct(OperatingSystem.IsWindows()
                     ? StringComparer.OrdinalIgnoreCase
                     : StringComparer.Ordinal))
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (LooksLikeRepo(dir.FullName))
                {
                    root = dir.FullName;
                    return true;
                }
                dir = dir.Parent;
            }
        }

        root = "";
        return false;
    }

    public static bool LooksLikeRepo(string path) =>
        Directory.Exists(Path.Combine(path, "models", "opt2")) &&
        File.Exists(Path.Combine(path, "refs", "neutrals.json"));

    public static string FindRepoRoot()
    {
        if (TryFindRepoRoot(out var root))
        {
            return root;
        }

        throw new DirectoryNotFoundException("Could not find the ColorCorrect Studio folder (models/opt2 + refs/neutrals.json).");
    }

    public static bool TryFindExecutable(out string exePath)
    {
        var env = Environment.GetEnvironmentVariable("CCP_ENGINE_EXE")
                  ?? Environment.GetEnvironmentVariable("COLORCORRECT_EXE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            exePath = Path.GetFullPath(env);
            return true;
        }

        var names = OperatingSystem.IsWindows()
            ? new[] { "ccp-engine.exe" }
            : new[] { "ccp-engine", "ccp-engine.exe" };

        var starts = CandidateStarts().ToList();
        if (TryFindRepoRoot(out var repoRoot))
        {
            starts.Insert(0, repoRoot);
        }

        foreach (var start in starts.Distinct(OperatingSystem.IsWindows()
                     ? StringComparer.OrdinalIgnoreCase
                     : StringComparer.Ordinal))
        {
            foreach (var name in names)
            {
                foreach (var relative in new[]
                {
                    name,
                    Path.Combine("engine", "target", "release", name),
                    Path.Combine("engine", "target", "debug", name),
                    Path.Combine("dist", name),
                    Path.Combine("dist", "win-cpu", name),
                    Path.Combine("dist", "macos-arm64", "payload", name),
                    Path.Combine("dist", "linux-x64", "payload", name),
                })
                {
                    var candidate = Path.GetFullPath(Path.Combine(start, relative));
                    if (File.Exists(candidate))
                    {
                        exePath = candidate;
                        return true;
                    }
                }
            }
        }

        exePath = "";
        return false;
    }

    public static string FindExecutable()
    {
        if (TryFindExecutable(out var exePath))
        {
            return exePath;
        }

        throw new FileNotFoundException(
            "The ColorCorrect processing engine was not found. Build it with scripts/start_desktop.ps1 or set CCP_ENGINE_EXE.");
    }

    public static string LibTorchRoot(string? baseDirectory = null, string? cudaRoot = null)
    {
        var env = Environment.GetEnvironmentVariable("CCP_LIBTORCH_ROOT")
                  ?? Environment.GetEnvironmentVariable("LIBTORCH_PATH");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        var addon = cudaRoot ?? LibTorchCudaInstaller.DestRoot();
        if (LibTorchCudaInstaller.HasCudaTree(addon)) return addon;

        // Portable packages keep their CPU runtime beside the published app.
        // Explicit environment configuration still wins for CUDA/custom trees.
        var appBase = baseDirectory ?? AppContext.BaseDirectory;
        foreach (var bundled in new[]
                 {
                     Path.Combine(appBase, "libtorch"),
                     Path.GetFullPath(Path.Combine(appBase, "..", "libtorch")),
                 })
        {
            if (File.Exists(Path.Combine(bundled, "libtorch.json")))
            {
                return bundled;
            }
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "CCP-New", "libtorch");
    }

    private static IEnumerable<string> CandidateStarts()
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                seen.Add(Path.GetFullPath(path));
            }
            catch (Exception)
            {
                // Ignore malformed BaseDirectory / cwd values.
            }
        }

        Add(Directory.GetCurrentDirectory());
        Add(AppContext.BaseDirectory);
        try
        {
            Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")));
        }
        catch (Exception)
        {
        }

        return seen;
    }
}
