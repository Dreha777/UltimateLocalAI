using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public sealed class LlamaApiClient
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public async Task StreamChatAsync(int port, IEnumerable<ChatMessage> history, GenerationSettings settings,
        Action<string> onDelta, CancellationToken ct)
    {
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(settings.SystemPrompt))
            messages.Add(new { role = "system", content = settings.SystemPrompt });

        messages.Add(new
        {
            role = "system",
            content = "Форматируй ответ читаемо в Markdown. Код помещай в fenced-блоки с языком: ```csharp, ```python и т.п. " +
                      "Математические формулы записывай в LaTeX: отдельные формулы между $ ... $ или \\[ ... \\], " +
                      "короткие формулы в тексте между \\( ... \\). Не используй HTML для оформления."
        });

        foreach (var m in history)
        {
            var content = m.Content;
            if (m.Role == "user" && !string.IsNullOrWhiteSpace(m.ContextText))
                content += "\n\n--- Прикреплённые материалы / найденный контекст ---\n" + m.ContextText;
            messages.Add(new { role = m.Role, content });
        }

        var body = new
        {
            model = "local-model",
            messages,
            stream = true,
            temperature = settings.Temperature,
            top_p = settings.TopP,
            top_k = settings.TopK,
            min_p = settings.MinP,
            repeat_penalty = settings.RepeatPenalty,
            max_tokens = settings.MaxTokens,
            seed = settings.Seed
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/v1/chat/completions")
        {
            Content = JsonContent.Create(body)
        };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var errorBody = resp.IsSuccessStatusCode ? null : await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"llama-server HTTP {(int)resp.StatusCode}: {errorBody}");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
                var choice = choices[0];
                if (!choice.TryGetProperty("delta", out var delta)) continue;
                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString();
                    if (!string.IsNullOrEmpty(text)) onDelta(text);
                }
                // reasoning_content намеренно не смешивается с финальным ответом.
                // Режим thinking настраивается на сервере, а в чат выводится итоговое content.
            }
            catch (JsonException ex) { LogService.Warn("SSE parse: " + ex.Message); }
        }
    }

    public async Task<ModelServerProperties?> GetServerPropertiesAsync(int port, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"http://127.0.0.1:{port}/props", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                LogService.Warn($"GET /props returned HTTP {(int)resp.StatusCode}");
                return null;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            var result = new ModelServerProperties
            {
                ModelAlias = TryGetString(root, "model_alias"),
                ModelFtype = TryGetString(root, "model_ftype"),
                ChatTemplate = TryGetString(root, "chat_template")
            };

            if (root.TryGetProperty("default_generation_settings", out var defaults) &&
                defaults.ValueKind == JsonValueKind.Object &&
                defaults.TryGetProperty("n_ctx", out var nctx) &&
                nctx.TryGetInt32(out var parsedContext))
            {
                result.ContextSize = parsedContext;
            }

            if (root.TryGetProperty("chat_template_caps", out var caps) && caps.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in caps.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.True ||
                        (property.Value.ValueKind == JsonValueKind.False && property.Value.GetBoolean()))
                    {
                        result.ChatTemplateCapabilities.Add(property.Name);
                    }
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            LogService.Warn("Model properties unavailable: " + ex.Message);
            return null;
        }
    }

    private static string TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return "";

        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : property.ToString();
    }

    public async Task<int> TokenCountAsync(int port, string text, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync($"http://127.0.0.1:{port}/tokenize", new { content = text, add_special = false }, ct);
            if (!resp.IsSuccessStatusCode) return Math.Max(1, text.Length / 4);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("tokens", out var tokens) ? tokens.GetArrayLength() : Math.Max(1, text.Length / 4);
        }
        catch { return Math.Max(1, text.Length / 4); }
    }
}
