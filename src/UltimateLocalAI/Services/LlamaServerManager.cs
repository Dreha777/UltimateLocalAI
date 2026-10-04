using System.Diagnostics;
using System.Net.Http;
using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public sealed class LlamaServerManager : IAsyncDisposable
{
    private Process? _process;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private string _lastBackendLine = "";
    private DateTime _lastBackendLineUtc;
    public bool IsRunning => _process is { HasExited: false };
    public BackendChoice? CurrentBackend { get; private set; }
    public string GpuOffloadSummary { get; private set; } = "";
    public event Action<string>? StatusChanged;

    public async Task StartAsync(BackendChoice backend, AppConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.ModelPath) || !File.Exists(config.ModelPath))
            throw new FileNotFoundException("GGUF-модель не найдена.", config.ModelPath);
        if (!File.Exists(backend.ExecutablePath))
            throw new FileNotFoundException($"Backend не найден: {backend.ExecutablePath}. Сначала выполните BUILD.cmd.");

        await StopAsync();
        CurrentBackend = backend;
        StatusChanged?.Invoke("Запуск backend…");

        var psi = new ProcessStartInfo
        {
            FileName = backend.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(backend.ExecutablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        Add(psi, "-m", config.ModelPath);
        Add(psi, "--host", "127.0.0.1");
        Add(psi, "--port", config.Port.ToString());
        Add(psi, "-c", Math.Max(512, config.Runtime.ContextSize).ToString());
        Add(psi, "-t", Math.Max(1, config.Runtime.Threads).ToString());
        Add(psi, "-tb", Math.Max(1, config.Runtime.ThreadsBatch).ToString());
        Add(psi, "-b", Math.Max(64, config.Runtime.BatchSize).ToString());
        Add(psi, "-ub", Math.Max(32, config.Runtime.UBatchSize).ToString());
        Add(psi, "-fa", NormalizeFlash(config.Runtime.FlashAttention));
        Add(psi, "-ctk", config.Runtime.CacheTypeK);
        Add(psi, "-ctv", config.Runtime.CacheTypeV);
        Add(psi, "-lm", config.Runtime.LoadMode);
        Add(psi, "--reasoning", config.Runtime.ReasoningMode);
        Add(psi, "--reasoning-effort", config.Runtime.ReasoningEffort);
        Add(psi, "--prio", Math.Clamp(config.Runtime.Priority, -1, 2).ToString());
        Add(psi, "--offline");
        Add(psi, "--metrics");
        Add(psi, "--log-timestamps");
        Add(psi, "--cache-prompt");

        // В Auto-режиме не тратим время на отдельный прогрев перед готовностью сервера.
        // Первый реальный запрос сам выполнит необходимые вычисления.
        if (string.Equals(config.BackendMode, "Auto", StringComparison.OrdinalIgnoreCase))
            Add(psi, "--no-warmup");

        if (backend.UsesGpu)
        {
            var gpuLayers = config.Runtime.GpuLayers?.Trim();
            // llama.cpp b11060 уже использует GPU layers=auto и --fit=on по умолчанию.
            // Не передаём -ngl auto явно: так auto-fit может свободно подобрать VRAM-параметры.
            if (!string.IsNullOrWhiteSpace(gpuLayers) &&
                !gpuLayers.Equals("auto", StringComparison.OrdinalIgnoreCase))
                Add(psi, "-ngl", gpuLayers);
        }
        else
        {
            Add(psi, "-ngl", "0");
            Add(psi, "--device", "none");
        }

        LogService.Info(
            $"LLAMA-START exe=\"{backend.ExecutablePath}\" backend=\"{backend.Name}\" " +
            $"args={string.Join(" ", psi.ArgumentList.Select(QuoteForLog))}");

        _lastBackendLine = "";
        _lastBackendLineUtc = default;
        GpuOffloadSummary = "";

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process = process;
        process.OutputDataReceived += (_, e) => HandleBackendLine(e.Data);
        process.ErrorDataReceived += (_, e) => HandleBackendLine(e.Data);
        process.Exited += (_, _) =>
        {
            var code = SafeExitCode(process);
            LogService.Warn($"llama-server exited with code {code}");
            StatusChanged?.Invoke($"Backend остановлен (код {code})");
        };

        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить llama-server.exe");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await WaitForHealthAsync(config.Port, ct);
        StatusChanged?.Invoke("Модель готова");
    }

    private async Task WaitForHealthAsync(int port, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var deadline = DateTime.UtcNow.AddMinutes(3);
        var uri = $"http://127.0.0.1:{port}/health";
        Exception? lastException = null;
        string lastHealthBody = "";
        var nextUiUpdate = TimeSpan.Zero;
        var healthEndpointSeen = false;

        LogService.Info("STARTUP-WAIT begin timeout=180s");

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var process = _process;
            if (process is null || process.HasExited)
                throw new InvalidOperationException(
                    $"llama-server завершился во время загрузки модели. Код: {SafeExitCode(process)}. " +
                    $"Последняя строка: {LastBackendDiagnostic()}");

            try
            {
                using var resp = await _http.GetAsync(uri, ct);
                lastHealthBody = await resp.Content.ReadAsStringAsync(ct);

                if ((int)resp.StatusCode == 200)
                {
                    sw.Stop();
                    LogService.Info($"STARTUP-READY elapsed_ms={sw.ElapsedMilliseconds} health=200");
                    return;
                }

                if ((int)resp.StatusCode == 503)
                    healthEndpointSeen = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastException = ex;
            }

            if (sw.Elapsed >= nextUiUpdate)
            {
                var seconds = Math.Max(1, (int)sw.Elapsed.TotalSeconds);
                var phase = healthEndpointSeen
                    ? $"Загрузка GGUF… {seconds} с"
                    : $"Запуск llama-server… {seconds} с";

                StatusChanged?.Invoke(phase);
                LogService.Info(
                    $"STARTUP-WAIT elapsed_s={seconds} health_seen={healthEndpointSeen} " +
                    $"last_health={Compact(lastHealthBody)} last_backend={Compact(LastBackendDiagnostic())}");

                nextUiUpdate = sw.Elapsed + TimeSpan.FromSeconds(5);
            }

            await Task.Delay(700, ct);
        }

        sw.Stop();
        var diagnostic =
            $"Модель не стала готова за {sw.Elapsed.TotalSeconds:0} с. " +
            $"Health: {Compact(lastHealthBody)}. Последняя строка backend: {LastBackendDiagnostic()}";
        LogService.Error("STARTUP-TIMEOUT " + diagnostic, lastException);
        throw new TimeoutException(diagnostic, lastException);
    }

    private void HandleBackendLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        _lastBackendLine = line.Trim();
        _lastBackendLineUtc = DateTime.UtcNow;

        if (_lastBackendLine.Contains("offloaded", StringComparison.OrdinalIgnoreCase) &&
            _lastBackendLine.Contains("layers to GPU", StringComparison.OrdinalIgnoreCase))
        {
            var start = _lastBackendLine.IndexOf("offloaded", StringComparison.OrdinalIgnoreCase);
            GpuOffloadSummary = start >= 0 ? _lastBackendLine[start..] : _lastBackendLine;
            LogService.Info("GPU-OFFLOAD " + GpuOffloadSummary);
        }

        LogService.Info("llama: " + _lastBackendLine);
    }

    private string LastBackendDiagnostic()
    {
        if (string.IsNullOrWhiteSpace(_lastBackendLine)) return "нет вывода";
        if (_lastBackendLineUtc == default) return _lastBackendLine;
        var age = Math.Max(0, (int)(DateTime.UtcNow - _lastBackendLineUtc).TotalSeconds);
        return $"{_lastBackendLine} ({age} с назад)";
    }

    private static int SafeExitCode(Process? process)
    {
        try { return process is null ? -1 : process.ExitCode; }
        catch { return -1; }
    }

    private static string Compact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var oneLine = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length <= 240 ? oneLine : oneLine[..240] + "…";
    }

    public async Task StopAsync()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex) { LogService.Warn("Stop backend: " + ex.Message); }
        finally { _process.Dispose(); _process = null; }
    }

    private static void Add(ProcessStartInfo psi, string key, string? value = null)
    {
        psi.ArgumentList.Add(key);
        if (value is not null) psi.ArgumentList.Add(value);
    }

    private static string NormalizeFlash(string value) => value.ToLowerInvariant() switch
    {
        "on" or "вкл" or "включено" => "on",
        "off" or "выкл" or "выключено" => "off",
        _ => "auto"
    };

    private static string QuoteForLog(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        return value.Any(char.IsWhiteSpace) || value.Contains('\"')
            ? "\"" + value.Replace("\"", "\\\"") + "\""
            : value;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
    }
}
