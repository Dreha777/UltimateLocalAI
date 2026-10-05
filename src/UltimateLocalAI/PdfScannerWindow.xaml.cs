using System.Windows;
using Microsoft.Win32;
using UltimateLocalAI.Models;
using UltimateLocalAI.Services;

namespace UltimateLocalAI;

public partial class PdfScannerWindow : Window
{
    private readonly AppConfig _config;
    private readonly ConfigService _configService;
    private readonly HardwareInfo _hardware;
    private readonly RagDocumentExtractor _extractor = new(new FileTextExtractor());
    private CancellationTokenSource? _cts;

    public PdfScannerWindow(AppConfig config, ConfigService configService, HardwareInfo hardware)
    {
        InitializeComponent();
        _config = config;
        _configService = configService;
        _hardware = hardware;

        OcrEnabledBox.IsChecked = _config.OcrEnabled;
        DpiBox.Text = _config.OcrDpi.ToString();
        LanguagesBox.Text = _config.OcrLanguages;
        ConfidenceBox.Text = _config.OcrMinConfidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        RestorationEnabledBox.IsChecked = _config.OcrRestorationEnabled;
        MaxDeskewBox.Text = _config.OcrMaxDeskewDegrees.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        Closed += (_, _) => _cts?.Cancel();

        if (LocalOcrSession.IsReady(_config.OcrLanguages, out var reason))
            StatusText.Text = "OCR готов: локальные rus/eng traineddata найдены.";
        else
            StatusText.Text = "OCR не готов: " + reason;
    }

    private void SelectPdf_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "PDF (*.pdf)|*.pdf",
            Title = "Выберите PDF для диагностики"
        };

        if (dlg.ShowDialog(this) == true)
            PdfPathBox.Text = dlg.FileName;
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        var path = PdfPathBox.Text.Trim();
        if (!File.Exists(path))
        {
            MessageBox.Show("Сначала выберите существующий PDF.", "Сканер PDF", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SaveSettings();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        AnalyzeButton.IsEnabled = false;
        PagesList.ItemsSource = null;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = true;

        var progress = new Progress<RagIndexProgress>(p =>
        {
            StatusText.Text = p.Message;
            if (p.Total > 0)
            {
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Maximum = p.Total;
                ProgressBar.Value = Math.Clamp(p.Completed, 0, p.Total);
            }
        });

        try
        {
            var result = await _extractor.ExtractAsync(path, _config, progress, null, _cts.Token);
            var report = result.PdfReport ?? throw new InvalidDataException("Не удалось получить отчёт анализа PDF.");

            PagesList.ItemsSource = report.Pages;
            SummaryText.Text =
                $"{report.DocumentMode} · страниц {report.PageCount} · text {report.TextPageCount} · OCR-кандидатов {report.OcrCandidatePageCount} · " +
                $"фактически OCR {report.OcrPageCount}" +
                (report.OcrPageCount > 0 ? $" · средняя confidence {report.OcrAverageConfidence:P0}" : "");

            var restored = report.Pages.Count(x => x.UsedRestoration);
            StatusText.Text = report.Pages.Any(x => x.UsedOcr && x.OcrConfidence < _config.OcrMinConfidence)
                ? $"Анализ завершён · восстановлено страниц: {restored}. Есть страницы с низкой OCR confidence — проверьте их через «До / после»."
                : $"Анализ завершён · восстановлено страниц: {restored}.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Анализ отменён.";
        }
        catch (Exception ex)
        {
            LogService.Error("PDF scanner", ex);
            MessageBox.Show(ex.Message, "Ошибка сканера PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Ошибка анализа.";
        }
        finally
        {
            AnalyzeButton.IsEnabled = true;
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void SaveSettings()
    {
        _config.OcrEnabled = OcrEnabledBox.IsChecked == true;
        _config.OcrDpi = ParseInt(DpiBox.Text, 300, 150, 450);
        _config.OcrLanguages = string.IsNullOrWhiteSpace(LanguagesBox.Text) ? "rus+eng" : LanguagesBox.Text.Trim();
        _config.OcrMinConfidence = ParseDouble(ConfidenceBox.Text, 0.45, 0, 1);
        _config.OcrRestorationEnabled = RestorationEnabledBox.IsChecked == true;
        _config.OcrMaxDeskewDegrees = ParseDouble(MaxDeskewBox.Text, 12.0, 0, 20);
        _configService.Save(_config);
    }

    private void PreviewRestoration_Click(object sender, RoutedEventArgs e)
    {
        if (PagesList.SelectedItem is not PdfPageScanInfo page || !File.Exists(PdfPathBox.Text))
        {
            MessageBox.Show(
                "Сначала выберите проанализированную страницу.",
                "Восстановление скана", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SaveSettings();
        var win = new PdfRestorationPreviewWindow(PdfPathBox.Text, page.PageNumber, _config)
        {
            Owner = this
        };
        win.ShowDialog();
    }

    private void PreviewVision_Click(object sender, RoutedEventArgs e)
    {
        if (PagesList.SelectedItem is not PdfPageScanInfo page || !File.Exists(PdfPathBox.Text))
        {
            MessageBox.Show(
                "Сначала выберите проанализированную страницу.",
                "Document Vision", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SaveSettings();

        if (!_config.DocumentVisionEnabled ||
            !File.Exists(_config.DocumentVisionModelPath) ||
            !File.Exists(_config.DocumentVisionMmprojPath))
        {
            MessageBox.Show(
                "Сначала настройте Vision GGUF и соответствующий mmproj в окне «База знаний».",
                "Document Vision", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var win = new DocumentVisionPreviewWindow(
            PdfPathBox.Text,
            page.PageNumber,
            page.ExtractedText,
            _config,
            _hardware)
        {
            Owner = this
        };
        win.ShowDialog();
    }

    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        if (PagesList.SelectedItem is not PdfPageScanInfo page || !File.Exists(PdfPathBox.Text))
            return;

        try
        {
            SourceNavigationService.OpenSource(new RagSourceCitation
            {
                Number = page.PageNumber,
                SourcePath = PdfPathBox.Text,
                DisplayName = Path.GetFileName(PdfPathBox.Text),
                PageFrom = page.PageNumber,
                PageTo = page.PageNumber
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Страница недоступна", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        Close();
    }

    private static int ParseInt(string? text, int fallback, int min, int max) =>
        int.TryParse(text, out var value) ? Math.Clamp(value, min, max) : fallback;

    private static double ParseDouble(string? text, double fallback, double min, double max)
    {
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ||
            double.TryParse(text, out value))
            return Math.Clamp(value, min, max);
        return fallback;
    }
}
