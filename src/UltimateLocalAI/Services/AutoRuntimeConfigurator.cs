using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public sealed class AutoRuntimeProfile
{
    public RuntimeSettings Runtime { get; init; } = new();
    public long ModelSizeMb { get; init; }
    public string Reason { get; init; } = "";
}

public sealed class AutoRuntimeConfigurator
{
    public AutoRuntimeProfile Create(HardwareInfo hw, BackendChoice backend, RuntimeSettings current, string modelPath)
    {
        var logical = Math.Max(1, hw.LogicalProcessors);
        var reservedThreads = logical >= 4 ? Math.Max(1, logical / 4) : 0;
        var workerThreads = Math.Max(1, logical - reservedThreads);

        var modelSizeMb = 0L;
        try
        {
            if (File.Exists(modelPath))
                modelSizeMb = new FileInfo(modelPath).Length / 1024 / 1024;
        }
        catch { }

        var context = 4096;
        if (hw.TotalRamMb > 0 && hw.TotalRamMb < 12_000)
            context = 2048;
        else if (hw.TotalRamMb >= 32_000 && modelSizeMb > 0 && modelSizeMb < hw.TotalRamMb * 0.35)
            context = 8192;

        if (hw.TotalRamMb > 0 && modelSizeMb > hw.TotalRamMb * 0.65)
            context = Math.Min(context, 2048);

        var batch = hw.TotalRamMb > 0 && hw.TotalRamMb <= 16_384 ? 256 : 512;
        var ubatch = batch / 2;

        // Для старых AVX-процессоров и малой VRAM начинаем с более консервативного batch.
        if (!hw.Avx2 || (backend.UsesCuda && hw.GpuVramMb > 0 && hw.GpuVramMb < 4096))
        {
            batch = Math.Min(batch, 256);
            ubatch = Math.Min(ubatch, 128);
        }

        var runtime = new RuntimeSettings
        {
            ContextSize = context,
            Threads = workerThreads,
            ThreadsBatch = workerThreads,
            GpuLayers = backend.UsesCuda ? "auto" : "0",
            BatchSize = batch,
            UBatchSize = ubatch,
            FlashAttention = "auto",
            CacheTypeK = "q8_0",
            CacheTypeV = "q8_0",
            LoadMode = "auto",
            ReasoningMode = current.ReasoningMode,
            ReasoningEffort = current.ReasoningEffort,
            Priority = 0
        };

        var reason = $"Auto Runtime: {logical} лог. CPU -> {workerThreads} рабочих потоков; " +
                     $"RAM {hw.TotalRamMb / 1024.0:0.#} ГБ; модель {modelSizeMb / 1024.0:0.00} ГБ; " +
                     $"context {context}; batch {batch}/{ubatch}; GPU layers {(backend.UsesCuda ? "llama.cpp auto-fit" : "off")}.";

        return new AutoRuntimeProfile
        {
            Runtime = runtime,
            ModelSizeMb = modelSizeMb,
            Reason = reason
        };
    }

    public static string ToLogLine(HardwareInfo hw, BackendChoice backend, AutoRuntimeProfile profile)
    {
        var r = profile.Runtime;
        return "AUTO-RUNTIME " +
               $"cpu=\"{hw.CpuName}\" logical={hw.LogicalProcessors} avx={hw.Avx} avx2={hw.Avx2} " +
               $"ram_mb={hw.TotalRamMb} gpu=\"{hw.GpuName}\" vram_mb={hw.GpuVramMb} cc=\"{hw.ComputeCapability}\" " +
               $"backend=\"{backend.Name}\" model_mb={profile.ModelSizeMb} context={r.ContextSize} " +
               $"threads={r.Threads} threads_batch={r.ThreadsBatch} batch={r.BatchSize} ubatch={r.UBatchSize} " +
               $"gpu_layers={r.GpuLayers} flash={r.FlashAttention} cache_k={r.CacheTypeK} cache_v={r.CacheTypeV} " +
               $"load_mode={r.LoadMode} priority={r.Priority}";
    }
}
