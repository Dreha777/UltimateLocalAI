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

        // AvailableRamMb обновляется монитором ресурсов перед запуском.
        // Оставляем Windows запас, чтобы старые ПК не уходили в интенсивный pagefile.
        var totalRamMb = Math.Max(0, hw.TotalRamMb);
        var availableRamMb = hw.AvailableRamMb > 0 ? hw.AvailableRamMb : totalRamMb;
        var osReserveMb = totalRamMb <= 8_192 ? 1_536L : totalRamMb <= 16_384 ? 2_048L : 3_072L;
        var usableRamMb = Math.Max(1_024L, availableRamMb - osReserveMb);

        var modelSizeMb = 0L;
        try
        {
            if (File.Exists(modelPath))
                modelSizeMb = new FileInfo(modelPath).Length / 1024 / 1024;
        }
        catch { }

        var context = 4096;
        if (totalRamMb > 0 && totalRamMb <= 6_144)
            context = 1024;
        else if (totalRamMb > 0 && totalRamMb <= 12_288)
            context = 2048;
        else if (totalRamMb >= 32_000 && modelSizeMb > 0 && modelSizeMb < usableRamMb * 0.45)
            context = 8192;

        // Реально свободная память важнее установленного объёма.
        if (modelSizeMb > 0 && usableRamMb < modelSizeMb * 1.10)
            context = Math.Min(context, 1024);
        else if (modelSizeMb > 0 && usableRamMb < modelSizeMb * 1.35)
            context = Math.Min(context, 2048);

        var batch = totalRamMb > 0 && totalRamMb <= 8_192 ? 128
            : totalRamMb > 0 && totalRamMb <= 16_384 ? 256
            : 512;
        var ubatch = Math.Max(64, batch / 2);

        if (availableRamMb > 0 && availableRamMb < 6_144)
        {
            batch = Math.Min(batch, 128);
            ubatch = Math.Min(ubatch, 64);
        }

        // Для AVX-only CPU и маленькой VRAM используем консервативные batch.
        if (!hw.Avx2 || (backend.UsesGpu && hw.GpuVramMb > 0 && hw.GpuVramMb < 4096))
        {
            batch = Math.Min(batch, 256);
            ubatch = Math.Min(ubatch, 128);
        }
        if (backend.UsesGpu && hw.GpuVramMb > 0 && hw.GpuVramMb < 2560)
        {
            batch = Math.Min(batch, 128);
            ubatch = Math.Min(ubatch, 64);
        }

        var runtime = new RuntimeSettings
        {
            ContextSize = context,
            Threads = workerThreads,
            ThreadsBatch = workerThreads,
            GpuLayers = backend.UsesGpu ? "auto" : "0",
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
                     $"RAM {totalRamMb / 1024.0:0.#} ГБ, свободно {availableRamMb / 1024.0:0.#} ГБ; модель {modelSizeMb / 1024.0:0.00} ГБ; " +
                     $"context {context}; batch {batch}/{ubatch}; GPU layers {(backend.UsesGpu ? "llama.cpp auto-fit" : "off")}.";

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
               $"ram_mb={hw.TotalRamMb} ram_available_mb={hw.AvailableRamMb} gpu=\"{hw.GpuName}\" gpu_vendor=\"{hw.GpuVendor}\" vram_mb={hw.GpuVramMb} cc=\"{hw.ComputeCapability}\" " +
               $"backend=\"{backend.Name}\" model_mb={profile.ModelSizeMb} context={r.ContextSize} " +
               $"threads={r.Threads} threads_batch={r.ThreadsBatch} batch={r.BatchSize} ubatch={r.UBatchSize} " +
               $"gpu_layers={r.GpuLayers} flash={r.FlashAttention} cache_k={r.CacheTypeK} cache_v={r.CacheTypeV} " +
               $"load_mode={r.LoadMode} priority={r.Priority}";
    }
}
