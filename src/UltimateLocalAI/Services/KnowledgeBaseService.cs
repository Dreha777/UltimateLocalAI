using System.Text.Json;
using System.Text.RegularExpressions;
using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public sealed partial class KnowledgeBaseService
{
    private sealed class KnowledgeChunk
    {
        public long DocumentId { get; set; }
        public int ChunkIndex { get; set; }
        public string Content { get; set; } = "";
    }

    private sealed class KnowledgeStore
    {
        public long NextDocumentId { get; set; } = 1;
        public List<KnowledgeDocument> Documents { get; set; } = [];
        public List<KnowledgeChunk> Chunks { get; set; } = [];
    }

    private readonly object _gate = new();
    private readonly FileTextExtractor _extractor;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = false };
    private KnowledgeStore _store;

    public KnowledgeBaseService(FileTextExtractor extractor)
    {
        _extractor = extractor;
        AppPaths.EnsureDirectories();
        _store = Load();
    }

    private KnowledgeStore Load()
    {
        try
        {
            if (!File.Exists(AppPaths.KnowledgeFile)) return new KnowledgeStore();
            return JsonSerializer.Deserialize<KnowledgeStore>(File.ReadAllText(AppPaths.KnowledgeFile)) ?? new KnowledgeStore();
        }
        catch (Exception ex)
        {
            LogService.Warn("Не удалось прочитать knowledge.json: " + ex.Message);
            return new KnowledgeStore();
        }
    }

    private void SaveLocked()
    {
        var tmp = AppPaths.KnowledgeFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_store, _json));
        if (File.Exists(AppPaths.KnowledgeFile)) File.Replace(tmp, AppPaths.KnowledgeFile, null, true);
        else File.Move(tmp, AppPaths.KnowledgeFile);
    }

    public async Task IndexFileAsync(string path)
    {
        var attachment = await _extractor.ExtractAsync(path);
        var chunks = Chunk(attachment.ExtractedText, 2400, 300).ToList();
        var fullPath = Path.GetFullPath(path);

        lock (_gate)
        {
            var doc = _store.Documents.FirstOrDefault(x => string.Equals(x.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase));
            if (doc is null)
            {
                doc = new KnowledgeDocument { Id = _store.NextDocumentId++, SourcePath = fullPath };
                _store.Documents.Add(doc);
            }
            doc.DisplayName = attachment.FileName;
            doc.IndexedAt = DateTime.Now;
            _store.Chunks.RemoveAll(x => x.DocumentId == doc.Id);
            for (var i = 0; i < chunks.Count; i++)
                _store.Chunks.Add(new KnowledgeChunk { DocumentId = doc.Id, ChunkIndex = i, Content = chunks[i] });
            SaveLocked();
        }
    }

    public List<KnowledgeDocument> GetDocuments()
    {
        lock (_gate)
            return _store.Documents.OrderByDescending(x => x.IndexedAt).Select(x => new KnowledgeDocument
            {
                Id = x.Id, SourcePath = x.SourcePath, DisplayName = x.DisplayName, IndexedAt = x.IndexedAt
            }).ToList();
    }

    public void RemoveDocument(long id)
    {
        lock (_gate)
        {
            _store.Chunks.RemoveAll(x => x.DocumentId == id);
            _store.Documents.RemoveAll(x => x.Id == id);
            SaveLocked();
        }
    }

    public List<KnowledgeHit> Search(string query, int top = 5)
    {
        var terms = Tokenize(query).Distinct().Take(24).ToArray();
        if (terms.Length == 0) return [];

        lock (_gate)
        {
            var docs = _store.Documents.ToDictionary(x => x.Id);
            var hits = new List<KnowledgeHit>();
            foreach (var chunk in _store.Chunks.Take(10000))
            {
                var lower = chunk.Content.ToLowerInvariant();
                double score = 0;
                foreach (var term in terms)
                {
                    var count = CountOccurrences(lower, term);
                    if (count > 0) score += 1 + Math.Log(1 + count);
                }
                if (score > 0 && docs.TryGetValue(chunk.DocumentId, out var doc))
                    hits.Add(new KnowledgeHit { SourcePath = doc.SourcePath, Content = chunk.Content, Score = score });
            }
            return hits.OrderByDescending(x => x.Score).Take(top).ToList();
        }
    }

    private static IEnumerable<string> Chunk(string text, int size, int overlap)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        for (var start = 0; start < text.Length; start += Math.Max(1, size - overlap))
        {
            var len = Math.Min(size, text.Length - start);
            yield return text.Substring(start, len);
            if (start + len >= text.Length) break;
        }
    }

    private static IEnumerable<string> Tokenize(string text) => WordRegex().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(x => x.Length >= 3);
    private static int CountOccurrences(string text, string term)
    {
        var count = 0; var pos = 0;
        while ((pos = text.IndexOf(term, pos, StringComparison.Ordinal)) >= 0) { count++; pos += term.Length; }
        return count;
    }

    [GeneratedRegex(@"[\p{L}\p{N}_-]+", RegexOptions.Compiled)]
    private static partial Regex WordRegex();
}
