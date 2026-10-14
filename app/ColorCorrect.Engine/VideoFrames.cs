using System.Diagnostics;

namespace ColorCorrect.Engine;

public static class VideoFrames
{
    public static string? FindFfmpeg(string repoRoot)
    {
        var env = Environment.GetEnvironmentVariable("CCP_FFMPEG");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        var names = OperatingSystem.IsWindows()
            ? new[] { "ffmpeg.exe" }
            : new[] { "ffmpeg", "ffmpeg.exe" };

        var dirs = new List<string>();
        void AddDir(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                dirs.Add(Path.GetFullPath(path));
            }
            catch (Exception)
            {
            }
        }

        AddDir(repoRoot);
        AddDir(Path.Combine(repoRoot, "installer"));
        AddDir(AppContext.BaseDirectory);
        AddDir(Path.Combine(AppContext.BaseDirectory, ".."));

        foreach (var name in names)
        {
            foreach (var dir in dirs.Distinct(OperatingSystem.IsWindows()
                         ? StringComparer.OrdinalIgnoreCase
                         : StringComparer.Ordinal))
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        foreach (var name in names)
        {
            var onPath = FindOnPath(name);
            if (onPath is not null)
            {
                return onPath;
            }
        }

        return null;
    }

    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var dirs = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (OperatingSystem.IsMacOS())
        {
            // Finder-launched apps commonly receive a minimal PATH that omits
            // both Apple Silicon and Intel Homebrew locations.
            dirs.Add("/opt/homebrew/bin");
            dirs.Add("/usr/local/bin");
        }
        else if (OperatingSystem.IsLinux())
        {
            dirs.Add("/usr/local/bin");
            dirs.Add("/usr/bin");
        }

        foreach (var dir in dirs.Distinct(OperatingSystem.IsWindows()
                     ? StringComparer.OrdinalIgnoreCase
                     : StringComparer.Ordinal))
        {
            var candidate = Path.Combine(dir.Trim('"'), name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static async Task<string[]> ExtractAsync(
        string ffmpeg,
        string videoPath,
        string destDir,
        int fps = 8,
        int width = 640,
        int maxSeconds = 90,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destDir);
        var capMark = Path.Combine(destDir, ".preview-cap");
        var cacheSignature = $"fps={fps};width={width};seconds={maxSeconds}";
        var existing = Directory.GetFiles(destDir, "frame_*.jpg");
        if (existing.Length > 0
            && FramesFresh(existing, videoPath)
            && File.Exists(capMark)
            && File.ReadAllText(capMark).Trim() == cacheSignature)
        {
            return existing.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        foreach (var old in existing)
        {
            File.Delete(old);
        }

        if (File.Exists(capMark))
        {
            File.Delete(capMark);
        }

        var pattern = Path.Combine(destDir, "frame_%04d.jpg");
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            ArgumentList =
            {
                "-hide_banner",
                "-loglevel",
                "error",
                "-y",
                "-i",
                videoPath,
                "-t",
                maxSeconds.ToString(),
                "-vf",
                $"fps={fps},scale={width}:-2",
                pattern,
            },
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg failed to start");
        var stderr = await WaitForExitAndReadErrorAsync(proc, cancellationToken).ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? "ffmpeg could not extract video frames."
                : $"ffmpeg could not extract video frames: {stderr.Trim()}");
        }

        File.WriteAllText(capMark, cacheSignature);
        return Directory.GetFiles(destDir, "frame_*.jpg").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static async Task<string?> ExtractPosterAsync(
        string ffmpeg,
        string videoPath,
        string destPath,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(destPath) && File.GetLastWriteTimeUtc(destPath) >= File.GetLastWriteTimeUtc(videoPath))
        {
            return destPath;
        }

        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            ArgumentList =
            {
                "-hide_banner",
                "-loglevel",
                "error",
                "-y",
                "-ss",
                "0.15",
                "-i",
                videoPath,
                "-frames:v",
                "1",
                "-vf",
                "scale=192:-2",
                destPath,
            },
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg failed to start");
        await WaitForExitAndReadErrorAsync(proc, cancellationToken).ConfigureAwait(false);
        return proc.ExitCode == 0 && File.Exists(destPath) ? destPath : null;
    }

    public static string WriteCompareHtml(string original, string? corrected, string destHtml)
    {
        var dir = Path.GetDirectoryName(destHtml) ?? ".";
        Directory.CreateDirectory(dir);
        var origRel = CopyBeside(original, dir);
        var corrRel = string.IsNullOrWhiteSpace(corrected) || !File.Exists(corrected)
            ? ""
            : CopyBeside(corrected, dir);
        var corrBlock = string.IsNullOrWhiteSpace(corrRel)
            ? "<p>Grade the clip to see the corrected playback.</p>"
            : $"""<video id="b" src="{corrRel}" controls muted playsinline></video>""";
        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8"/>
              <title>ColorCorrect Studio — original vs corrected</title>
              <style>
                body { margin: 24px; font-family: Segoe UI, sans-serif; background: #141414; color: #eee; }
                h1 { font-size: 18px; font-weight: 600; margin: 0 0 16px; }
                .row { display: flex; gap: 16px; flex-wrap: wrap; }
                .col { flex: 1 1 320px; min-width: 280px; }
                .lab { font-size: 12px; letter-spacing: 0.08em; text-transform: uppercase; color: #9a9a9a; margin: 0 0 8px; }
                video { width: 100%; background: #000; }
                button { margin-top: 16px; padding: 8px 14px; }
              </style>
            </head>
            <body>
              <h1>Original vs corrected clip</h1>
              <div class="row">
                <div class="col">
                  <p class="lab">Original</p>
                  <video id="a" src="{{origRel}}" controls muted playsinline></video>
                </div>
                <div class="col">
                  <p class="lab">Corrected</p>
                  {{corrBlock}}
                </div>
              </div>
              <button type="button" id="sync">Play both</button>
              <script>
                const a = document.getElementById('a');
                const b = document.getElementById('b');
                document.getElementById('sync').onclick = () => {
                  if (b) { b.currentTime = a.currentTime; b.play(); }
                  a.play();
                };
                if (a && b) {
                  a.addEventListener('play', () => b.play());
                  a.addEventListener('pause', () => b.pause());
                  a.addEventListener('seeked', () => { b.currentTime = a.currentTime; });
                }
              </script>
            </body>
            </html>
            """;
        File.WriteAllText(destHtml, html);
        return destHtml;
    }

    private static bool FramesFresh(string[] files, string source)
    {
        var src = File.GetLastWriteTimeUtc(source);
        return files.All(f => File.GetLastWriteTimeUtc(f) >= src);
    }

    private static async Task<string> WaitForExitAndReadErrorAsync(Process process, CancellationToken cancellationToken)
    {
        // Drain stderr concurrently. Waiting first can deadlock when FFmpeg
        // fills its redirected pipe (Microsoft's Process guidance explicitly
        // requires asynchronous reading of at least one redirected stream).
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // The process may have exited between the checks.
            }

            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch
            {
                // Preserve the caller's cancellation.
            }
            await stderrTask.ConfigureAwait(false);
            throw;
        }

        return await stderrTask.ConfigureAwait(false);
    }

    private static string CopyBeside(string source, string dir)
    {
        var name = Path.GetFileName(source);
        var dest = Path.Combine(dir, name);
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(source, dest, overwrite: true);
        }

        return Uri.EscapeDataString(name);
    }
}
