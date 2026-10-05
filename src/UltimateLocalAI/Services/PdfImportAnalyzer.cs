using System.Text.RegularExpressions;
using UltimateLocalAI.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace UltimateLocalAI.Services;

public sealed class PdfImportAnalyzer
{
    public Task<PdfScanReport> AnalyzeAsync(string path, CancellationToken ct = default) =>
        Task.Run(() => Analyze(path, ct), ct);

    public PdfScanReport Analyze(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("PDF не найден.", path);

        var report = new PdfScanReport { SourcePath = Path.GetFullPath(path) };
        using var document = PdfDocument.Open(path);
        report.PageCount = document.NumberOfPages;

        foreach (var page in document.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var raw = ContentOrderTextExtractor.GetText(page);
            var text = Normalize(raw);
            var info = EvaluatePage(page.Number, text);
            report.Pages.Add(info);

            if (info.Mode == "Text")
                report.TextPageCount++;
            else
                report.OcrCandidatePageCount++;
        }

        report.DocumentMode = report.OcrCandidatePageCount switch
        {
            0 => "Text PDF",
            _ when report.TextPageCount == 0 => "Scan PDF",
            _ => "Mixed PDF"
        };

        return report;
    }

    public static PdfPageScanInfo EvaluatePage(int pageNumber, string? text)
    {
        var normalized = Normalize(text);
        var nonSpace = normalized.Count(ch => !char.IsWhiteSpace(ch));
        var useful = normalized.Count(IsUsefulTextChar);
        var letters = normalized.Count(char.IsLetter);
        var replacement = normalized.Count(ch => ch == '�');
        var control = normalized.Count(ch => char.IsControl(ch) && ch is not '
' and not '	');
        var usefulRatio = nonSpace == 0 ? 0 : useful / (double)nonSpace;

        string mode;
        string reason;

        if (nonSpace < 30)
        {
            mode = "OCR";
            reason = "На странице почти нет извлекаемого текстового слоя.";
        }
        else if (letters < 15)
        {
            mode = "OCR";
            reason = "Текстовый слой слишком короткий или почти не содержит букв.";
        }
        else if (usefulRatio < 0.72 || replacement > Math.Max(2, nonSpace / 100) || control > 0)
        {
            mode = "OCR";
            reason = "Текстовый слой выглядит повреждённым или содержит слишком много нечитабельных символов.";
        }
        else
        {
            mode = "Text";
            reason = "Текстовый слой выглядит пригодным для прямой индексации.";
        }

        return new PdfPageScanInfo
        {
            PageNumber = pageNumber,
            TextCharCount = normalized.Length,
            UsefulTextRatio = usefulRatio,
            Mode = mode,
            Reason = reason,
            ExtractedText = normalized
        };
    }

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var value = text.Replace("
", "
").Replace('', '
');
        value = Regex.Replace(value, @"[ 	]+", " ");
        value = Regex.Replace(value, @"
{3,}", "

");
        return value.Trim();
    }

    private static bool IsUsefulTextChar(char ch)
    {
        if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
            return true;

        return ch is '.' or ',' or ';' or ':' or '!' or '?' or '-' or '—' or '–' or
               '(' or ')' or '[' or ']' or '{' or '}' or '/' or '\\' or '+' or '=' or
               '%' or '№' or '§' or '°' or 'µ' or 'μ' or 'λ' or 'Δ' or 'δ' or 'π' or
               '<' or '>' or ''' or '"' or '«' or '»';
    }
}
