using System.Diagnostics;
using System.Net.Http;
using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public sealed class LlamaServerManager : IAsyncDisposable
{
    private Process? _process;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };
    public bool IsRunning => _process is { HasExited: false };
    public BackendChoice? CurrentBackend { get; private set; }
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

        if (backend.UsesCuda)
            Add(psi, "-ngl", string.IsNullOrWhiteSpace(config.Runtime.GpuLayers) ? "auto" : config.Runtime.GpuLayers);
        else
        {
            Add(psi, "-ngl", "0");
            Add(psi, "--device", "none");
        }

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) LogService.Info("llama: " + e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) LogService.Info("llama: " + e.Data); };
        _process.Exited += (_, _) =>
        {
            var code = _process?.ExitCode ?? -1;
            LogService.Warn($"llama-server exited with code {code}");
            StatusChanged?.Invoke($"Backend остановлен (код {code})");
        };

        if (!_process.Start()) throw new InvalidOperationException("Не удалось запустить llama-server.exe");
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitForHealthAsync(config.Port, ct);
        StatusChanged?.Invoke("Модель готова");
    }

    private async Task WaitForHealthAsync(int port, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        var uri = $"http://127.0.0.1:{port}/health";
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_process is null || _process.HasExited)
                throw new InvalidOperationException($"llama-server завершился во время загрузки модели. Код: {_process?.ExitCode}");
            try
            {
                using var resp = await _http.GetAsync(uri, ct);
                if ((int)resp.StatusCode == 200) return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { last = ex; }
            await Task.Delay(700, ct);
        }
        throw new TimeoutException("Модель не стала готова за 3 минуты. Проверьте Logs и объём RAM/VRAM.", last);
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

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
    }
}
