using System.Net.Http.Json;
using System.Text.Json;

namespace UltimateLocalAI.Services;

public sealed class EmbeddingApiClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public async Task<List<float[]>> EmbedAsync(int port, IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0) return [];

        var body = new
        {
            input = texts,
            model = "local-embedding",
            encoding_format = "float"
        };

        using var response = await _http.PostAsJsonAsync($"http://127.0.0.1:{port}/v1/embeddings", body, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Embedding HTTP {(int)response.StatusCode}: {Compact(json)}");

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Embedding server вернул ответ без массива data.");

        var ordered = new List<(int index, float[] vector)>();
        foreach (var item in data.EnumerateArray())
        {
            var index = item.TryGetProperty("index", out var indexNode) && indexNode.TryGetInt32(out var parsed)
                ? parsed
                : ordered.Count;

            if (!item.TryGetProperty("embedding", out var emb) || emb.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Embedding server вернул элемент без embedding.");

            var vector = emb.EnumerateArray()
                .Select(x => x.GetSingle())
                .ToArray();

            if (vector.Length == 0)
                throw new InvalidDataException("Embedding server вернул пустой вектор.");

            ordered.Add((index, vector));
        }

        var result = ordered.OrderBy(x => x.index).Select(x => x.vector).ToList();
        if (result.Count != texts.Count)
            throw new InvalidDataException($"Embedding server вернул {result.Count} векторов вместо {texts.Count}.");

        var dimensions = result[0].Length;
        if (result.Any(x => x.Length != dimensions))
            throw new InvalidDataException("Embedding server вернул векторы разной размерности.");

        return result;
    }

    private static string Compact(string value)
    {
        var oneLine = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length <= 500 ? oneLine : oneLine[..500] + "…";
    }
}
