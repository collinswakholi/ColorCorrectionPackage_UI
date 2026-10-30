using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ColorCorrect.Engine;

/// <summary>
/// Long-lived <c>ccp-engine</c> NDJSON client. Drains stderr so the pipe cannot fill.
/// Cancellation recycles the worker so a stale reply cannot affect the next request.
/// Disposal cancels all owned work and waits for bounded process cleanup.
/// </summary>
public sealed class SpeedyWorker : IAsyncDisposable
{
    private readonly string _exePath;
    private readonly string _repoRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    private volatile bool _disposeStarted;
    private readonly ConcurrentQueue<string> _stderrLines = new();
    private Process? _process;
    private CancellationTokenSource? _stderrCts;
    private Task? _stderrTask;

    public string Device { get; private set; } = "";
    public bool Warmed { get; private set; }
    public string LastError { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Policy { get; set; } = "auto";
    public string RepoRoot => _repoRoot;
    public string ExePath => _exePath;
    public IReadOnlySet<string> Capabilities { get; private set; } = new HashSet<string>();
    public event Action<string>? Progress;

    public async Task RequireCapabilityAsync(string capability, CancellationToken cancellationToken = default)
    {
        using var request = LinkRequest(cancellationToken);
        cancellationToken = request.Token;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsAlive()) await StartLockedAsync(cancellationToken).ConfigureAwait(false);
            if (!Capabilities.Contains(capability))
                throw new NotSupportedException($"This worker does not support {capability}. Install the matching ColorCorrect engine to use this feature.");
        }
        finally { _gate.Release(); }
    }

    public SpeedyWorker(string? exePath = null, string? repoRoot = null)
    {
        _repoRoot = repoRoot ?? (EnginePaths.TryFindRepoRoot(out var root) ? root : Directory.GetCurrentDirectory());
        _exePath = exePath ?? (EnginePaths.TryFindExecutable(out var exe) ? exe : "");
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        using var request = LinkRequest(cancellationToken);
        cancellationToken = request.Token;
        if (string.IsNullOrWhiteSpace(_exePath) || !File.Exists(_exePath))
        {
            throw new FileNotFoundException(
                "The ColorCorrect processing engine was not found. Build it with scripts/start_desktop.ps1 or set CCP_ENGINE_EXE.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Recycle the worker after a runtime or backend change. Do not call on a single timeout.</summary>
    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        using var request = LinkRequest(cancellationToken);
        cancellationToken = request.Token;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<JsonElement> SendAsync(
        Dictionary<string, object?> payload,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var request = LinkRequest(cancellationToken);
        cancellationToken = request.Token;
        timeout ??= TimeSpan.FromMinutes(10);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsAlive())
            {
                await StartLockedAsync(cancellationToken).ConfigureAwait(false);
            }

            var requestedPolicy = payload.TryGetValue("policy", out var policy) ? policy?.ToString() : Policy;
            if (requestedPolicy == "cpu-inference" && !Capabilities.Contains("cpu_inference_v1"))
                throw new NotSupportedException("This worker cannot run explicit CPU inference. Install the matching ColorCorrect engine.");

            var line = JsonSerializer.Serialize(payload);
            JsonElement result;
            try
            {
                await _process!.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
                result = await ReadResultAsync(_process.StandardOutput, timeout.Value, Progress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LastError = "Processing cancelled. The worker will restart for the next operation.";
                await StopLockedAsync().ConfigureAwait(false);
                throw;
            }
            catch (TimeoutException)
            {
                // The protocol is a sequential NDJSON stream. If the timed-out
                // process is left alive, its eventual reply can be consumed as
                // the result of the next command. Recycle it before releasing
                // the gate so a stale reply can never poison the stream.
                LastError = "worker command timed out; worker was restarted to preserve protocol integrity";
                await StopLockedAsync().ConfigureAwait(false);
                throw;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                LastError = "worker command cancelled";
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkerReply> ExecAsync(
        IReadOnlyList<string> argv,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        // Legacy argv path is not used by ColorCorrect Studio. Map a lone detect/correct token to typed cmds.
        var cmd = argv.Count > 0 ? argv[0] : "ping";
        var payload = new Dictionary<string, object?> { ["cmd"] = cmd == "correct" ? "run" : cmd };
        var result = await SendAsync(payload, timeout, cancellationToken).ConfigureAwait(false);
        var ok = result.TryGetProperty("ok", out var okEl) && okEl.ValueKind != JsonValueKind.False && okEl.GetBoolean();
        var raw = result.GetRawText();
        var err = result.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
        return new WorkerReply(ok, ok ? 0 : 1, raw, err, raw);
    }

    public async Task<DetectOutcome> DetectAsync(
        string inputPath,
        double crop = 0.75,
        string device = "gpu",
        CancellationToken cancellationToken = default,
        bool retryInference = false)
    {
        var jsonOut = Path.Combine(Path.GetTempPath(), $"ccp-detect-{Guid.NewGuid():N}.json");
        try
        {
            var result = await SendAsync(new Dictionary<string, object?>
            {
                ["cmd"] = "detect",
                ["image"] = inputPath,
                ["crop"] = crop,
                ["policy"] = string.Equals(device, "cpu", StringComparison.OrdinalIgnoreCase) ? "cpu" : Policy,
                ["handshake_out"] = jsonOut,
                ["retry_inference"] = retryInference,
            }, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);

            var ok = result.TryGetProperty("ok", out var okEl) && okEl.ValueKind != JsonValueKind.False && okEl.GetBoolean();
            var chartType = result.TryGetProperty("chart_type", out var t) ? t.GetString() ?? "" : "";
            var charts = 0;
            if (result.TryGetProperty("n_charts", out var n) && n.TryGetInt32(out var parsed))
            {
                charts = parsed;
            }

            var handshakePath = result.TryGetProperty("handshake", out var hs) ? hs.GetString() ?? jsonOut : jsonOut;
            var json = File.Exists(handshakePath) ? await File.ReadAllTextAsync(handshakePath, cancellationToken).ConfigureAwait(false) : "";
            var error = result.TryGetProperty("error", out var err) ? err.GetString() ?? "" : "";
            if (!ok && string.IsNullOrWhiteSpace(error))
            {
                error = "detect failed";
            }

            return new DetectOutcome(ok, json, handshakePath, chartType, charts, error);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new DetectOutcome(false, "", "", "", 0, ex.Message);
        }
    }

    public async Task<JsonElement> DevicesAsync(CancellationToken cancellationToken = default) =>
        await SendAsync(new Dictionary<string, object?> { ["cmd"] = "devices", ["policy"] = Policy }, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

    public IReadOnlyList<string> RecentStderr(int max = 20)
    {
        var snapshot = _stderrLines.ToArray();
        return snapshot.Length <= max ? snapshot : snapshot[^max..];
    }

    public string FormatRecentStderr(int max = 8)
    {
        var lines = RecentStderr(max);
        return lines.Count == 0 ? "" : string.Join(Environment.NewLine, lines);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            _disposeStarted = true;
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private CancellationTokenSource LinkRequest(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposeStarted, this);
        return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
    }

    private async Task DisposeCoreAsync()
    {
        // Cancel reads, warmup and queued commands before acquiring the gate.
        // Otherwise an uncancelled deferred preview can delay exit by minutes.
        _lifetime.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _lifetime.Dispose();
        }
    }

    private bool IsAlive() => _process is { HasExited: false };

    private async Task StartLockedAsync(CancellationToken cancellationToken)
    {
        await StopLockedAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        LastError = "";
        Warmed = false;
        Device = "";
        Title = "";

        var psi = new ProcessStartInfo(_exePath)
        {
            WorkingDirectory = _repoRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["CCP_NEW_UI_ROOT"] = _repoRoot;
        if (!psi.Environment.ContainsKey("CCP_INFERENCE_COMPONENTS") && InstalledSetup.ComponentAllowlist() is { } selectedComponents)
            psi.Environment["CCP_INFERENCE_COMPONENTS"] = selectedComponents;
        var extras = new List<string>();
        var engineDir = Path.GetDirectoryName(_exePath);
        if (!string.IsNullOrWhiteSpace(engineDir))
        {
            extras.Add(engineDir);
            var ortDll = Path.Combine(engineDir, "onnxruntime.dll");
            if (File.Exists(ortDll))
            {
                psi.Environment["ORT_DYLIB_PATH"] = ortDll;
            }
        }
        if (extras.Count > 0)
        {
            var nativePaths = string.Join(Path.PathSeparator, extras);
            var path = psi.Environment["PATH"] ?? Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.Environment["PATH"] = nativePaths + Path.PathSeparator + path;
            if (OperatingSystem.IsLinux())
            {
                var current = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "";
                psi.Environment["LD_LIBRARY_PATH"] = nativePaths +
                    (string.IsNullOrWhiteSpace(current) ? "" : Path.PathSeparator + current);
            }
            else if (OperatingSystem.IsMacOS())
            {
                var current = Environment.GetEnvironmentVariable("DYLD_LIBRARY_PATH") ?? "";
                psi.Environment["DYLD_LIBRARY_PATH"] = nativePaths +
                    (string.IsNullOrWhiteSpace(current) ? "" : Path.PathSeparator + current);
            }
        }

        _process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start the ColorCorrect processing engine");
        _stderrCts = new CancellationTokenSource();
        _stderrTask = DrainStderrAsync(_process, _stderrCts.Token);

        string? ready;
        try
        {
            ready = await ReadLineAsync(_process.StandardOutput, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LastError = "worker produced no ready event";
            await StopLockedAsync().ConfigureAwait(false);
            throw new TimeoutException(LastError);
        }
        catch (OperationCanceledException)
        {
            await StopLockedAsync().ConfigureAwait(false);
            throw;
        }

        if (string.IsNullOrWhiteSpace(ready))
        {
            LastError = "worker produced no ready event";
            await StopLockedAsync().ConfigureAwait(false);
            throw new InvalidOperationException(LastError);
        }

        using var doc = JsonDocument.Parse(ready);
        var root = doc.RootElement;
        Capabilities = root.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Array
            ? capabilities.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>();
        var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
        Device = root.TryGetProperty("device", out var dev) ? dev.GetString() ?? "" : "";
        Warmed = root.TryGetProperty("warmed", out var warm) && warm.GetBoolean();
        Title = root.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "";
        if (root.TryGetProperty("error", out var err))
        {
            LastError = err.GetString() ?? "";
        }
        if (!ok)
        {
            await StopLockedAsync().ConfigureAwait(false);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(LastError) ? "worker ready failed" : LastError);
        }

        if (Policy == "cpu-inference" && !Capabilities.Contains("cpu_inference_v1"))
        {
            await StopLockedAsync().ConfigureAwait(false);
            throw new NotSupportedException("This worker cannot run explicit CPU inference. Install the matching ColorCorrect engine.");
        }
        await TryWarmupLockedAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TryWarmupLockedAsync(CancellationToken cancellationToken)
    {
        if (_process is not { HasExited: false })
        {
            return;
        }

        var line = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["cmd"] = "warmup",
            ["policy"] = Policy,
        });
        try
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            var result = await ReadResultAsync(_process.StandardOutput, TimeSpan.FromMinutes(8), Progress, cancellationToken).ConfigureAwait(false);
            var ok = result.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            Device = result.TryGetProperty("device", out var dev) ? dev.GetString() ?? Device : Device;
            Warmed = result.TryGetProperty("warmed", out var warm) && warm.GetBoolean();
            if (result.TryGetProperty("title", out var title))
            {
                Title = title.GetString() ?? Title;
            }

            if (!ok && result.TryGetProperty("error", out var err))
            {
                LastError = err.GetString() ?? LastError;
            }
        }
        catch (OperationCanceledException)
        {
            await StopLockedAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            // A failed read/parse leaves the request/reply stream in an unknown
            // state. Do not let the next command reuse it.
            await StopLockedAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"worker warmup failed: {ex.Message}", ex);
        }
    }

    private async Task StopLockedAsync()
    {
        var proc = _process;
        _process = null;
        if (proc is null)
        {
            return;
        }

        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            if (!proc.HasExited && proc.StandardInput is { BaseStream.CanWrite: true })
            {
                await proc.StandardInput.WriteLineAsync("{\"cmd\":\"shutdown\"}".AsMemory(), grace.Token).WaitAsync(grace.Token).ConfigureAwait(false);
                await proc.StandardInput.FlushAsync(grace.Token).WaitAsync(grace.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Process may already be gone.
        }

        try
        {
            await proc.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!proc.HasExited)
                    _stderrLines.Enqueue($"Worker shutdown could not complete: {ex.Message}");
            }
        }
        catch
        {
            // Ignore teardown races.
        }

        _stderrCts?.Cancel();
        if (_stderrTask is not null)
        {
            try
            {
                await _stderrTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch
            {
                // Drain cancellation.
            }
        }
        _stderrCts?.Dispose();
        _stderrCts = null;
        _stderrTask = null;
        proc.Dispose();
    }

    private Task DrainStderrAsync(Process process, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && !process.HasExited)
                {
                    var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }
                    if (line.Length > 0)
                    {
                        _stderrLines.Enqueue(line);
                        while (_stderrLines.Count > 200 && _stderrLines.TryDequeue(out _))
                        {
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }, cancellationToken);
    }

    private static async Task<JsonElement> ReadResultAsync(StreamReader reader, TimeSpan timeout, Action<string>? progress, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var remain = deadline - DateTime.UtcNow;
            if (remain <= TimeSpan.Zero)
            {
                throw new TimeoutException("timed out waiting for the worker");
            }

            var line = await ReadLineAsync(reader, remain, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidOperationException("worker returned an empty line");
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement.Clone();
            if (root.TryGetProperty("type", out var type) &&
                string.Equals(type.GetString(), "progress", StringComparison.OrdinalIgnoreCase))
            {
                var message = root.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(message))
                {
                    progress?.Invoke(message);
                }

                continue;
            }

            return root;
        }
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            // Some redirected process pipes cannot abort their underlying OS
            // read immediately. Bound our wait independently; the caller then
            // terminates that worker rather than reusing its pending read.
            return await reader.ReadLineAsync(linked.Token).AsTask().WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("timed out waiting for the worker");
        }
    }
}
