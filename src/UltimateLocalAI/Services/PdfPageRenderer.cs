using PdfiumRaster;

namespace UltimateLocalAI.Services;

public sealed class PdfPageRenderer
{
    public string RenderPageToPng(string pdfPath, int pageNumber, int dpi, string outputDirectory)
    {
        if (!File.Exists(pdfPath))
            throw new FileNotFoundException("PDF не найден.", pdfPath);

        Directory.CreateDirectory(outputDirectory);
        dpi = Math.Clamp(dpi, 150, 450);

        var output = Path.Combine(outputDirectory, $"page-{pageNumber:D4}.png");
        var options = new PdfImageConversionOptions
        {
            Format = PdfImageOutputFormat.Png,
            ColorMode = PdfImageColorMode.Grayscale,
            Encoding = PdfImageEncodingOptions.Fast,
            Render = new PdfPageRenderOptions
            {
                Dpi = dpi,
                BackgroundColor = 0xFFFFFFFF,
                AntiAliasing = PdfAntiAliasing.All
            }
        };

        PdfImageConverter.SavePageNumber(pdfPath, pageNumber, output, options);
        return output;
    }
}
