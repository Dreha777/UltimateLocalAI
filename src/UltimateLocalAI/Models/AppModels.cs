using System.Collections.ObjectModel;

namespace UltimateLocalAI.Models;

public sealed class AppConfig
{
    public string ModelPath { get; set; } = "";
    public string BackendMode { get; set; } = "Auto";
    public string RuntimeChannel { get; set; } = "Stable";
    public string CustomRuntimePath { get; set; } = "";
    public int Port { get; set; } = 8089;
    public bool AutoStartLastModel { get; set; } = false;
    public bool AutoFallbackToCpu { get; set; } = true;
    public bool UseKnowledgeBase { get; set; } = false;
    public RuntimeSettings Runtime { get; set; } = new();
    public GenerationSettings Generation { get; set; } = new();
}

public sealed class RuntimeSettings
{
    public int ContextSize { get; set; } = 4096;
    public int Threads { get; set; } = Math.Max(1, Environment.ProcessorCount - 1);
    public int ThreadsBatch { get; set; } = Math.Max(1, Environment.ProcessorCount);
    public string GpuLayers { get; set; } = "auto";
    public int BatchSize { get; set; } = 512;
    public int UBatchSize { get; set; } = 256;
    public string FlashAttention { get; set; } = "auto";
    public string CacheTypeK { get; set; } = "q8_0";
    public string CacheTypeV { get; set; } = "q8_0";
    public string LoadMode { get; set; } = "auto";
    public string ReasoningMode { get; set; } = "auto";
    public string ReasoningEffort { get; set; } = "default";
    public int Priority { get; set; } = 1;
}

public sealed class GenerationSettings
{
    public double Temperature { get; set; } = 0.7;
    public double TopP { get; set; } = 0.95;
    public int TopK { get; set; } = 40;
    public double MinP { get; set; } = 0.05;
    public double RepeatPenalty { get; set; } = 1.05;
    public int MaxTokens { get; set; } = 1024;
    public int Seed { get; set; } = -1;
    public string SystemPrompt { get; set; } = "Ты локальный ИИ-ассистент. Отвечай точно, ясно и по существу.";
}

public sealed class ChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Новый чат";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public string ModelPath { get; set; } = "";
    public override string ToString() => Title;
}

public sealed class ChatMessage
{
    public long Id { get; set; }
    public string ChatId { get; set; } = "";
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string ContextText { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<AttachmentInfo> Attachments { get; set; } = [];
}

public sealed class UiMessage
{
    public string Role { get; set; } = "assistant";
    public string Content { get; set; } = "";
    public string AttachmentSummary { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public sealed class AttachmentInfo
{
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long SizeBytes { get; set; }
    public string ExtractedText { get; set; } = "";
}

public sealed class HardwareInfo
{
    public string CpuName { get; set; } = "Не определён";
    public int LogicalProcessors { get; set; } = Environment.ProcessorCount;
    public long TotalRamMb { get; set; }
    public bool Avx { get; set; }
    public bool Avx2 { get; set; }
    public bool Sse42 { get; set; }
    public string GpuName { get; set; } = "Не обнаружена";
    public int GpuVramMb { get; set; }
    public string ComputeCapability { get; set; } = "";
    public bool NvidiaDetected { get; set; }

    public string ShortSummary => $"{CpuName}\nRAM: {TotalRamMb / 1024.0:0.#} ГБ · AVX {(Avx ? "✓" : "✗")} · AVX2 {(Avx2 ? "✓" : "✗")}\nGPU: {GpuName}";
}

public sealed class BackendChoice
{
    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public bool UsesCuda { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class BenchmarkResult
{
    public double Seconds { get; set; }
    public int ApproxTokens { get; set; }
    public double TokensPerSecond => Seconds > 0 ? ApproxTokens / Seconds : 0;
}

public sealed class KnowledgeDocument
{
    public long Id { get; set; }
    public string SourcePath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTime IndexedAt { get; set; }
}

public sealed class KnowledgeHit
{
    public string SourcePath { get; set; } = "";
    public string Content { get; set; } = "";
    public double Score { get; set; }
}
