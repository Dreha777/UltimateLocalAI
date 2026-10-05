using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public sealed class DocumentVisionSession : IAsyncDisposable
{
    private readonly BackendSelector _backendSelector;
    private readonly HardwareInfo _hardware;
    private readonly AppConfig _config;
    private readonly DocumentVisionServerManager _server = new();
    private readonly DocumentVisionApiClient _client = new();

    public bool IsReady { get; private set; }
    public string ModelName => Path.GetFileName(_config.DocumentVisionModelPath);

    public DocumentVisionSession(BackendSelector backendSelector, HardwareInfo hardware, AppConfig config)
    {
        _backendSelector = backendSelector;
        _hardware = hardware;
        _config = config;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!_config.DocumentVisionEnabled)
            return;

        if (!File.Exists(_config.DocumentVisionModelPath) ||
            !File.Exists(_config.DocumentVisionMmprojPath))
            return;

        var runtimeConfig = new AppConfig
        {
            RuntimeChannel = "Latest",
            CustomRuntimePath = _config.CustomRuntimePath,
            BackendMode = "Auto",
            Runtime = _config.Runtime
        };

        var backend = _config.DocumentVisionUseGpu
            ? _backendSelector.Select(_hardware, runtimeConfig)
            : _backendSelector.CpuFallback(_hardware, runtimeConfig);

        await _server.StartAsync(backend, _config, ct);
        IsReady = true;
    }

    public Task<DocumentVisionAnalysis> AnalyzePageAsync(
        string imagePath,
        int pageNumber,
        string? extractedText,
        CancellationToken ct = default)
    {
        if (!IsReady)
            throw new InvalidOperationException("Document Vision session не запущена.");

        return _client.AnalyzePageAsync(
            _config.DocumentVisionPort,
            imagePath,
            pageNumber,
            extractedText,
            ModelName,
            ct);
    }

    public async ValueTask DisposeAsync()
    {
        IsReady = false;
        await _server.DisposeAsync();
    }
}
