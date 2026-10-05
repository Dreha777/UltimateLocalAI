using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public sealed class RagVectorIndexService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions JsonLine = new() { WriteIndented = false };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RagDocumentExtractor _extractor;
    private readonly BackendSelector _backendSelector;
    private readonly EmbeddingApiClient _embeddingClient = new();

    public RagVectorIndexService(FileTextExtractor extractor, BackendSelector backendSelector)
    {
        _extractor = new RagDocumentExtractor(extractor);
        _backendSelector = backendSelector;
        AppPaths.EnsureDirectories();
    }

    public RagIndexManifest GetManifest()
    {
        try
        {
            if (!File.Exists(AppPaths.RagManifestFile))
                return new RagIndexManifest();

            return JsonSerializer.Deserialize<RagIndexManifest>(
                       File.ReadAllText(AppPaths.RagManifestFile), Json)
                   ?? new RagIndexManifest();
        }
        catch (Exception ex)
        {
            LogService.Warn("RAG manifest read failed: " + ex.Message);
            return new RagIndexManifest();
        }
    }

    public List<KnowledgeDocument> GetDocuments()
    {
        var manifest = GetManifest();
        return manifest.Documents
            .OrderByDescending(x => x.IndexedAt)
            .Select(x => new KnowledgeDocument
            {
                Id = x.Id,
                SourcePath = x.SourcePath,
                DisplayName = x.DisplayName,
                IndexedAt = x.IndexedAt,
                PageCount = x.PageCount,
                ChunkCount = x.ChunkCount,
                IndexStatus = "Vector RAG",
                EmbeddingModelName = manifest.EmbeddingModelName
            })
            .ToList();
    }

    public async Task IndexFilesAsync(
        IReadOnlyList<string> paths,
        AppConfig config,
        HardwareInfo hardware,
        IProgress<RagIndexProgress>? progress = null,
        CancellationToken ct = default)
    {
        var actual = paths
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (actual.Count == 0)
            return;

        if (string.IsNullOrWhiteSpace(config.EmbeddingModelPath) || !File.Exists(config.EmbeddingModelPath))
            throw new InvalidOperationException("Сначала выберите отдельную GGUF embedding-модель для RAG.");

        await _gate.WaitAsync(ct);
        await using var server = new EmbeddingServerManager();
        try
        {
            AppPaths.EnsureDirectories();
            var manifest = GetManifest();
            var fingerprint = BuildEmbeddingFingerprint(config.EmbeddingModelPath, config.EmbeddingDocumentPrefix, config.EmbeddingPooling);
            ValidateEmbeddingSpace(manifest, config, fingerprint);

            var backend = _backendSelector.CpuFallback(hardware, config);
            progress?.Report(new RagIndexProgress
            {
                Phase = "embedding-server",
                Message = "Загрузка embedding-модели…",
                Total = actual.Count
            });

            await server.StartAsync(backend, config, config.EmbeddingModelPath, ct);

            for (var fileIndex = 0; fileIndex < actual.Count; fileIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var path = actual[fileIndex];
                await IndexOneAsync(path, config, manifest, fingerprint, fileIndex, actual.Count, progress, ct);
            }

            SaveManifest(manifest);
        }
        finally
        {
            await server.StopAsync();
            _gate.Release();
        }
    }

    private async Task IndexOneAsync(
        string path,
        AppConfig config,
        RagIndexManifest manifest,
        string fingerprint,
        int fileIndex,
        int fileTotal,
        IProgress<RagIndexProgress>? progress,
        CancellationToken ct)
    {
        var file = new FileInfo(path);
        var sha256 = await ComputeSha256Async(path, ct);
        var existing = manifest.Documents.FirstOrDefault(x =>
            string.Equals(x.SourcePath, path, StringComparison.OrdinalIgnoreCase));

        if (existing is not null &&
            existing.SourceSizeBytes == file.Length &&
            existing.SourceLastWriteUtc == file.LastWriteTimeUtc &&
            string.Equals(existing.SourceSha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new RagIndexProgress
            {
                Phase = "skip",
                FileName = file.Name,
                Completed = fileIndex + 1,
                Total = fileTotal,
                Message = "Уже проиндексирован, изменений нет."
            });
            return;
        }

        progress?.Report(new RagIndexProgress
        {
            Phase = "extract",
            FileName = file.Name,
            Completed = fileIndex,
            Total = fileTotal,
            Message = "Извлечение структуры документа…"
        });

        var segments = await _extractor.ExtractAsync(path, ct);
        if (segments.Count == 0)
            throw new InvalidDataException($"{file.Name}: не удалось извлечь текст.");

        var usefulChars = segments.Sum(x => x.Content.Count(ch => !char.IsWhiteSpace(ch)));
        if (usefulChars < 80 ||
            (segments.Count == 1 && segments[0].Content.Contains("потребуется OCR", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"{file.Name}: текстовый слой отсутствует или слишком слабый. " +
                "Этот документ будет обрабатываться OCR-конвейером на следующих стадиях 7G.");
        }

        var documentId = existing?.Id ?? manifest.NextDocumentId++;
        var nextChunkId = manifest.NextChunkId;
        var chunks = RagChunker.Build(segments, documentId, path, ref nextChunkId);
        if (chunks.Count == 0)
            throw new InvalidDataException($"{file.Name}: после разбиения не получено ни одного смыслового блока.");

        manifest.NextChunkId = nextChunkId;

        var tempChunkPath = Path.Combine(AppPaths.RagIndexDir, $"doc_{documentId}.jsonl.tmp");
        var tempVectorPath = Path.Combine(AppPaths.RagIndexDir, $"doc_{documentId}.vec.tmp");
        var finalChunkName = $"doc_{documentId}.jsonl";
        var finalVectorName = $"doc_{documentId}.vec";
        var finalChunkPath = Path.Combine(AppPaths.RagIndexDir, finalChunkName);
        var finalVectorPath = Path.Combine(AppPaths.RagIndexDir, finalVectorName);

        var dimensions = manifest.VectorDimensions;

        try
        {
            await using var chunkStream = new FileStream(tempChunkPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await using var chunkWriter = new StreamWriter(chunkStream, new UTF8Encoding(false));
            await using var vectorStream = new FileStream(tempVectorPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var vectorWriter = new BinaryWriter(vectorStream, Encoding.UTF8, leaveOpen: true);

            const int batchSize = 8;
            for (var offset = 0; offset < chunks.Count; offset += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = chunks.Skip(offset).Take(batchSize).ToList();
                var inputs = batch
                    .Select(x => (config.EmbeddingDocumentPrefix ?? "") + x.Content)
                    .ToArray();

                progress?.Report(new RagIndexProgress
                {
                    Phase = "embed",
                    FileName = file.Name,
                    Completed = offset,
                    Total = chunks.Count,
                    Message = $"Векторизация: {Math.Min(offset + batch.Count, chunks.Count)}/{chunks.Count}"
                });

                var vectors = await _embeddingClient.EmbedAsync(config.EmbeddingPort, inputs, ct);
                for (var i = 0; i < batch.Count; i++)
                {
                    var vector = vectors[i];
                    if (dimensions == 0)
                        dimensions = vector.Length;
                    if (vector.Length != dimensions)
                        throw new InvalidDataException(
                            $"Размерность embedding изменилась: ожидалось {dimensions}, получено {vector.Length}.");

                    var record = batch[i];
                    record.VectorIndex = offset + i;
                    await chunkWriter.WriteLineAsync(JsonSerializer.Serialize(record, JsonLine).AsMemory(), ct);

                    foreach (var value in vector)
                        vectorWriter.Write(value);
                }
            }

            await chunkWriter.FlushAsync(ct);
            await vectorStream.FlushAsync(ct);

            File.Move(tempChunkPath, finalChunkPath, true);
            File.Move(tempVectorPath, finalVectorPath, true);
        }
        catch
        {
            TryDelete(tempChunkPath);
            TryDelete(tempVectorPath);
            throw;
        }

        manifest.VectorDimensions = dimensions;
        manifest.EmbeddingModelPath = Path.GetFullPath(config.EmbeddingModelPath);
        manifest.EmbeddingModelFingerprint = fingerprint;
        manifest.EmbeddingModelName = Path.GetFileName(config.EmbeddingModelPath);
        manifest.EmbeddingDocumentPrefix = config.EmbeddingDocumentPrefix ?? "";
        manifest.EmbeddingPooling = config.EmbeddingPooling ?? "Auto";
        manifest.UpdatedAt = DateTime.Now;

        var indexed = new RagIndexedDocument
        {
            Id = documentId,
            SourcePath = path,
            DisplayName = file.Name,
            SourceSizeBytes = file.Length,
            SourceLastWriteUtc = file.LastWriteTimeUtc,
            SourceSha256 = sha256,
            PageCount = segments.Where(x => x.PageNumber.HasValue).Select(x => x.PageNumber!.Value).Distinct().Count(),
            ChunkCount = chunks.Count,
            ChunkFile = finalChunkName,
            VectorFile = finalVectorName,
            IndexedAt = DateTime.Now
        };

        if (existing is null)
            manifest.Documents.Add(indexed);
        else
        {
            var index = manifest.Documents.IndexOf(existing);
            manifest.Documents[index] = indexed;
        }

        SaveManifest(manifest);

        progress?.Report(new RagIndexProgress
        {
            Phase = "done",
            FileName = file.Name,
            Completed = fileIndex + 1,
            Total = fileTotal,
            Message = $"Готово: {chunks.Count} chunks."
        });
    }

    public async Task RemoveDocumentAsync(long documentId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var manifest = GetManifest();
            var doc = manifest.Documents.FirstOrDefault(x => x.Id == documentId);
            if (doc is null) return;

            TryDelete(Path.Combine(AppPaths.RagIndexDir, doc.ChunkFile));
            TryDelete(Path.Combine(AppPaths.RagIndexDir, doc.VectorFile));
            manifest.Documents.Remove(doc);
            manifest.UpdatedAt = DateTime.Now;

            if (manifest.Documents.Count == 0)
            {
                manifest.VectorDimensions = 0;
                manifest.EmbeddingModelPath = "";
                manifest.EmbeddingModelFingerprint = "";
                manifest.EmbeddingModelName = "";
                manifest.EmbeddingDocumentPrefix = "";
                manifest.EmbeddingPooling = "Auto";
            }

            SaveManifest(manifest);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            AppPaths.EnsureDirectories();
            foreach (var path in Directory.EnumerateFiles(AppPaths.RagIndexDir, "doc_*.*"))
                TryDelete(path);
            SaveManifest(new RagIndexManifest());
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidateEmbeddingSpace(RagIndexManifest manifest, AppConfig config, string fingerprint)
    {
        if (manifest.Documents.Count == 0 || string.IsNullOrWhiteSpace(manifest.EmbeddingModelFingerprint))
            return;

        if (!string.Equals(manifest.EmbeddingModelFingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Текущий RAG-индекс создан другой embedding-моделью или с другим document prefix. " +
                "Смешивать такие вектора нельзя. Очистите RAG-индекс и проиндексируйте документы заново.");
        }
    }

    private static string BuildEmbeddingFingerprint(string modelPath, string? documentPrefix, string? pooling)
    {
        var file = new FileInfo(modelPath);
        var canonical = Path.GetFullPath(modelPath);
        return string.Join("|",
            canonical.ToUpperInvariant(),
            file.Length,
            file.LastWriteTimeUtc.Ticks,
            documentPrefix ?? "",
            pooling?.Trim().ToLowerInvariant() ?? "auto");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash);
    }

    private static void SaveManifest(RagIndexManifest manifest)
    {
        AppPaths.EnsureDirectories();
        if (manifest.CreatedAt == default)
            manifest.CreatedAt = DateTime.Now;
        manifest.UpdatedAt = DateTime.Now;

        var temp = AppPaths.RagManifestFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(manifest, Json), new UTF8Encoding(false));
        File.Move(temp, AppPaths.RagManifestFile, true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            LogService.Warn($"RAG cleanup failed for '{path}': {ex.Message}");
        }
    }
}
