using System.Text.RegularExpressions;
using UltimateLocalAI.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace UltimateLocalAI.Services;

public sealed class RagDocumentExtractor
{
    private readonly FileTextExtractor _fallback;

    public RagDocumentExtractor(FileTextExtractor fallback)
    {
        _fallback = fallback;
    }

    public async Task<List<RagSourceSegment>> ExtractAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Файл не найден.", path);

        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return await Task.Run(() => ExtractPdf(path, ct), ct);

        var attachment = await _fallback.ExtractAsync(path);
        ct.ThrowIfCancellationRequested();
        return SplitTextIntoSegments(attachment.ExtractedText);
    }

    private static List<RagSourceSegment> ExtractPdf(string path, CancellationToken ct)
    {
        var result = new List<RagSourceSegment>();
        using var document = PdfDocument.Open(path);

        foreach (var page in document.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var text = Normalize(ContentOrderTextExtractor.GetText(page));
            if (string.IsNullOrWhiteSpace(text))
                continue;

            result.Add(new RagSourceSegment
            {
                PageNumber = page.Number,
                Section = "",
                Content = text
            });
        }

        if (result.Count == 0)
        {
            result.Add(new RagSourceSegment
            {
                Content = "[PDF не содержит надёжно извлекаемого текстового слоя. Для него потребуется OCR-режим Stage 7G-4/7G-5.]"
            });
        }

        return result;
    }

    private static List<RagSourceSegment> SplitTextIntoSegments(string text)
    {
        var normalized = Normalize(text);
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
                Content = paragraph
            });
        }

        if (result.Count == 0)
            result.Add(new RagSourceSegment { Content = normalized });

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

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var value = text.Replace("\r\n", "\n").Replace('\r', '\n');
        value = Regex.Replace(value, @"[ \t]+", " ");
        value = Regex.Replace(value, @"\n{3,}", "\n\n");
        return value.Trim();
    }
}
