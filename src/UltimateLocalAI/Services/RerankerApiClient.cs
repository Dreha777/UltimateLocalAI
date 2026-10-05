using System.Net.Http.Json;
using System.Text.Json;

namespace UltimateLocalAI.Services;

public sealed class RerankerApiClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public async Task<List<(int Index, double Score)>> RerankAsync(
        int port,
        string query,
        IReadOnlyList<string> documents,
        int topN,
        CancellationToken ct = default)
    {
        if (documents.Count == 0) return [];

        var body = new
        {
            model = "local-reranker",
            query,
            top_n = Math.Clamp(topN, 1, documents.Count),
            documents
        };

        using var response = await _http.PostAsJsonAsync($"http://127.0.0.1:{port}/v1/rerank", body, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Reranker HTTP {(int)response.StatusCode}: {Compact(json)}");

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Reranker вернул ответ без массива results.");

        var output = new List<(int Index, double Score)>();
        foreach (var item in results.EnumerateArray())
        {
            if (!item.TryGetProperty("index", out var indexNode) || !indexNode.TryGetInt32(out var index))
                continue;

            double score;
            if (item.TryGetProperty("relevance_score", out var rel) && rel.TryGetDouble(out var relevance))
                score = relevance;
            else if (item.TryGetProperty("score", out var raw) && raw.TryGetDouble(out var parsed))
                score = parsed;
            else
                continue;

            if (index >= 0 && index < documents.Count)
                output.Add((index, score));
        }

        return output.OrderByDescending(x => x.Score).ToList();
    }

    private static string Compact(string value)
    {
        var oneLine = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length <= 500 ? oneLine : oneLine[..500] + "…";
    }
}
