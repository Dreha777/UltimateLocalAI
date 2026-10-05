using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public sealed class AutoRuntimeProfile
{
    public RuntimeSettings Runtime { get; init; } = new();
    public long ModelSizeMb { get; init; }
    public string Reason { get; init; } = "";
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class AutoRuntimeConfigurator
{
    public AutoRuntimeProfile Create(HardwareInfo hw, BackendChoice backend, RuntimeSettings current, string modelPath)
    {
        var warnings = new List<string>();
        var logical = Math.Max(1, hw.LogicalProcessors);
        var reservedThreads = logical >= 4 ? Math.Max(1, logical / 4) : 0;
        var workerThreads = Math.Max(1, logical - reservedThreads);

        // Quality-first Auto: keep enough memory for Windows, but do not sacrifice
        // context aggressively just to make the model start a little faster.
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

        // Respect a larger user preference when it is realistic, but never silently
        // collapse normal Auto operation to 1024 tokens. 4096 is the quality target.
        var requestedContext = current.ContextSize > 0 ? current.ContextSize : 4096;
        requestedContext = Math.Clamp(requestedContext, 2_048, 16_384);
        var context = Math.Max(4_096, requestedContext);

        if (context > 8_192 && totalRamMb > 0 && totalRamMb < 32_000)
        {
            context = 8_192;
            warnings.Add("Запрошенный контекст ограничен 8192: для большего контекста недостаточно системной RAM.");
        }

        if (context > 4_096 && totalRamMb > 0 && totalRamMb <= 16_384)
        {
            context = 4_096;
            warnings.Add("Контекст ограничен 4096, чтобы сохранить запас памяти для Windows.");
        }

        if (totalRamMb > 0 && totalRamMb <= 6_144)
        {
            context = 2_048;
            warnings.Add("Очень мало RAM: Auto вынужденно использует context=2048. Для лучшего качества нужна более лёгкая модель или больше памяти.");
        }
        else if (availableRamMb > 0 && availableRamMb < 4_096)
        {
            context = Math.Min(context, 2_048);
            warnings.Add("Свободной RAM меньше 4 ГБ: context временно снижен до 2048.");
        }
        else if (!backend.UsesGpu && modelSizeMb > 0 && usableRamMb < modelSizeMb * 1.02)
        {
            context = Math.Min(context, 2_048);
            warnings.Add("Модель почти полностью занимает доступную RAM: context снижен до 2048, но не до 1024.");
        }

        var batch = totalRamMb > 0 && totalRamMb <= 8_192 ? 128
            : totalRamMb > 0 && totalRamMb <= 16_384 ? 256
            : 512;
        var ubatch = Math.Max(64, batch / 2);

        if (availableRamMb > 0 && availableRamMb < 6_144)
        {
            batch = Math.Min(batch, 128);
            ubatch = Math.Min(ubatch, 64);
        }

        // Batch affects speed/memory, not model intelligence. Keep it conservative on
        // old CPUs/small GPUs without reducing the model context unnecessarily.
        if (!hw.Avx2 || (backend.UsesGpu && hw.GpuVramMb > 0 && hw.GpuVramMb < 4_096))
        {
            batch = Math.Min(batch, 256);
            ubatch = Math.Min(ubatch, 128);
        }
        if (backend.UsesGpu && hw.GpuVramMb > 0 && hw.GpuVramMb < 2_560)
        {
            batch = Math.Min(batch, 128);
            ubatch = Math.Min(ubatch, 64);
        }

        // Prefer full-precision KV cache when there is ample headroom. Otherwise q8_0
        // remains the safe quality/memory compromise; q4 KV is never selected by Auto.
        var ramHeadroomMb = Math.Max(0L, usableRamMb - modelSizeMb);
        var gpuHasF16Headroom = backend.UsesGpu &&
                                hw.GpuVramMb >= 12_288 &&
                                modelSizeMb > 0 &&
                                modelSizeMb <= hw.GpuVramMb * 0.62;
        var cpuHasF16Headroom = !backend.UsesGpu && ramHeadroomMb >= 6_144;
        var veryLargeSystem = totalRamMb >= 64_000 && availableRamMb >= 16_384;
        var useF16Kv = gpuHasF16Headroom || cpuHasF16Headroom || veryLargeSystem;
        var cacheType = useF16Kv ? "f16" : "q8_0";

        var runtime = new RuntimeSettings
        {
            ContextSize = context,
            Threads = workerThreads,
            ThreadsBatch = workerThreads,
            GpuLayers = backend.UsesGpu ? "auto" : "0",
            BatchSize = batch,
            UBatchSize = ubatch,
            FlashAttention = "auto",
            CacheTypeK = cacheType,
            CacheTypeV = cacheType,
            LoadMode = "auto",
            ReasoningMode = current.ReasoningMode,
            ReasoningEffort = current.ReasoningEffort,
            Priority = current.Priority
        };

        var reason = $"Auto Quality: {logical} лог. CPU -> {workerThreads} рабочих потоков; " +
                     $"RAM {totalRamMb / 1024.0:0.#} ГБ, свободно {availableRamMb / 1024.0:0.#} ГБ; модель {modelSizeMb / 1024.0:0.00} ГБ; " +
                     $"context {context}; KV {cacheType}; batch {batch}/{ubatch}; GPU layers {(backend.UsesGpu ? "llama.cpp auto-fit" : "off")}.";

        return new AutoRuntimeProfile
        {
            Runtime = runtime,
            ModelSizeMb = modelSizeMb,
            Reason = reason,
            Warnings = warnings
        };
    }

    public static string ToLogLine(HardwareInfo hw, BackendChoice backend, AutoRuntimeProfile profile)
    {
        var r = profile.Runtime;
        var warnings = profile.Warnings.Count == 0 ? "-" : string.Join(" | ", profile.Warnings);
        return "AUTO-RUNTIME " +
               $"policy=quality-first cpu=\"{hw.CpuName}\" logical={hw.LogicalProcessors} avx={hw.Avx} avx2={hw.Avx2} " +
               $"ram_mb={hw.TotalRamMb} ram_available_mb={hw.AvailableRamMb} gpu=\"{hw.GpuName}\" gpu_vendor=\"{hw.GpuVendor}\" vram_mb={hw.GpuVramMb} cc=\"{hw.ComputeCapability}\" " +
               $"backend=\"{backend.Name}\" model_mb={profile.ModelSizeMb} context={r.ContextSize} " +
               $"threads={r.Threads} threads_batch={r.ThreadsBatch} batch={r.BatchSize} ubatch={r.UBatchSize} " +
               $"gpu_layers={r.GpuLayers} flash={r.FlashAttention} cache_k={r.CacheTypeK} cache_v={r.CacheTypeV} " +
               $"load_mode={r.LoadMode} priority={r.Priority} warnings=\"{warnings}\"";
    }
}
