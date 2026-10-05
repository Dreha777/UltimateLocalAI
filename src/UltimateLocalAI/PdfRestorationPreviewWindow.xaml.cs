using System.Windows;
using System.Windows.Media.Imaging;
using UltimateLocalAI.Models;
using UltimateLocalAI.Services;

namespace UltimateLocalAI;

public partial class PdfRestorationPreviewWindow : Window
{
    private readonly string _pdfPath;
    private readonly int _pageNumber;
    private readonly AppConfig _config;
    private readonly PdfPageRenderer _renderer = new();
    private readonly ScanRestorationService _restoration = new();
    private string? _tempDir;

    public PdfRestorationPreviewWindow(string pdfPath, int pageNumber, AppConfig config)
    {
        InitializeComponent();
        _pdfPath = pdfPath;
        _pageNumber = pageNumber;
        _config = config;

        TitleText.Text = $"{Path.GetFileName(pdfPath)} · стр. {pageNumber}";
        Loaded += async (_, _) => await BuildPreviewAsync();
        Closed += (_, _) => Cleanup();
    }

    private async Task BuildPreviewAsync()
    {
        StatusText.Text = "Подготовка страницы и сравнение OCR-кандидатов…";

        try
        {
            if (!File.Exists(_pdfPath))
                throw new FileNotFoundException("PDF больше не найден.", _pdfPath);

            if (!LocalOcrSession.IsReady(_config.OcrLanguages, out var reason))
                throw new InvalidOperationException("OCR не готов: " + reason);

            _tempDir = Path.Combine(AppPaths.OcrCacheDir, "preview-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            var originalPath = await Task.Run(() =>
                _renderer.RenderPageToPng(
                    _pdfPath,
                    _pageNumber,
                    Math.Clamp(_config.OcrDpi, 150, 450),
                    _tempDir));

            var candidateDir = Path.Combine(_tempDir, "restored");
            var candidates = await Task.Run(() =>
                _restoration.BuildCandidates(
                    originalPath,
                    candidateDir,
                    Math.Clamp(_config.OcrMaxDeskewDegrees, 0, 20)));

            OcrCandidateSelection selection;
            using (var ocr = new LocalOcrSession(_config.OcrLanguages))
            {
                selection = await Task.Run(() =>
                    OcrCandidateSelector.SelectBest(ocr, candidates));
            }

            OriginalImage.Source = LoadBitmap(originalPath);
            SelectedImage.Source = LoadBitmap(selection.Candidate.ImagePath);
            RawTextBox.Text = selection.RawRecognition.Text;
            SelectedTextBox.Text = selection.Recognition.Text;

            SelectedTitleText.Text = selection.Candidate.Processed
                ? $"Выбран: {selection.Candidate.Name}"
                : "Выбран: Original";

            var delta = selection.Recognition.Confidence - selection.RawRecognition.Confidence;
            DiagnosticsText.Text =
                $"OCR raw {selection.RawRecognition.Confidence:P1} → итог {selection.Recognition.Confidence:P1} " +
                $"({delta:+0.0%;-0.0%;0.0%}) · deskew {selection.Candidate.DeskewDegrees:+0.0;-0.0;0.0}° · " +
                $"оценка {selection.Score:0.000}";

            StatusText.Text = selection.Candidate.Processed
                ? "Восстановленный вариант принят, потому что он достаточно превзошёл оригинал по OCR-оценке."
                : "Оригинал сохранён: preprocessing не дал достаточно убедительного улучшения.";
        }
        catch (Exception ex)
        {
            LogService.Error("Restoration preview", ex);
            StatusText.Text = "Ошибка предпросмотра.";
            MessageBox.Show(ex.Message, "Восстановление скана", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static BitmapImage LoadBitmap(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void Cleanup()
    {
        if (string.IsNullOrWhiteSpace(_tempDir))
            return;

        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (Exception ex)
        {
            LogService.Warn("Restoration preview cleanup: " + ex.Message);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
