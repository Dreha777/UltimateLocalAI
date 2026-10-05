using System.Diagnostics;
using System.Net.Http;
using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public sealed class DocumentVisionServerManager : IAsyncDisposable
{
    private Process? _process;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };

    public async Task StartAsync(BackendChoice backend, AppConfig config, CancellationToken ct = default)
    {
        if (!File.Exists(config.DocumentVisionModelPath))
            throw new FileNotFoundException("Vision GGUF-модель не найдена.", config.DocumentVisionModelPath);
        if (!File.Exists(config.DocumentVisionMmprojPath))
            throw new FileNotFoundException("Vision mmproj GGUF не найден.", config.DocumentVisionMmprojPath);
        if (!File.Exists(backend.ExecutablePath))
            throw new FileNotFoundException("llama-server для Document Vision не найден.", backend.ExecutablePath);

        await StopAsync();

        var psi = new ProcessStartInfo
        {
            FileName = backend.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(backend.ExecutablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        Add(psi, "-m", config.DocumentVisionModelPath);
        Add(psi, "--mmproj", config.DocumentVisionMmprojPath);
        Add(psi, "--host", "127.0.0.1");
        Add(psi, "--port", Math.Clamp(config.DocumentVisionPort, 1024, 65535).ToString());
        Add(psi, "-c", Math.Max(8192, config.Runtime.ContextSize).ToString());
        Add(psi, "-t", Math.Max(1, config.Runtime.Threads).ToString());
        Add(psi, "-tb", Math.Max(1, config.Runtime.ThreadsBatch).ToString());
        Add(psi, "-b", "256");
        Add(psi, "-ub", "128");
        Add(psi, "--offline");
        Add(psi, "--media-path", AppPaths.TempDir);
        Add(psi, "--no-webui");
        Add(psi, "--no-warmup");

        if (!config.DocumentVisionUseGpu)
        {
            Add(psi, "-ngl", "0");
            Add(psi, "--device", "none");
            Add(psi, "--no-mmproj-offload");
        }

        LogService.Info(
            $"VISION-SERVER-START model=\"{config.DocumentVisionModelPath}\" mmproj=\"{config.DocumentVisionMmprojPath}\" " +
            $"backend=\"{backend.Name}\" port={config.DocumentVisionPort}");

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process = process;
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) LogService.Info("vision: " + e.Data.Trim());
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) LogService.Info("vision: " + e.Data.Trim());
        };
        process.Exited += (_, _) => LogService.Warn("Document Vision llama-server exited.");

        if (!process.Start())
            throw new InvalidOperationException("Не удалось запустить Document Vision llama-server.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await WaitForHealthAsync(config.DocumentVisionPort, ct);
    }

    private async Task WaitForHealthAsync(int port, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(4);
        Exception? last = null;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (_process is null || _process.HasExited)
                throw new InvalidOperationException("Document Vision server завершился при загрузке модели.");

            try
            {
                using var response = await _http.GetAsync($"http://127.0.0.1:{port}/health", ct);
                if ((int)response.StatusCode == 200)
                {
                    LogService.Info("VISION-SERVER-READY");
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex;
            }

            await Task.Delay(700, ct);
        }

        throw new TimeoutException("Vision-модель не стала готова за 240 секунд.", last);
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
        catch (Exception ex)
        {
            LogService.Warn("Stop Document Vision backend: " + ex.Message);
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private static void Add(ProcessStartInfo psi, string key, string? value = null)
    {
        psi.ArgumentList.Add(key);
        if (value is not null) psi.ArgumentList.Add(value);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
    }
}
