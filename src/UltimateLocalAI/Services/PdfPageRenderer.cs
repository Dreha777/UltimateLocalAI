using PdfiumRaster;

namespace UltimateLocalAI.Services;

public sealed class PdfPageRenderer
{
    public string RenderPageToPng(string pdfPath, int pageNumber, int dpi, string outputDirectory)
    {
        return RenderPage(pdfPath, pageNumber, dpi, outputDirectory, grayscale: true, "page");
    }

    public string RenderPageForVision(string pdfPath, int pageNumber, int dpi, string outputDirectory)
    {
        return RenderPage(pdfPath, pageNumber, dpi, outputDirectory, grayscale: false, "vision");
    }

    private static string RenderPage(string pdfPath, int pageNumber, int dpi, string outputDirectory, bool grayscale, string prefix)
    {
        if (!File.Exists(pdfPath))
            throw new FileNotFoundException("PDF не найден.", pdfPath);

        Directory.CreateDirectory(outputDirectory);
        dpi = Math.Clamp(dpi, 150, 450);

        var output = Path.Combine(outputDirectory, $"{prefix}-{pageNumber:D4}.png");
        var options = new PdfImageConversionOptions
        {
            Format = PdfImageOutputFormat.Png,
            Encoding = PdfImageEncodingOptions.Fast,
            Render = new PdfPageRenderOptions
            {
                Dpi = dpi,
                BackgroundColor = 0xFFFFFFFF,
                AntiAliasing = PdfAntiAliasing.All
            }
        };

        if (grayscale)
            options.ColorMode = PdfImageColorMode.Grayscale;

        PdfImageConverter.SavePageNumber(pdfPath, pageNumber, output, options);
        return output;
    }
}
