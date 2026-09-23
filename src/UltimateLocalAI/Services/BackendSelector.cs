using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public sealed class BackendSelector
{
    public BackendChoice Select(HardwareInfo hw, AppConfig config)
    {
        var mode = config.BackendMode?.Trim() ?? "Auto";
        if (!mode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            return FromForcedMode(mode, hw, config);

        if (hw.NvidiaDetected && IsPascal(hw.ComputeCapability, hw.GpuName))
        {
            var pascal = Candidate(config, "cuda-pascal-avx", "CUDA Pascal + CPU AVX", true,
                "Обнаружена NVIDIA Pascal. Используется специальный sm_61 backend, совместимый с Xeon AVX.");
            if (File.Exists(pascal.ExecutablePath)) return pascal;
        }

        if (hw.NvidiaDetected && IsBlackwell(hw.ComputeCapability, hw.GpuName) && hw.Avx2)
        {
            var blackwell = Candidate(config, "cuda-blackwell-avx2", "CUDA Blackwell + CPU AVX2", true,
                "Обнаружена NVIDIA Blackwell. Используется отдельный sm_120 backend.");
            if (File.Exists(blackwell.ExecutablePath)) return blackwell;
            // Не пытаемся запускать backend 75/86/89 на Blackwell, если отдельный backend не собран.
        }
        else if (hw.NvidiaDetected && hw.Avx2)
        {
            var cuda = Candidate(config, "cuda-modern-avx2", "CUDA RTX 20/30/40 + CPU AVX2", true,
                "Обнаружена NVIDIA и AVX2. Выбран CUDA backend для Turing/Ampere/Ada/Hopper.");
            if (File.Exists(cuda.ExecutablePath)) return cuda;
        }

        if (hw.Avx2)
            return Candidate(config, "cpu-avx2", "CPU AVX2", false, "GPU backend недоступен или не собран; используется AVX2 CPU backend.");
        if (hw.Avx)
            return Candidate(config, "cpu-avx", "CPU AVX", false, "Процессор поддерживает AVX, но не AVX2; используется совместимый AVX backend.");

        return Candidate(config, "cpu-avx", "CPU AVX", false, "Совместимый backend не определён автоматически. Требуется процессор как минимум с AVX.");
    }

    public BackendChoice CpuFallback(HardwareInfo hw, AppConfig config) => hw.Avx2
        ? Candidate(config, "cpu-avx2", "CPU AVX2", false, "Безопасный fallback на CPU AVX2.")
        : Candidate(config, "cpu-avx", "CPU AVX", false, "Безопасный fallback на CPU AVX.");

    private static BackendChoice FromForcedMode(string mode, HardwareInfo hw, AppConfig config) => mode switch
    {
        "CPU AVX" => Candidate(config, "cpu-avx", "CPU AVX", false, "Режим выбран вручную."),
        "CPU AVX2" => Candidate(config, "cpu-avx2", "CPU AVX2", false, "Режим выбран вручную."),
        "CUDA Pascal" => Candidate(config, "cuda-pascal-avx", "CUDA Pascal + CPU AVX", true, "Режим выбран вручную."),
        "CUDA Modern" => Candidate(config, "cuda-modern-avx2", "CUDA RTX 20/30/40 + CPU AVX2", true, "Режим выбран вручную."),
        "CUDA Blackwell" => Candidate(config, "cuda-blackwell-avx2", "CUDA Blackwell + CPU AVX2", true, "Режим выбран вручную."),
        _ => hw.Avx2 ? Candidate(config, "cpu-avx2", "CPU AVX2", false, "Неизвестный режим; fallback AVX2.") : Candidate(config, "cpu-avx", "CPU AVX", false, "Неизвестный режим; fallback AVX.")
    };

    private static BackendChoice Candidate(AppConfig config, string folder, string name, bool cuda, string reason) => new()
    {
        Name = name,
        ExecutablePath = Path.Combine(AppPaths.ResolveBackendsDir(config.RuntimeChannel, config.CustomRuntimePath), folder, "llama-server.exe"),
        UsesCuda = cuda,
        Reason = reason
    };

    private static bool IsPascal(string cc, string gpuName)
    {
        if (!string.IsNullOrWhiteSpace(cc) && (cc.StartsWith("6.", StringComparison.OrdinalIgnoreCase) || cc == "61")) return true;
        var name = gpuName ?? "";
        return name.Contains("GTX 1050", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("GTX 1060", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("GTX 1070", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("GTX 1080", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBlackwell(string cc, string gpuName)
    {
        if (!string.IsNullOrWhiteSpace(cc) && (cc.StartsWith("12.", StringComparison.OrdinalIgnoreCase) || cc.StartsWith("120", StringComparison.OrdinalIgnoreCase))) return true;
        var name = gpuName ?? "";
        return name.Contains("RTX 50", StringComparison.OrdinalIgnoreCase);
    }
}
