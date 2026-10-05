using System.Net.Http.Json;
using System.Text.Json;
using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public sealed class DocumentVisionApiClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(12) };

    public async Task<DocumentVisionAnalysis> AnalyzePageAsync(
        int port,
        string imagePath,
        int pageNumber,
        string? extractedText,
        string modelName,
        CancellationToken ct = default)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("Изображение страницы для Vision не найдено.", imagePath);

        var relative = Path.GetRelativePath(AppPaths.TempDir, Path.GetFullPath(imagePath));
        if (relative.StartsWith("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Vision image находится вне разрешённой временной папки.");

        var localMediaUrl = "file://" + string.Join("/",
            relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Where(x => x.Length > 0)
                .Select(Uri.EscapeDataString));

        var ocrContext = string.IsNullOrWhiteSpace(extractedText)
            ? "No reliable OCR/text-layer context is available."
            : "Text-layer/OCR context for cross-checking only (do not simply repeat it):\n" +
              extractedText[..Math.Min(extractedText.Length, 4500)];

        var prompt =
            "Analyze this page from a technical/scientific book as a document-understanding system. " +
            "Do NOT rewrite ordinary prose that is already recoverable by OCR. Extract and explain the visual/structured information that normal OCR loses.\n\n" +
            "Return compact Markdown using only sections that are actually present:\n" +
            "### Формулы\n- Preserve visible equations as LaTeX as exactly as possible; include equation numbers and define visible symbols when the page makes them clear.\n" +
            "### Таблицы\n- Reconstruct readable tables as Markdown. Preserve headings, units and footnotes.\n" +
            "### Графики\n- State axes, units, legends/curves, trends and readable numeric values. Never invent unreadable values.\n" +
            "### Рисунки и схемы\n- Describe components, labels, arrows, optical/electrical/mechanical relationships and figure caption if visible.\n" +
            "### Другие визуальные данные\n- Include diagrams, annotations, callouts, geometry or other meaningful non-prose content.\n\n" +
            "Use [неразборчиво] when a symbol/value cannot be read reliably. " +
            "If the page contains no meaningful formulas, tables, graphs, figures, diagrams or other non-prose visual information, answer exactly: VISUAL_EMPTY\n\n" +
            ocrContext;

        var endpoint = $"http://127.0.0.1:{port}/v1/chat/completions";
        var first = await SendAsync(endpoint, prompt, localMediaUrl, ct);

        string json;
        if (first.Success)
        {
            json = first.Body;
        }
        else
        {
            LogService.Warn(
                $"Document Vision local-media request failed HTTP {first.StatusCode}; retrying base64 fallback.");

            var bytes = await File.ReadAllBytesAsync(imagePath, ct);
            var base64Url = "data:image/png;base64," + Convert.ToBase64String(bytes);
            var second = await SendAsync(endpoint, prompt, base64Url, ct);
            if (!second.Success)
                throw new InvalidOperationException(
                    $"Document Vision HTTP {second.StatusCode}: {Compact(second.Body)}");
            json = second.Body;
        }

        using var document = JsonDocument.Parse(json);
        var content = ExtractContent(document.RootElement).Trim();
        content = StripOuterFence(content);

        var empty = string.Equals(content, "VISUAL_EMPTY", StringComparison.OrdinalIgnoreCase) ||
                    content.Length < 12;

        return new DocumentVisionAnalysis
        {
            PageNumber = pageNumber,
            Content = empty ? "" : content,
            HasVisualContent = !empty,
            ModelName = modelName,
            Status = empty ? "Визуальные элементы не обнаружены" : "Визуальные элементы распознаны"
        };
    }

    private async Task<(bool Success, int StatusCode, string Body)> SendAsync(
        string endpoint,
        string prompt,
        string mediaUrl,
        CancellationToken ct)
    {
        var body = new
        {
            model = "document-vision",
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new { type = "image_url", image_url = new { url = mediaUrl } }
                    }
                }
            },
            stream = false,
            temperature = 0.05,
            top_p = 0.9,
            max_tokens = 2200
        };

        using var response = await _http.PostAsJsonAsync(endpoint, body, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        return (response.IsSuccessStatusCode, (int)response.StatusCode, responseBody);
    }

    private static string StripOuterFence(string content)
    {
        var fence = new string((char)96, 3);
        if (!content.StartsWith(fence, StringComparison.Ordinal) ||
            !content.EndsWith(fence, StringComparison.Ordinal))
            return content;

        var firstBreak = content.IndexOf('\n');
        if (firstBreak > 0 && firstBreak < 30)
            return content[(firstBreak + 1)..^3].Trim();

        return content[3..^3].Trim();
    }

    private static string ExtractContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
            throw new InvalidDataException("Document Vision вернул ответ без choices.");

        var choice = choices[0];
        if (!choice.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
            throw new InvalidDataException("Document Vision вернул ответ без message.content.");

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";

        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String)
                    parts.Add(text.GetString() ?? "");
            }
            return string.Join("\n", parts);
        }

        return content.ToString();
    }

    private static string Compact(string value)
    {
        var oneLine = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length <= 700 ? oneLine : oneLine[..700] + "…";
    }
}
