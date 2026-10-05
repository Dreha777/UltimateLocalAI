using System.Windows;
using System.Windows.Media.Imaging;
using UltimateLocalAI.Models;
using UltimateLocalAI.Services;

namespace UltimateLocalAI;

public partial class DocumentVisionPreviewWindow : Window
{
    private readonly string _pdfPath;
    private readonly int _pageNumber;
    private readonly string _textContext;
    private readonly AppConfig _config;
    private readonly HardwareInfo _hardware;
    private readonly PdfPageRenderer _renderer = new();
    private string? _tempDir;

    public DocumentVisionPreviewWindow(
        string pdfPath,
        int pageNumber,
        string? textContext,
        AppConfig config,
        HardwareInfo hardware)
    {
        InitializeComponent();
        _pdfPath = pdfPath;
        _pageNumber = pageNumber;
        _textContext = textContext ?? "";
        _config = config;
        _hardware = hardware;

        TitleText.Text = $"{Path.GetFileName(pdfPath)} · стр. {pageNumber}";
        Loaded += async (_, _) => await AnalyzeAsync();
        Closed += (_, _) => Cleanup();
    }

    private async Task AnalyzeAsync()
    {
        StatusText.Text = "Загрузка vision-модели и анализ страницы…";

        try
        {
            _tempDir = Path.Combine(AppPaths.TempDir, "vision-preview-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            var image = await Task.Run(() =>
                _renderer.RenderPageForVision(
                    _pdfPath,
                    _pageNumber,
                    Math.Clamp(_config.DocumentVisionDpi, 140, 320),
                    _tempDir));

            PageImage.Source = LoadBitmap(image);

            await using var session = new DocumentVisionSession(new BackendSelector(), _hardware, _config);
            await session.StartAsync();
            if (!session.IsReady)
                throw new InvalidOperationException("Vision-модель не запущена. Проверьте GGUF и mmproj.");

            var analysis = await session.AnalyzePageAsync(
                image,
                _pageNumber,
                _textContext);

            VisionResult.Text = analysis.HasVisualContent
                ? analysis.Content
                : "На странице не обнаружено значимого нетекстового содержимого.";

            StatusText.Text =
                $"{analysis.ModelName} · {(_config.DocumentVisionUseGpu ? "GPU/Auto" : "CPU")} · " +
                $"{analysis.Status} · DPI {_config.DocumentVisionDpi}";
        }
        catch (Exception ex)
        {
            LogService.Error("Document Vision preview", ex);
            VisionResult.Text = "";
            StatusText.Text = "Ошибка Document Vision.";
            MessageBox.Show(ex.Message, "Document Vision", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            LogService.Warn("Vision preview cleanup: " + ex.Message);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
