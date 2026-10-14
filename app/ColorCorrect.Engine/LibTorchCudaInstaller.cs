using System.Diagnostics;
using System.Text;

namespace ColorCorrect.Engine;

/// <summary>
/// Downloads official LibTorch 2.9 CUDA into the single per-user libtorch folder.
/// </summary>
public static class LibTorchCudaInstaller
{
    public static string DestRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CCP-New", "libtorch-cuda");

    public static bool HasCudaTree(string? root = null)
    {
        var dir = string.IsNullOrWhiteSpace(root) ? DestRoot() : root;
        var dll = Path.Combine(dir, "lib", "torch_cuda.dll");
        if (!File.Exists(dll))
        {
            return false;
        }

        var json = Path.Combine(dir, "libtorch.json");
        if (!File.Exists(json))
        {
            return true;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
            var kind = doc.RootElement.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
            var version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            if (!string.IsNullOrWhiteSpace(kind) &&
                !kind.Equals("cuda", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(version) || version.StartsWith("2.9", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static string? FindScript(string? repoRoot, string? engineExe)
    {
        var names = new List<string>();
        void Add(string? dir)
        {
            if (!string.IsNullOrWhiteSpace(dir))
            {
                names.Add(Path.Combine(dir, "scripts", "install_libtorch_cuda.ps1"));
                names.Add(Path.Combine(dir, "install_libtorch_cuda.ps1"));
            }
        }

        Add(repoRoot);
        if (!string.IsNullOrWhiteSpace(engineExe))
        {
            var exeDir = Path.GetDirectoryName(engineExe);
            Add(exeDir);
            if (!string.IsNullOrWhiteSpace(exeDir))
            {
                Add(Path.GetDirectoryName(exeDir));
            }
        }

        Add(AppContext.BaseDirectory);
        try
        {
            Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")));
        }
        catch
        {
        }

        return names.FirstOrDefault(File.Exists);
    }

    public static async Task<(bool Ok, string Log)> InstallAsync(
        string scriptPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(powershell))
        {
            powershell = "powershell.exe";
        }

        var dest = DestRoot();
        var psi = new ProcessStartInfo(powershell)
        {
            Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\" -Dest \"" + dest + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        var log = new StringBuilder();
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start PowerShell");
        proc.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            log.AppendLine(e.Data);
            progress?.Report(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            log.AppendLine(e.Data);
            progress?.Report(e.Data);
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var ok = proc.ExitCode == 0 && HasCudaTree(dest);
        if (!ok && log.Length == 0)
        {
            log.Append("CUDA LibTorch install failed (exit ").Append(proc.ExitCode).Append(')');
        }

        return (ok, log.ToString());
    }
}
