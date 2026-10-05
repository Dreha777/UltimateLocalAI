using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public sealed class ModelCatalogService
{
    public Task<List<ModelCatalogItem>> ScanAsync(IEnumerable<string> folders, HardwareInfo hardware, string currentModelPath, CancellationToken ct = default)
        => Task.Run(() => Scan(folders, hardware, currentModelPath, ct), ct);

    private static List<ModelCatalogItem> Scan(IEnumerable<string> folders, HardwareInfo hardware, string currentModelPath, CancellationToken ct)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var folder in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.gguf", options))
                {
                    ct.ThrowIfCancellationRequested();
                    files.Add(Path.GetFullPath(file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                LogService.Warn($"Model catalog scan skipped folder '{folder}': {ex.Message}");
            }
        }

        var result = new List<ModelCatalogItem>(files.Count);
        foreach (var path in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var fi = new FileInfo(path);
                var meta = GgufMetadataReader.Read(path);
                result.Add(BuildItem(fi, meta, hardware, currentModelPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Warn($"Model catalog skipped '{path}': {ex.Message}");
            }
        }

        return result
            .OrderByDescending(x => x.IsCurrent)
            .ThenByDescending(x => x.RecommendationRank)
            .ThenBy(x => x.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static ModelCatalogItem BuildItem(FileInfo file, GgufModelMetadata meta, HardwareInfo hw, string currentModelPath)
    {
        var quant = string.IsNullOrWhiteSpace(meta.Quantization) ? GuessQuantFromName(file.Name) : meta.Quantization;
        var quality = DescribeQuality(quant);
        var qualityRank = QualityRank(quant);

        var sizeMb = Math.Max(1d, file.Length / 1024d / 1024d);
        var estimatedSystemNeedMb = sizeMb * 1.22 + EstimateContextReserveMb(meta.NativeContextSize);
        var totalRam = Math.Max(0, hw.TotalRamMb);
        var availableRam = Math.Max(0, hw.AvailableRamMb);
        var vram = Math.Max(0, hw.GpuVramMb);

        string hardwareFit;
        int fitRank;
        if (totalRam <= 0)
        {
            hardwareFit = "RAM не определена";
            fitRank = 1;
        }
        else if (availableRam >= estimatedSystemNeedMb + 1024)
        {
            hardwareFit = "✓ Помещается в доступную RAM";
            fitRank = 4;
        }
        else if (totalRam >= estimatedSystemNeedMb + 1536)
        {
            hardwareFit = "△ Поместится после освобождения RAM";
            fitRank = 3;
        }
        else if (totalRam >= sizeMb * 1.05 + 1024)
        {
            hardwareFit = "△ Погранично по RAM";
            fitRank = 2;
        }
        else
        {
            hardwareFit = "✗ Слишком тяжёлая для RAM";
            fitRank = 0;
        }

        string gpuHint;
        if (!hw.GpuDetected || vram <= 0)
        {
            gpuHint = "CPU";
        }
        else if (vram >= sizeMb * 1.18 + 768)
        {
            gpuHint = "полный GPU вероятен";
        }
        else if (vram >= 2048)
        {
            gpuHint = "частичный GPU offload";
        }
        else
        {
            gpuHint = "GPU мал, в основном CPU";
        }

        var advice = BuildAdvice(quant, qualityRank, fitRank, gpuHint);
        var recommendationRank = fitRank * 10 + qualityRank;

        return new ModelCatalogItem
        {
            FileName = file.Name,
            FullPath = file.FullName,
            SizeBytes = file.Length,
            Architecture = string.IsNullOrWhiteSpace(meta.Architecture) ? "—" : meta.Architecture,
            SizeLabel = string.IsNullOrWhiteSpace(meta.SizeLabel) ? "—" : meta.SizeLabel,
            Quantization = string.IsNullOrWhiteSpace(quant) ? "—" : quant,
            NativeContextSize = meta.NativeContextSize,
            Quality = quality,
            HardwareFit = hardwareFit + " · " + gpuHint,
            Advice = advice,
            RecommendationRank = recommendationRank,
            IsCurrent = string.Equals(Path.GetFullPath(file.FullName), SafeFullPath(currentModelPath), StringComparison.OrdinalIgnoreCase)
        };
    }

    private static string BuildAdvice(string quant, int qualityRank, int fitRank, string gpuHint)
    {
        if (fitRank == 0)
            return "Не рекомендуется на этом ПК: модель не помещается в системную память с безопасным запасом.";

        if (IsAggressiveQuant(quant))
            return "Качество ограничено сильной квантовкой. Если память позволяет, предпочтительнее Q4_K_M, Q5_K_M, Q6_K или Q8_0.";

        if (fitRank == 2)
            return "Можно пробовать, но нужен запас RAM; закройте тяжёлые приложения и оставьте Auto.";

        if (qualityRank >= 5)
            return $"Высокое качество квантования; {gpuHint}. Хороший кандидат для профиля «Качество».";

        if (qualityRank >= 4)
            return $"Хороший баланс качества и памяти; {gpuHint}. Рекомендуемый универсальный вариант.";

        return $"Совместимость выглядит приемлемо; {gpuHint}. Итоговое качество зависит от самой архитектуры и обучения модели.";
    }

    private static string DescribeQuality(string quant)
    {
        var q = quant.ToUpperInvariant();
        if (IsAggressiveQuant(q)) return "Низкое / экономное";
        if (q.Contains("Q4") || q.Contains("IQ4") || q.Contains("MXFP4") || q.Contains("NVFP4")) return "Хороший баланс";
        if (q.Contains("Q5") || q.Contains("Q6")) return "Высокое";
        if (q.Contains("Q8") || q.Contains("F16") || q.Contains("BF16") || q.Contains("F32")) return "Очень высокое";
        return "Не определено";
    }

    private static int QualityRank(string quant)
    {
        var q = quant.ToUpperInvariant();
        if (IsAggressiveQuant(q)) return 1;
        if (q.Contains("Q4") || q.Contains("IQ4") || q.Contains("MXFP4") || q.Contains("NVFP4")) return 4;
        if (q.Contains("Q5") || q.Contains("Q6")) return 5;
        if (q.Contains("Q8") || q.Contains("F16") || q.Contains("BF16") || q.Contains("F32")) return 6;
        return 2;
    }

    private static bool IsAggressiveQuant(string quant)
    {
        var q = quant.ToUpperInvariant();
        return q.Contains("IQ1") || q.Contains("IQ2") || q.Contains("IQ3") ||
               q.Contains("TQ1") || q.Contains("TQ2") ||
               q.Contains("Q1_") || q.Contains("Q2_") || q.Contains("Q2-") ||
               q.Contains("Q3_") || q.Contains("Q3-");
    }

    private static double EstimateContextReserveMb(long? nativeContext)
    {
        var target = nativeContext is > 0 ? Math.Min(nativeContext.Value, 8192) : 4096;
        return target >= 8192 ? 1800 : target >= 4096 ? 1000 : 650;
    }

    private static string GuessQuantFromName(string fileName)
    {
        var upper = Path.GetFileNameWithoutExtension(fileName).ToUpperInvariant();
        var known = new[]
        {
            "IQ1_M", "IQ1_S", "IQ2_XXS", "IQ2_XS", "IQ2_S", "IQ2_M",
            "IQ3_XXS", "IQ3_XS", "IQ3_S", "IQ3_M", "IQ4_XS", "IQ4_NL",
            "Q2_K", "Q3_K_S", "Q3_K_M", "Q3_K_L", "Q4_K_S", "Q4_K_M",
            "Q5_K_S", "Q5_K_M", "Q6_K", "Q8_0", "Q5_0", "Q5_1", "Q4_0", "Q4_1",
            "BF16", "F16", "F32"
        };

        return known.FirstOrDefault(x => upper.Contains(x, StringComparison.OrdinalIgnoreCase)) ?? "";
    }

    private static string SafeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
