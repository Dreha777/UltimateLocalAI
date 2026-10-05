using System.Text;
using System.Text.Json;
using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public sealed class RagRetrievalService
{
    private readonly BackendSelector _backendSelector;
    private readonly EmbeddingApiClient _embeddingClient = new();
    private readonly RerankerApiClient _rerankerClient = new();

    public RagRetrievalService(BackendSelector backendSelector)
    {
        _backendSelector = backendSelector;
    }

    public async Task<RagRetrievalResult> SearchAsync(
        string query,
        AppConfig config,
        HardwareInfo hardware,
        CancellationToken ct = default)
    {
        var manifest = LoadManifest();
        if (manifest.Documents.Count == 0)
            return new RagRetrievalResult
            {
                Diagnostics = new RagRetrievalDiagnostics { Status = "RAG-индекс пуст." }
            };

        if (string.IsNullOrWhiteSpace(manifest.EmbeddingModelPath) || !File.Exists(manifest.EmbeddingModelPath))
            throw new FileNotFoundException(
                "Embedding-модель, которой была создана библиотека, не найдена. " +
                "Chat-модель можно менять свободно, но semantic search должен использовать ту же embedding-модель, что и индекс.",
                manifest.EmbeddingModelPath);

        var retrieval = new RagRetrievalResult
        {
            Diagnostics = new RagRetrievalDiagnostics
            {
                EmbeddingModelName = manifest.EmbeddingModelName,
                RerankerModelName = File.Exists(config.RerankerModelPath) ? Path.GetFileName(config.RerankerModelPath) : "",
                Status = "Semantic search"
            }
        };

        var queryConfig = CloneConfigForEmbedding(config, manifest);
        var backend = _backendSelector.CpuFallback(hardware, queryConfig);

        await using var embeddingServer = new EmbeddingServerManager();
        await embeddingServer.StartAsync(backend, queryConfig, manifest.EmbeddingModelPath, ct);

        var queryInput = (config.EmbeddingQueryPrefix ?? "") + query.Trim();
        var queryVectors = await _embeddingClient.EmbedAsync(config.EmbeddingPort, [queryInput], ct);
        var queryVector = queryVectors[0];
        await embeddingServer.StopAsync();

        if (queryVector.Length != manifest.VectorDimensions)
            throw new InvalidDataException(
                $"Размерность query embedding ({queryVector.Length}) не совпадает с индексом ({manifest.VectorDimensions}). " +
                "Индекс необходимо перестроить той же embedding-моделью.");

        var candidateTopK = Math.Clamp(config.RagCandidateTopK, 4, 100);
        var finalTopK = Math.Clamp(config.RagFinalTopK, 1, Math.Min(20, candidateTopK));
        var candidates = ScanTopCandidates(manifest, queryVector, candidateTopK, ct);
        retrieval.Diagnostics.CandidateCount = candidates.Count;

        if (candidates.Count == 0)
        {
            retrieval.Diagnostics.Status = "Semantic search не нашёл подходящих chunks.";
            return retrieval;
        }

        var final = candidates.Take(finalTopK).ToList();

        if (config.UseReranker &&
            !string.IsNullOrWhiteSpace(config.RerankerModelPath) &&
            File.Exists(config.RerankerModelPath) &&
            candidates.Count > 1)
        {
            try
            {
                await using var rerankerServer = new RerankerServerManager();
                await rerankerServer.StartAsync(_backendSelector.CpuFallback(hardware, config), config, config.RerankerModelPath, ct);

                var ranked = await _rerankerClient.RerankAsync(
                    config.RerankerPort,
                    query,
                    candidates.Select(x => BuildRerankerText(x)).ToArray(),
                    finalTopK,
                    ct);

                if (ranked.Count > 0)
                {
                    final = ranked
                        .Where(x => x.Index >= 0 && x.Index < candidates.Count)
                        .Select(x =>
                        {
                            var hit = CloneHit(candidates[x.Index]);
                            hit.RerankScore = x.Score;
                            hit.Score = x.Score;
                            hit.RetrievalMethod = "semantic + reranker";
                            return hit;
                        })
                        .Take(finalTopK)
                        .ToList();

                    retrieval.Diagnostics.UsedReranker = true;
                    retrieval.Diagnostics.Status = "Semantic search + reranker";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogService.Warn("RAG reranker fallback to semantic ranking: " + ex.Message);
                retrieval.Diagnostics.Status = "Semantic search; reranker недоступен, использован fallback.";
            }
        }

        foreach (var hit in final)
        {
            if (string.IsNullOrWhiteSpace(hit.RetrievalMethod))
            {
                hit.Score = hit.SemanticScore;
                hit.RetrievalMethod = "semantic";
            }
        }

        retrieval.Hits = final;
        retrieval.Diagnostics.ReturnedCount = final.Count;

        LogService.Info(
            $"RAG-SEARCH embedding=\"{retrieval.Diagnostics.EmbeddingModelName}\" candidates={retrieval.Diagnostics.CandidateCount} " +
            $"returned={retrieval.Diagnostics.ReturnedCount} reranker={retrieval.Diagnostics.UsedReranker}");

        return retrieval;
    }

    private static List<KnowledgeHit> ScanTopCandidates(
        RagIndexManifest manifest,
        float[] query,
        int topK,
        CancellationToken ct)
    {
        var queryNorm = Norm(query);
        if (queryNorm <= 0)
            throw new InvalidDataException("Query embedding имеет нулевую норму.");

        var queue = new PriorityQueue<KnowledgeHit, double>();

        foreach (var doc in manifest.Documents)
        {
            ct.ThrowIfCancellationRequested();

            var chunkPath = Path.Combine(AppPaths.RagIndexDir, doc.ChunkFile);
            var vectorPath = Path.Combine(AppPaths.RagIndexDir, doc.VectorFile);
            if (!File.Exists(chunkPath) || !File.Exists(vectorPath))
            {
                LogService.Warn($"RAG document files missing: {doc.DisplayName}");
                continue;
            }

            using var vectorStream = new FileStream(vectorPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var vectorReader = new BinaryReader(vectorStream, Encoding.UTF8, leaveOpen: false);
            using var chunkStream = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var chunkReader = new StreamReader(chunkStream, Encoding.UTF8);

            string? line;
            while ((line = chunkReader.ReadLine()) is not null)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;

                RagChunkRecord? chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize<RagChunkRecord>(line);
                }
                catch (JsonException ex)
                {
                    LogService.Warn($"RAG chunk JSON skipped in {doc.DisplayName}: {ex.Message}");
                    SkipVector(vectorReader, manifest.VectorDimensions);
                    continue;
                }

                var vector = ReadVector(vectorReader, manifest.VectorDimensions);
                if (vector is null)
                {
                    LogService.Warn($"RAG vector file truncated: {doc.DisplayName}");
                    break;
                }

                if (chunk is null) continue;

                var score = Cosine(query, queryNorm, vector);
                var hit = new KnowledgeHit
                {
                    SourcePath = chunk.SourcePath,
                    DisplayName = string.IsNullOrWhiteSpace(doc.DisplayName) ? Path.GetFileName(chunk.SourcePath) : doc.DisplayName,
                    Content = chunk.Content,
                    Section = chunk.Section,
                    PageFrom = chunk.PageFrom,
                    PageTo = chunk.PageTo,
                    Score = score,
                    SemanticScore = score,
                    RetrievalMethod = "semantic"
                };

                if (queue.Count < topK)
                {
                    queue.Enqueue(hit, score);
                }
                else if (queue.TryPeek(out _, out var minScore) && score > minScore)
                {
                    queue.Dequeue();
                    queue.Enqueue(hit, score);
                }
            }
        }

        var result = new List<KnowledgeHit>(queue.Count);
        while (queue.TryDequeue(out var hit, out _))
            result.Add(hit);

        result.Reverse();
        return result;
    }

    private static float[]? ReadVector(BinaryReader reader, int dimensions)
    {
        if (dimensions <= 0) return null;
        var bytesNeeded = (long)dimensions * sizeof(float);
        if (reader.BaseStream.Length - reader.BaseStream.Position < bytesNeeded)
            return null;

        var vector = new float[dimensions];
        for (var i = 0; i < dimensions; i++)
            vector[i] = reader.ReadSingle();
        return vector;
    }

    private static void SkipVector(BinaryReader reader, int dimensions)
    {
        var bytes = (long)Math.Max(0, dimensions) * sizeof(float);
        if (reader.BaseStream.Position <= reader.BaseStream.Length - bytes)
            reader.BaseStream.Seek(bytes, SeekOrigin.Current);
    }

    private static double Cosine(float[] query, double queryNorm, float[] vector)
    {
        double dot = 0;
        double norm = 0;
        var len = Math.Min(query.Length, vector.Length);
        for (var i = 0; i < len; i++)
        {
            dot += query[i] * vector[i];
            norm += vector[i] * vector[i];
        }

        return norm <= 0 ? -1 : dot / (queryNorm * Math.Sqrt(norm));
    }

    private static double Norm(float[] vector)
    {
        double sum = 0;
        foreach (var value in vector)
            sum += value * value;
        return Math.Sqrt(sum);
    }

    private static string BuildRerankerText(KnowledgeHit hit)
    {
        var meta = new StringBuilder();
        meta.Append("Источник: ").Append(hit.DisplayName);
        if (!string.IsNullOrWhiteSpace(hit.PageLabel)) meta.Append(", ").Append(hit.PageLabel);
        if (!string.IsNullOrWhiteSpace(hit.Section)) meta.Append(", раздел: ").Append(hit.Section);
        meta.AppendLine();
        meta.Append(hit.Content);
        return meta.ToString();
    }

    private static KnowledgeHit CloneHit(KnowledgeHit source) => new()
    {
        SourcePath = source.SourcePath,
        DisplayName = source.DisplayName,
        Content = source.Content,
        Section = source.Section,
        PageFrom = source.PageFrom,
        PageTo = source.PageTo,
        Score = source.Score,
        SemanticScore = source.SemanticScore,
        RerankScore = source.RerankScore,
        RetrievalMethod = source.RetrievalMethod
    };

    private static AppConfig CloneConfigForEmbedding(AppConfig source, RagIndexManifest manifest) => new()
    {
        ModelPath = source.ModelPath,
        BackendMode = source.BackendMode,
        RuntimeChannel = source.RuntimeChannel,
        CustomRuntimePath = source.CustomRuntimePath,
        Port = source.Port,
        AutoStartLastModel = source.AutoStartLastModel,
        AutoFallbackToCpu = source.AutoFallbackToCpu,
        UseKnowledgeBase = source.UseKnowledgeBase,
        WorkspaceBackground = source.WorkspaceBackground,
        ModelFolders = source.ModelFolders.ToList(),
        EmbeddingModelPath = manifest.EmbeddingModelPath,
        EmbeddingPort = source.EmbeddingPort,
        EmbeddingDocumentPrefix = manifest.EmbeddingDocumentPrefix,
        EmbeddingQueryPrefix = source.EmbeddingQueryPrefix,
        EmbeddingPooling = manifest.EmbeddingPooling,
        RerankerModelPath = source.RerankerModelPath,
        RerankerPort = source.RerankerPort,
        UseReranker = source.UseReranker,
        RagCandidateTopK = source.RagCandidateTopK,
        RagFinalTopK = source.RagFinalTopK,
        Runtime = source.Runtime,
        Generation = source.Generation
    };

    private static RagIndexManifest LoadManifest()
    {
        if (!File.Exists(AppPaths.RagManifestFile))
            return new RagIndexManifest();

        try
        {
            return JsonSerializer.Deserialize<RagIndexManifest>(File.ReadAllText(AppPaths.RagManifestFile))
                   ?? new RagIndexManifest();
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("Не удалось прочитать RAG manifest.", ex);
        }
    }
}
