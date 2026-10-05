using System.Text.RegularExpressions;
using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public sealed class RagDocumentExtractor
{
    private readonly FileTextExtractor _fallback;
    private readonly PdfImportAnalyzer _pdfAnalyzer = new();
    private readonly PdfPageRenderer _pdfRenderer = new();
    private readonly ScanRestorationService _scanRestoration = new();

    public RagDocumentExtractor(FileTextExtractor fallback)
    {
        _fallback = fallback;
    }

    public async Task<RagExtractionResult> ExtractAsync(
        string path,
        AppConfig config,
        IProgress<RagIndexProgress>? progress = null,
        DocumentVisionSession? vision = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Файл не найден.", path);

        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return await ExtractPdfAsync(path, config, progress, vision, ct);

        var attachment = await _fallback.ExtractAsync(path);
        ct.ThrowIfCancellationRequested();
        return new RagExtractionResult
        {
            Segments = SplitTextIntoSegments(attachment.ExtractedText)
        };
    }

    private async Task<RagExtractionResult> ExtractPdfAsync(
        string path,
        AppConfig config,
        IProgress<RagIndexProgress>? progress,
        DocumentVisionSession? vision,
        CancellationToken ct)
    {
        progress?.Report(new RagIndexProgress
        {
            Phase = "pdf-analyze",
            FileName = Path.GetFileName(path),
            Message = "Анализ текстового слоя PDF…"
        });

        var report = await _pdfAnalyzer.AnalyzeAsync(path, ct);
        var result = new RagExtractionResult { PdfReport = report };

        if (report.OcrCandidatePageCount == 0)
        {
            foreach (var page in report.Pages)
            {
                if (string.IsNullOrWhiteSpace(page.ExtractedText)) continue;
                result.Segments.Add(new RagSourceSegment
                {
                    PageNumber = page.PageNumber,
                    Content = page.ExtractedText,
                    ExtractionMode = "Text"
                });
            }

            await AppendVisionSegmentsAsync(path, config, report, result, progress, vision, ct);
            SortPageSegments(result);
            return result;
        }

        if (!config.OcrEnabled)
        {
            foreach (var page in report.Pages)
            {
                if (string.IsNullOrWhiteSpace(page.ExtractedText)) continue;
                result.Segments.Add(new RagSourceSegment
                {
                    PageNumber = page.PageNumber,
                    Content = page.ExtractedText,
                    ExtractionMode = page.Mode == "Text" ? "Text" : "WeakText"
                });
            }

            await AppendVisionSegmentsAsync(path, config, report, result, progress, vision, ct);
            SortPageSegments(result);
            return result;
        }

        if (!LocalOcrSession.IsReady(config.OcrLanguages, out var ocrReason))
            throw new InvalidOperationException(
                $"PDF содержит {report.OcrCandidatePageCount} страниц, которым нужен OCR, но OCR-движок не готов: {ocrReason}");

        var tempDir = Path.Combine(AppPaths.TempDir, "ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var confidences = new List<double>();
        try
        {
            using var ocr = new LocalOcrSession(config.OcrLanguages);

            foreach (var page in report.Pages)
            {
                ct.ThrowIfCancellationRequested();

                if (page.Mode == "Text")
                {
                    result.Segments.Add(new RagSourceSegment
                    {
                        PageNumber = page.PageNumber,
                        Content = page.ExtractedText,
                        ExtractionMode = "Text"
                    });
                    continue;
                }

                progress?.Report(new RagIndexProgress
                {
                    Phase = "ocr",
                    FileName = Path.GetFileName(path),
                    Completed = page.PageNumber,
                    Total = report.PageCount,
                    Message = $"OCR страницы {page.PageNumber}/{report.PageCount}…"
                });

                var image = _pdfRenderer.RenderPageToPng(
                    path,
                    page.PageNumber,
                    Math.Clamp(config.OcrDpi, 150, 450),
                    tempDir);

                OcrRecognitionResult recognized;
                if (config.OcrRestorationEnabled)
                {
                    var candidateDir = Path.Combine(tempDir, $"page-{page.PageNumber:D4}");
                    var candidates = _scanRestoration.BuildCandidates(
                        image,
                        candidateDir,
                        Math.Clamp(config.OcrMaxDeskewDegrees, 0, 20));

                    var selection = await Task.Run(
                        () => OcrCandidateSelector.SelectBest(ocr, candidates, ct),
                        ct);

                    recognized = selection.Recognition;
                    page.RawOcrConfidence = selection.RawRecognition.Confidence;
                    page.UsedRestoration = selection.Candidate.Processed;
                    page.RestorationVariant = selection.Candidate.Name;
                    page.DeskewDegrees = selection.Candidate.DeskewDegrees;
                    page.RestorationScore = selection.Score;
                }
                else
                {
                    recognized = await Task.Run(() => ocr.Recognize(image), ct);
                    page.RawOcrConfidence = recognized.Confidence;
                    page.UsedRestoration = false;
                    page.RestorationVariant = "Original";
                    page.DeskewDegrees = 0;
                }

                page.UsedOcr = true;
                page.OcrConfidence = recognized.Confidence;
                report.OcrPageCount++;
                confidences.Add(recognized.Confidence);

                var ocrText = PdfImportAnalyzer.Normalize(recognized.Text);
                var useOcr = ocrText.Count(char.IsLetterOrDigit) >= 20;

                if (!useOcr && !string.IsNullOrWhiteSpace(page.ExtractedText))
                {
                    result.Segments.Add(new RagSourceSegment
                    {
                        PageNumber = page.PageNumber,
                        Content = page.ExtractedText,
                        ExtractionMode = "WeakText",
                        OcrConfidence = recognized.Confidence
                    });
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(ocrText))
                {
                    result.Segments.Add(new RagSourceSegment
                    {
                        PageNumber = page.PageNumber,
                        Content = ocrText,
                        ExtractionMode = recognized.Confidence >= config.OcrMinConfidence
                            ? (page.UsedRestoration ? "OCR-Restored" : "OCR")
                            : (page.UsedRestoration ? "OCR-Restored-LowConfidence" : "OCR-LowConfidence"),
                        OcrConfidence = recognized.Confidence
                    });
                }
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }

        report.OcrAverageConfidence = confidences.Count == 0 ? 0 : confidences.Average();
        report.DocumentMode = report.OcrPageCount switch
        {
            0 => report.DocumentMode,
            _ when report.TextPageCount == 0 => "OCR scan",
            _ => "Mixed text + OCR"
        };

        await AppendVisionSegmentsAsync(path, config, report, result, progress, vision, ct);
        SortPageSegments(result);
        return result;
    }

    private async Task AppendVisionSegmentsAsync(
        string path,
        AppConfig config,
        PdfScanReport report,
        RagExtractionResult result,
        IProgress<RagIndexProgress>? progress,
        DocumentVisionSession? vision,
        CancellationToken ct)
    {
        if (!config.DocumentVisionEnabled || vision is null || !vision.IsReady)
            return;

        var tempDir = Path.Combine(AppPaths.TempDir, "vision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        report.VisionModelName = vision.ModelName;

        try
        {
            foreach (var page in report.Pages)
            {
                ct.ThrowIfCancellationRequested();

                var textContext = result.Segments
                    .Where(x => x.PageNumber == page.PageNumber && !string.Equals(x.ExtractionMode, "Vision", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.Content)
                    .FirstOrDefault() ?? page.ExtractedText;

                if (!config.DocumentVisionAnalyzeAllPages &&
                    !ShouldAnalyzeVisualPage(textContext, page))
                    continue;

                progress?.Report(new RagIndexProgress
                {
                    Phase = "vision",
                    FileName = Path.GetFileName(path),
                    Completed = page.PageNumber,
                    Total = report.PageCount,
                    Message = $"Vision-анализ страницы {page.PageNumber}/{report.PageCount}…"
                });

                try
                {
                    var image = _pdfRenderer.RenderPageForVision(
                        path,
                        page.PageNumber,
                        Math.Clamp(config.DocumentVisionDpi, 140, 320),
                        tempDir);

                    var analysis = await vision.AnalyzePageAsync(
                        image,
                        page.PageNumber,
                        textContext,
                        ct);

                    page.VisionStatus = analysis.Status;
                    page.VisionContent = analysis.Content;

                    if (!analysis.HasVisualContent)
                        continue;

                    page.UsedVision = true;
                    report.VisionPageCount++;

                    result.Segments.Add(new RagSourceSegment
                    {
                        PageNumber = page.PageNumber,
                        Section = "Визуальный анализ страницы",
                        Content = analysis.Content,
                        ExtractionMode = "Vision"
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    page.VisionStatus = "Ошибка Vision: " + ex.Message;
                    LogService.Warn($"Document Vision page {page.PageNumber}: {ex.Message}");
                }
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static bool ShouldAnalyzeVisualPage(string? text, PdfPageScanInfo page)
    {
        if (page.Mode != "Text")
            return true;

        var value = text ?? "";
        if (value.Length < 300)
            return true;

        return value.Contains("рис.", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("рисунок", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("figure", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("табл.", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("таблица", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("table", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("график", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("схем", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("=", StringComparison.Ordinal);
    }

    private static void SortPageSegments(RagExtractionResult result)
    {
        result.Segments = result.Segments
            .OrderBy(x => x.PageNumber ?? int.MaxValue)
            .ThenBy(x => string.Equals(x.ExtractionMode, "Vision", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ToList();
    }

    private static List<RagSourceSegment> SplitTextIntoSegments(string text)
    {
        var normalized = PdfImportAnalyzer.Normalize(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return [];

        var paragraphs = Regex.Split(normalized, @"\n\s*\n")
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();

        var result = new List<RagSourceSegment>();
        var currentSection = "";

        foreach (var paragraph in paragraphs)
        {
            if (LooksLikeHeading(paragraph))
            {
                currentSection = paragraph.Trim();
                continue;
            }

            result.Add(new RagSourceSegment
            {
                PageNumber = null,
                Section = currentSection,
                Content = paragraph,
                ExtractionMode = "Text"
            });
        }

        if (result.Count == 0)
            result.Add(new RagSourceSegment { Content = normalized, ExtractionMode = "Text" });

        return result;
    }

    private static bool LooksLikeHeading(string text)
    {
        var oneLine = !text.Contains('\n');
        var trimmed = text.Trim();
        if (!oneLine || trimmed.Length is < 3 or > 120)
            return false;

        if (Regex.IsMatch(trimmed, @"^(глава|раздел|chapter|section)\s+[\p{L}\p{N}IVXLC.-]+", RegexOptions.IgnoreCase))
            return true;

        var letters = trimmed.Count(char.IsLetter);
        if (letters < 3) return false;
        var upper = trimmed.Count(char.IsUpper);
        return upper >= letters * 0.8;
    }
}
