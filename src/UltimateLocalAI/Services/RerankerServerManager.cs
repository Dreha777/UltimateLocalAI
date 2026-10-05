using System.Diagnostics;
using System.Net.Http;
using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public sealed class RerankerServerManager : IAsyncDisposable
{
    private Process? _process;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };

    public async Task StartAsync(BackendChoice backend, AppConfig config, string modelPath, CancellationToken ct = default)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("GGUF reranker-модель не найдена.", modelPath);
        if (!File.Exists(backend.ExecutablePath))
            throw new FileNotFoundException("llama-server для reranker не найден.", backend.ExecutablePath);

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

        Add(psi, "-m", modelPath);
        Add(psi, "--host", "127.0.0.1");
        Add(psi, "--port", Math.Clamp(config.RerankerPort, 1024, 65535).ToString());
        Add(psi, "--embedding");
        Add(psi, "--reranking");
        Add(psi, "--pooling", "rank");
        Add(psi, "-c", "4096");
        Add(psi, "-t", Math.Max(1, config.Runtime.Threads).ToString());
        Add(psi, "-tb", Math.Max(1, config.Runtime.ThreadsBatch).ToString());
        Add(psi, "-b", "512");
        Add(psi, "-ub", "256");
        Add(psi, "--offline");
        Add(psi, "--no-webui");
        Add(psi, "--no-warmup");

        // Reranker is deliberately CPU-first. The main chat model may already occupy VRAM.
        Add(psi, "-ngl", "0");
        Add(psi, "--device", "none");

        LogService.Info($"RERANK-SERVER-START model="{modelPath}" port={config.RerankerPort}");

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process = process;
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) LogService.Info("rerank: " + e.Data.Trim());
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) LogService.Info("rerank: " + e.Data.Trim());
        };
        process.Exited += (_, _) => LogService.Warn("Reranker llama-server exited.");

        if (!process.Start())
            throw new InvalidOperationException("Не удалось запустить reranker llama-server.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await WaitForHealthAsync(config.RerankerPort, ct);
    }

    private async Task WaitForHealthAsync(int port, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        Exception? last = null;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (_process is null || _process.HasExited)
                throw new InvalidOperationException("Reranker server завершился при загрузке модели.");

            try
            {
                using var response = await _http.GetAsync($"http://127.0.0.1:{port}/health", ct);
                if ((int)response.StatusCode == 200)
                {
                    LogService.Info("RERANK-SERVER-READY");
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex;
            }

            await Task.Delay(600, ct);
        }

        throw new TimeoutException("Reranker-модель не стала готова за 180 секунд.", last);
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
            LogService.Warn("Stop reranker backend: " + ex.Message);
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
