using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

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
    public string WorkspaceBackground { get; set; } = "Светло-серый";
    public List<string> ModelFolders { get; set; } = [];
    public string EmbeddingModelPath { get; set; } = "";
    public int EmbeddingPort { get; set; } = 8090;
    public string EmbeddingDocumentPrefix { get; set; } = "";
    public string EmbeddingQueryPrefix { get; set; } = "";
    public string EmbeddingPooling { get; set; } = "Auto";
    public string RerankerModelPath { get; set; } = "";
    public int RerankerPort { get; set; } = 8091;
    public bool UseReranker { get; set; } = true;
    public int RagCandidateTopK { get; set; } = 24;
    public int RagFinalTopK { get; set; } = 6;
    public bool OcrEnabled { get; set; } = true;
    public int OcrDpi { get; set; } = 300;
    public double OcrMinConfidence { get; set; } = 0.45;
    public string OcrLanguages { get; set; } = "rus+eng";
    public bool OcrRestorationEnabled { get; set; } = true;
    public double OcrMaxDeskewDegrees { get; set; } = 12.0;
    public bool DocumentVisionEnabled { get; set; } = true;
    public string DocumentVisionModelPath { get; set; } = "";
    public string DocumentVisionMmprojPath { get; set; } = "";
    public int DocumentVisionPort { get; set; } = 8092;
    public int DocumentVisionDpi { get; set; } = 220;
    public bool DocumentVisionUseGpu { get; set; } = false;
    public bool DocumentVisionAnalyzeAllPages { get; set; } = true;
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
    public string AutoProfile { get; set; } = "Качество";
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
    public List<RagSourceCitation> Sources { get; set; } = [];
}

public sealed class UiMessage : INotifyPropertyChanged
{
    private string _content = "";
    private string _attachmentSummary = "";

    public string Role { get; set; } = "assistant";
    public ObservableCollection<RagSourceCitation> Sources { get; } = [];

    public string Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            _content = value;
            OnPropertyChanged();
        }
    }

    public string AttachmentSummary
    {
        get => _attachmentSummary;
        set
        {
            if (_attachmentSummary == value) return;
            _attachmentSummary = value;
            OnPropertyChanged();
        }
    }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class RagSourceCitation
{
    public int Number { get; set; }
    public string SourcePath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Section { get; set; } = "";
    public int? PageFrom { get; set; }
    public int? PageTo { get; set; }
    public string Snippet { get; set; } = "";
    public double SemanticScore { get; set; }
    public double? RerankScore { get; set; }
    public string RetrievalMethod { get; set; } = "";
    public string ExtractionMode { get; set; } = "";
    public double OcrConfidence { get; set; }

    public string PageLabel => PageFrom.HasValue
        ? PageTo.HasValue && PageTo != PageFrom ? $"стр. {PageFrom}–{PageTo}" : $"стр. {PageFrom}"
        : "";

    public string LocationLabel
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(PageLabel)) parts.Add(PageLabel);
            if (!string.IsNullOrWhiteSpace(Section)) parts.Add(Section);
            return string.Join(" · ", parts);
        }
    }
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
    public long AvailableRamMb { get; set; }
    public bool Avx { get; set; }
    public bool Avx2 { get; set; }
    public bool Sse42 { get; set; }
    public string GpuName { get; set; } = "Не обнаружена";
    public int GpuVramMb { get; set; }
    public string ComputeCapability { get; set; } = "";
    public bool GpuDetected { get; set; }
    public string GpuVendor { get; set; } = "";
    public bool NvidiaDetected { get; set; }

    public string ShortSummary => $"{CpuName}\nRAM: {TotalRamMb / 1024.0:0.#} ГБ · AVX {(Avx ? "✓" : "✗")} · AVX2 {(Avx2 ? "✓" : "✗")}\nGPU: {GpuName}";
}

public sealed class ModelServerProperties
{
    public string ModelAlias { get; set; } = "";
    public string ModelFtype { get; set; } = "";
    public int ContextSize { get; set; }
    public string ChatTemplate { get; set; } = "";
    public List<string> ChatTemplateCapabilities { get; set; } = [];

    public bool HasChatTemplate => !string.IsNullOrWhiteSpace(ChatTemplate);

    public bool SupportsReasoning =>
        ChatTemplateCapabilities.Any(x => x.Contains("reason", StringComparison.OrdinalIgnoreCase) ||
                                          x.Contains("think", StringComparison.OrdinalIgnoreCase)) ||
        ChatTemplate.Contains("reasoning", StringComparison.OrdinalIgnoreCase) ||
        ChatTemplate.Contains("think", StringComparison.OrdinalIgnoreCase);
}

public sealed class GgufModelMetadata
{
    public bool IsValid { get; set; }
    public uint Version { get; set; }
    public string Name { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string SizeLabel { get; set; } = "";
    public int? FileTypeCode { get; set; }
    public string Quantization { get; set; } = "";
    public long? NativeContextSize { get; set; }

    public double? RecommendedTemperature { get; set; }
    public double? RecommendedTopP { get; set; }
    public int? RecommendedTopK { get; set; }
    public double? RecommendedMinP { get; set; }
    public double? RecommendedRepeatPenalty { get; set; }

    public bool HasRecommendedSampling =>
        RecommendedTemperature.HasValue || RecommendedTopP.HasValue ||
        RecommendedTopK.HasValue || RecommendedMinP.HasValue ||
        RecommendedRepeatPenalty.HasValue;

    public bool IsAggressivelyQuantized =>
        Quantization.StartsWith("Q1", StringComparison.OrdinalIgnoreCase) ||
        Quantization.StartsWith("Q2", StringComparison.OrdinalIgnoreCase) ||
        Quantization.StartsWith("Q3", StringComparison.OrdinalIgnoreCase) ||
        Quantization.StartsWith("IQ1", StringComparison.OrdinalIgnoreCase) ||
        Quantization.StartsWith("IQ2", StringComparison.OrdinalIgnoreCase) ||
        Quantization.StartsWith("IQ3", StringComparison.OrdinalIgnoreCase) ||
        Quantization.StartsWith("TQ1", StringComparison.OrdinalIgnoreCase) ||
        Quantization.StartsWith("TQ2", StringComparison.OrdinalIgnoreCase);
}

public sealed class ModelCatalogItem
{
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long SizeBytes { get; set; }
    public double SizeGb => SizeBytes / 1024d / 1024d / 1024d;
    public string SizeText => $"{SizeGb:0.00} ГБ";
    public string Architecture { get; set; } = "";
    public string SizeLabel { get; set; } = "";
    public string Quantization { get; set; } = "";
    public long? NativeContextSize { get; set; }
    public string NativeContextText => NativeContextSize is > 0 ? NativeContextSize.Value.ToString("N0") : "—";
    public string Quality { get; set; } = "Не определено";
    public string HardwareFit { get; set; } = "Не оценено";
    public string Advice { get; set; } = "";
    public int RecommendationRank { get; set; }
    public bool IsCurrent { get; set; }
}

public sealed class BackendChoice
{
    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public bool UsesCuda { get; set; }
    public bool UsesGpu { get; set; }
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
    public int PageCount { get; set; }
    public int ChunkCount { get; set; }
    public string IndexStatus { get; set; } = "";
    public string EmbeddingModelName { get; set; } = "";
    public int OcrPageCount { get; set; }
    public double OcrAverageConfidence { get; set; }
    public string ImportMode { get; set; } = "";
    public int VisionPageCount { get; set; }
    public string ImportSummary
    {
        get
        {
            var baseText = OcrPageCount > 0
                ? $"{ImportMode} · OCR {OcrPageCount} стр. · {OcrAverageConfidence:P0}"
                : string.IsNullOrWhiteSpace(ImportMode) ? "Text" : ImportMode;
            return VisionPageCount > 0 ? $"{baseText} · Vision {VisionPageCount} стр." : baseText;
        }
    }
}

public sealed class KnowledgeHit
{
    public string SourcePath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Content { get; set; } = "";
    public string Section { get; set; } = "";
    public int? PageFrom { get; set; }
    public int? PageTo { get; set; }
    public double Score { get; set; }
    public double SemanticScore { get; set; }
    public double? RerankScore { get; set; }
    public string RetrievalMethod { get; set; } = "";
    public string ExtractionMode { get; set; } = "";
    public double OcrConfidence { get; set; }
    public string PageLabel => PageFrom.HasValue
        ? PageTo.HasValue && PageTo != PageFrom ? $"стр. {PageFrom}–{PageTo}" : $"стр. {PageFrom}"
        : "";
}


public sealed class RagSourceSegment
{
    public int? PageNumber { get; set; }
    public string Section { get; set; } = "";
    public string Content { get; set; } = "";
    public string ExtractionMode { get; set; } = "Text";
    public double OcrConfidence { get; set; }
}

public sealed class RagChunkRecord
{
    public long ChunkId { get; set; }
    public long DocumentId { get; set; }
    public int ChunkIndex { get; set; }
    public int? PageFrom { get; set; }
    public int? PageTo { get; set; }
    public string Section { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string Content { get; set; } = "";
    public string ExtractionMode { get; set; } = "Text";
    public double OcrConfidence { get; set; }
    public int VectorIndex { get; set; }
}

public sealed class RagIndexedDocument
{
    public long Id { get; set; }
    public string SourcePath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long SourceSizeBytes { get; set; }
    public DateTime SourceLastWriteUtc { get; set; }
    public string SourceSha256 { get; set; } = "";
    public string ExtractionFingerprint { get; set; } = "";
    public int PageCount { get; set; }
    public int ChunkCount { get; set; }
    public string ChunkFile { get; set; } = "";
    public string VectorFile { get; set; } = "";
    public DateTime IndexedAt { get; set; }
    public int OcrPageCount { get; set; }
    public double OcrAverageConfidence { get; set; }
    public string ImportMode { get; set; } = "";
    public int VisionPageCount { get; set; }
    public string VisionModelName { get; set; } = "";
}

public sealed class RagIndexManifest
{
    public int Version { get; set; } = 1;
    public long NextDocumentId { get; set; } = 1;
    public long NextChunkId { get; set; } = 1;
    public string EmbeddingModelPath { get; set; } = "";
    public string EmbeddingModelFingerprint { get; set; } = "";
    public string EmbeddingModelName { get; set; } = "";
    public string EmbeddingDocumentPrefix { get; set; } = "";
    public string EmbeddingPooling { get; set; } = "Auto";
    public int VectorDimensions { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<RagIndexedDocument> Documents { get; set; } = [];
}

public sealed class RagIndexProgress
{
    public string Phase { get; set; } = "";
    public string FileName { get; set; } = "";
    public int Completed { get; set; }
    public int Total { get; set; }
    public string Message { get; set; } = "";
}


public sealed class RagRetrievalDiagnostics
{
    public string EmbeddingModelName { get; set; } = "";
    public string RerankerModelName { get; set; } = "";
    public int CandidateCount { get; set; }
    public int ReturnedCount { get; set; }
    public bool UsedReranker { get; set; }
    public string Status { get; set; } = "";
}

public sealed class RagRetrievalResult
{
    public List<KnowledgeHit> Hits { get; set; } = [];
    public RagRetrievalDiagnostics Diagnostics { get; set; } = new();
}


public sealed class PdfPageScanInfo
{
    public int PageNumber { get; set; }
    public int TextCharCount { get; set; }
    public double UsefulTextRatio { get; set; }
    public string Mode { get; set; } = "Text";
    public string Reason { get; set; } = "";
    public string ExtractedText { get; set; } = "";
    public double OcrConfidence { get; set; }
    public double RawOcrConfidence { get; set; }
    public bool UsedOcr { get; set; }
    public bool UsedRestoration { get; set; }
    public string RestorationVariant { get; set; } = "Original";
    public double DeskewDegrees { get; set; }
    public double RestorationScore { get; set; }
    public string RestorationLabel => !UsedOcr
        ? "—"
        : UsedRestoration
            ? $"{RestorationVariant} · {DeskewDegrees:+0.0;-0.0;0.0}°"
            : "Original";
    public bool UsedVision { get; set; }
    public string VisionStatus { get; set; } = "";
    public string VisionContent { get; set; } = "";
}

public sealed class PdfScanReport
{
    public string SourcePath { get; set; } = "";
    public int PageCount { get; set; }
    public int TextPageCount { get; set; }
    public int OcrCandidatePageCount { get; set; }
    public int OcrPageCount { get; set; }
    public double OcrAverageConfidence { get; set; }
    public int VisionPageCount { get; set; }
    public string VisionModelName { get; set; } = "";
    public string DocumentMode { get; set; } = "Text PDF";
    public List<PdfPageScanInfo> Pages { get; set; } = [];
}

public sealed class RagExtractionResult
{
    public List<RagSourceSegment> Segments { get; set; } = [];
    public PdfScanReport? PdfReport { get; set; }
}


public sealed class DocumentVisionAnalysis
{
    public int PageNumber { get; set; }
    public string Content { get; set; } = "";
    public bool HasVisualContent { get; set; }
    public string ModelName { get; set; } = "";
    public string Status { get; set; } = "";
}
