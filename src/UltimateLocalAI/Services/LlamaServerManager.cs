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
            throw new FileNotFoundException($"Backend не найден: {backend.ExecutablePath}.", backend.ExecutablePath);

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

        // Safe Auto mode:
        // llama.cpp itself chooses context/batch/thread/cache/load parameters.
        // We only provide the model, local endpoint and conservative scheduling.
        Add(psi, "-m", config.ModelPath);
        Add(psi, "--host", "127.0.0.1");
        Add(psi, "--port", config.Port.ToString());
        Add(psi, "--prio", "0");
        Add(psi, "--poll", "0");
        Add(psi, "--no-warmup");
        Add(psi, "--offline");
        Add(psi, "--log-timestamps");

        // CUDA is optional. When a CUDA backend is selected, let llama.cpp fit
        // unset parameters to available device memory. No manual layer count.
        if (backend.UsesCuda)
            Add(psi, "--fit", "on");

        LogService.Info(
            $"Starting llama-server. Backend={backend.Name}; CUDA={backend.UsesCuda}; " +
            $"Args={string.Join(" ", psi.ArgumentList.Select(QuoteForLog))}");

        _process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        _process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                LogService.Info("llama: " + e.Data);
        };

        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                LogService.Info("llama: " + e.Data);
        };

        _process.Exited += (_, _) =>
        {
            var code = _process?.ExitCode ?? -1;
            LogService.Warn($"llama-server exited with code {code}");
            StatusChanged?.Invoke($"Backend остановлен (код {code})");
        };

        if (!_process.Start())
            throw new InvalidOperationException("Не удалось запустить llama-server.exe");

        // During model loading keep the backend below the GUI priority so an
        // old 4C/8T CPU cannot make the whole desktop appear frozen.
        TrySetPriority(ProcessPriorityClass.BelowNormal);

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitForHealthAsync(config.Port, ct);

        // Once the model is ready, restore normal priority for generation.
        TrySetPriority(ProcessPriorityClass.Normal);

        StatusChanged?.Invoke("Модель готова");
    }

    private async Task WaitForHealthAsync(int port, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var deadline = started.AddMinutes(3);
        var nextStatus = started;
        var uri = $"http://127.0.0.1:{port}/health";
        Exception? last = null;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (_process is null || _process.HasExited)
                throw new InvalidOperationException(
                    $"llama-server завершился во время загрузки модели. Код: {_process?.ExitCode}");

            if (DateTime.UtcNow >= nextStatus)
            {
                var elapsed = (int)(DateTime.UtcNow - started).TotalSeconds;
                StatusChanged?.Invoke($"Загрузка модели… {elapsed} с");
                nextStatus = DateTime.UtcNow.AddSeconds(5);
            }

            try
            {
                using var resp = await _http.GetAsync(uri, ct);
                if ((int)resp.StatusCode == 200)
                    return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex;
            }

            await Task.Delay(700, ct);
        }

        throw new TimeoutException(
            "Модель не стала готова за 3 минуты. Проверьте Logs и объём RAM/VRAM.",
            last);
    }

    public async Task StopAsync()
    {
        if (_process is null)
            return;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("Stop backend: " + ex.Message);
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private void TrySetPriority(ProcessPriorityClass priority)
    {
        try
        {
            if (_process is { HasExited: false })
                _process.PriorityClass = priority;
        }
        catch (Exception ex)
        {
            LogService.Warn($"Cannot set llama-server priority to {priority}: {ex.Message}");
        }
    }

    private static void Add(ProcessStartInfo psi, string key, string? value = null)
    {
        psi.ArgumentList.Add(key);
        if (value is not null)
            psi.ArgumentList.Add(value);
    }

    private static string QuoteForLog(string value)
        => value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
    }
}
