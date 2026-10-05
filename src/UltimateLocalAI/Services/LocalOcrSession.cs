using TesseractOCR;
using TesseractOCR.Enums;

namespace UltimateLocalAI.Services;

public sealed class LocalOcrSession : IDisposable
{
    private readonly Engine _engine;

    public string Languages { get; }

    public LocalOcrSession(string languages)
    {
        Languages = NormalizeLanguages(languages);
        ValidateAssets(Languages);
        _engine = new Engine(AppPaths.OcrTessdataDir, Languages, EngineMode.LstmOnly);
        _engine.DefaultPageSegMode = PageSegMode.Auto;
    }

    public OcrRecognitionResult Recognize(string imagePath)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("Изображение OCR не найдено.", imagePath);

        using var image = TesseractOCR.Pix.Image.LoadFromFile(imagePath);
        using var page = _engine.Process(image, PageSegMode.Auto);

        var text = PdfImportAnalyzer.Normalize(page.Text);
        var confidence = Math.Clamp(Convert.ToDouble(page.MeanConfidence), 0.0, 1.0);
        return new OcrRecognitionResult
        {
            Text = text,
            Confidence = confidence,
            Languages = Languages
        };
    }

    public static bool IsReady(string languages, out string reason)
    {
        try
        {
            ValidateAssets(NormalizeLanguages(languages));
            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static void ValidateAssets(string languages)
    {
        if (!Directory.Exists(AppPaths.OcrTessdataDir))
            throw new DirectoryNotFoundException($"OCR tessdata не найдена: {AppPaths.OcrTessdataDir}");

        foreach (var lang in languages.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var file = Path.Combine(AppPaths.OcrTessdataDir, lang + ".traineddata");
            if (!File.Exists(file))
                throw new FileNotFoundException($"Не найден OCR-язык {lang}.traineddata. Установите полный GUI patch/Portable со Stage 7G-4.", file);
        }
    }

    private static string NormalizeLanguages(string? languages)
    {
        var requested = (languages ?? "rus+eng")
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant())
            .Where(x => x is "rus" or "eng")
            .Distinct()
            .ToList();

        if (requested.Count == 0)
            requested.AddRange(["rus", "eng"]);

        return string.Join("+", requested);
    }

    public void Dispose() => _engine.Dispose();
}

public sealed class OcrRecognitionResult
{
    public string Text { get; set; } = "";
    public double Confidence { get; set; }
    public string Languages { get; set; } = "";
}
