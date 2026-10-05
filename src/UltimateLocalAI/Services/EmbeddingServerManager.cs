using System.Diagnostics;
using System.Net.Http;
using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public sealed class EmbeddingServerManager : IAsyncDisposable
{
    private Process? _process;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(BackendChoice backend, AppConfig config, string modelPath, CancellationToken ct = default)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Embedding GGUF-модель не найдена.", modelPath);
        if (!File.Exists(backend.ExecutablePath))
            throw new FileNotFoundException("llama-server для embedding не найден.", backend.ExecutablePath);

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
        Add(psi, "--port", Math.Clamp(config.EmbeddingPort, 1024, 65535).ToString());
        Add(psi, "--embeddings");
        var pooling = NormalizePooling(config.EmbeddingPooling);
        if (pooling is not null)
            Add(psi, "--pooling", pooling);
        Add(psi, "-c", "2048");
        Add(psi, "-t", Math.Max(1, config.Runtime.Threads).ToString());
        Add(psi, "-tb", Math.Max(1, config.Runtime.ThreadsBatch).ToString());
        Add(psi, "-b", "1024");
        Add(psi, "-ub", "512");
        Add(psi, "--offline");
        Add(psi, "--no-webui");
        Add(psi, "--no-warmup");

        if (backend.UsesGpu)
        {
            Add(psi, "-ngl", "auto");
        }
        else
        {
            Add(psi, "-ngl", "0");
            Add(psi, "--device", "none");
        }

        LogService.Info($"EMBED-SERVER-START exe=\"{backend.ExecutablePath}\" model=\"{modelPath}\" port={config.EmbeddingPort}");

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process = process;
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) LogService.Info("embed: " + e.Data.Trim());
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) LogService.Info("embed: " + e.Data.Trim());
        };
        process.Exited += (_, _) => LogService.Warn("Embedding llama-server exited.");

        if (!process.Start())
            throw new InvalidOperationException("Не удалось запустить embedding llama-server.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await WaitForHealthAsync(config.EmbeddingPort, ct);
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
                throw new InvalidOperationException("Embedding server завершился при загрузке модели.");

            try
            {
                using var response = await _http.GetAsync(uri, ct);
                if ((int)response.StatusCode == 200)
                {
                    LogService.Info("EMBED-SERVER-READY");
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex;
            }

            await Task.Delay(600, ct);
        }

        throw new TimeoutException("Embedding-модель не стала готова за 180 секунд.", last);
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
            LogService.Warn("Stop embedding backend: " + ex.Message);
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private static string? NormalizePooling(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "mean" => "mean",
            "cls" => "cls",
            "last" => "last",
            _ => null
        };

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
