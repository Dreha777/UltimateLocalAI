using System.Windows;
using Microsoft.Win32;
using UltimateLocalAI.Models;
using UltimateLocalAI.Services;

namespace UltimateLocalAI;

public partial class KnowledgeBaseWindow : Window
{
    private readonly KnowledgeBaseService _legacyService;
    private readonly RagVectorIndexService _ragService;
    private readonly AppConfig _config;
    private readonly ConfigService _configService;
    private readonly HardwareInfo _hardware;
    private CancellationTokenSource? _indexCts;

    public KnowledgeBaseWindow(
        KnowledgeBaseService legacyService,
        RagVectorIndexService ragService,
        AppConfig config,
        ConfigService configService,
        HardwareInfo hardware)
    {
        InitializeComponent();
        _legacyService = legacyService;
        _ragService = ragService;
        _config = config;
        _configService = configService;
        _hardware = hardware;

        EmbeddingModelPathBox.Text = _config.EmbeddingModelPath;
        DocumentPrefixBox.Text = _config.EmbeddingDocumentPrefix;
        QueryPrefixBox.Text = _config.EmbeddingQueryPrefix;
        SelectComboByText(PoolingBox, _config.EmbeddingPooling);
        RerankerModelPathBox.Text = _config.RerankerModelPath;
        UseRerankerBox.IsChecked = _config.UseReranker;
        CandidateTopKBox.Text = _config.RagCandidateTopK.ToString();
        FinalTopKBox.Text = _config.RagFinalTopK.ToString();
        OcrEnabledBox.IsChecked = _config.OcrEnabled;
        OcrDpiBox.Text = _config.OcrDpi.ToString();
        OcrLanguagesBox.Text = _config.OcrLanguages;
        OcrConfidenceBox.Text = _config.OcrMinConfidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        OcrRestorationBox.IsChecked = _config.OcrRestorationEnabled;
        OcrMaxDeskewBox.Text = _config.OcrMaxDeskewDegrees.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        Loaded += (_, _) => Refresh();
        Closing += (_, _) =>
        {
            try { SaveEmbeddingSettings(); } catch { }
        };
        Closed += (_, _) => _indexCts?.Cancel();
    }

    private void Refresh()
    {
        DocsList.ItemsSource = _ragService.GetDocuments();
        var manifest = _ragService.GetManifest();

        var hasIndex = manifest.Documents.Count > 0;
        var model = string.IsNullOrWhiteSpace(manifest.EmbeddingModelName)
            ? "embedding-модель ещё не зафиксирована"
            : manifest.EmbeddingModelName;
        var dims = manifest.VectorDimensions > 0 ? $" · {manifest.VectorDimensions} dim" : "";
        IndexInfoText.Text = $"Документов: {manifest.Documents.Count} · embedding индекса: {model}{dims}";

        if (hasIndex)
        {
            EmbeddingModelPathBox.Text = manifest.EmbeddingModelPath;
            DocumentPrefixBox.Text = manifest.EmbeddingDocumentPrefix;
            SelectComboByText(PoolingBox, manifest.EmbeddingPooling);
        }

        SelectEmbeddingButton.IsEnabled = !hasIndex;
        DocumentPrefixBox.IsEnabled = !hasIndex;
        PoolingBox.IsEnabled = !hasIndex;
        if (LocalOcrSession.IsReady(_config.OcrLanguages, out var ocrReason))
            OcrStatusText.Text = "OCR готов";
        else
            OcrStatusText.Text = "OCR не готов: " + ocrReason;

        var chatName = File.Exists(_config.ModelPath) ? Path.GetFileName(_config.ModelPath) : "не выбрана";
        ModelIndependenceText.Text = manifest.Documents.Count == 0
            ? $"Chat-модель: {chatName}. Индекс ещё пуст. После первой индексации embedding-модель фиксирует векторное пространство библиотеки."
            : $"Chat-модель: {chatName} — её можно менять свободно. Библиотека создана embedding-моделью «{manifest.EmbeddingModelName}» и остаётся с ней. Reranker тоже можно менять без переиндексации. Только смена embedding-модели, document prefix или pooling требует перестроить индекс.";
    }

    private void SaveEmbeddingSettings()
    {
        _config.EmbeddingModelPath = EmbeddingModelPathBox.Text.Trim();
        _config.EmbeddingDocumentPrefix = DocumentPrefixBox.Text;
        _config.EmbeddingQueryPrefix = QueryPrefixBox.Text;
        _config.EmbeddingPooling = (PoolingBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Auto";
        _config.RerankerModelPath = RerankerModelPathBox.Text.Trim();
        _config.UseReranker = UseRerankerBox.IsChecked == true;
        _config.RagCandidateTopK = ParseInt(CandidateTopKBox.Text, 24, 4, 100);
        _config.RagFinalTopK = ParseInt(FinalTopKBox.Text, 6, 1, Math.Min(20, _config.RagCandidateTopK));
        _config.OcrEnabled = OcrEnabledBox.IsChecked == true;
        _config.OcrDpi = ParseInt(OcrDpiBox.Text, 300, 150, 450);
        _config.OcrLanguages = string.IsNullOrWhiteSpace(OcrLanguagesBox.Text) ? "rus+eng" : OcrLanguagesBox.Text.Trim();
        _config.OcrMinConfidence = ParseDouble(OcrConfidenceBox.Text, 0.45, 0, 1);
        _config.OcrRestorationEnabled = OcrRestorationBox.IsChecked == true;
        _config.OcrMaxDeskewDegrees = ParseDouble(OcrMaxDeskewBox.Text, 12.0, 0, 20);
        _configService.Save(_config);
    }

    private void SelectEmbeddingModel_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "GGUF embedding-модель (*.gguf)|*.gguf|Все файлы (*.*)|*.*",
            Title = "Выберите отдельную embedding-модель GGUF"
        };

        if (File.Exists(_config.EmbeddingModelPath))
            dlg.InitialDirectory = Path.GetDirectoryName(_config.EmbeddingModelPath);
        else if (File.Exists(_config.ModelPath))
            dlg.InitialDirectory = Path.GetDirectoryName(_config.ModelPath);

        if (dlg.ShowDialog(this) != true)
            return;

        var manifest = _ragService.GetManifest();
        if (manifest.Documents.Count > 0)
        {
            MessageBox.Show(
                "Embedding-модель уже зафиксирована существующим RAG-индексом. Chat-модель и reranker можно менять свободно. Для смены embedding-модели сначала очистите RAG-индекс, затем выберите новую модель и переиндексируйте документы.",
                "RAG: embedding-модель зафиксирована", MessageBoxButton.OK, MessageBoxImage.Information);
            Refresh();
            return;
        }

        EmbeddingModelPathBox.Text = dlg.FileName;
        SaveEmbeddingSettings();

        var meta = GgufMetadataReader.Read(dlg.FileName);
        StatusText.Text = meta.IsValid
            ? $"Embedding GGUF выбрана: {Path.GetFileName(dlg.FileName)} · {meta.Architecture} · {meta.Quantization}"
            : $"Embedding GGUF выбрана: {Path.GetFileName(dlg.FileName)}";
    }

    private void SelectRerankerModel_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "GGUF reranker-модель (*.gguf)|*.gguf|Все файлы (*.*)|*.*",
            Title = "Выберите GGUF reranker-модель"
        };

        if (File.Exists(_config.RerankerModelPath))
            dlg.InitialDirectory = Path.GetDirectoryName(_config.RerankerModelPath);
        else if (File.Exists(_config.EmbeddingModelPath))
            dlg.InitialDirectory = Path.GetDirectoryName(_config.EmbeddingModelPath);

        if (dlg.ShowDialog(this) != true)
            return;

        RerankerModelPathBox.Text = dlg.FileName;
        UseRerankerBox.IsChecked = true;
        SaveEmbeddingSettings();
        StatusText.Text = $"Reranker выбран: {Path.GetFileName(dlg.FileName)}. Его можно менять без перестройки RAG-индекса.";
    }

    private void PdfScanner_Click(object sender, RoutedEventArgs e)
    {
        SaveEmbeddingSettings();
        var win = new PdfScannerWindow(_config, _configService) { Owner = this };
        win.ShowDialog();

        OcrEnabledBox.IsChecked = _config.OcrEnabled;
        OcrDpiBox.Text = _config.OcrDpi.ToString();
        OcrLanguagesBox.Text = _config.OcrLanguages;
        OcrConfidenceBox.Text = _config.OcrMinConfidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        OcrRestorationBox.IsChecked = _config.OcrRestorationEnabled;
        OcrMaxDeskewBox.Text = _config.OcrMaxDeskewDegrees.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        Refresh();
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        SaveEmbeddingSettings();

        if (string.IsNullOrWhiteSpace(_config.EmbeddingModelPath) || !File.Exists(_config.EmbeddingModelPath))
        {
            MessageBox.Show(
                "Сначала выберите отдельную GGUF embedding-модель. Обычная chat/instruct-модель для качественного semantic index обычно не подходит.",
                "RAG 2.0", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Документы и код|*.pdf;*.docx;*.xlsx;*.txt;*.md;*.csv;*.json;*.xml;*.yaml;*.yml;*.cs;*.cpp;*.c;*.h;*.hpp;*.py;*.js;*.ts;*.html;*.css;*.sql;*.java;*.go;*.rs;*.ps1;*.log|Все файлы|*.*",
            Title = "Добавить документы в RAG-индекс"
        };
        if (dlg.ShowDialog(this) != true) return;

        SetBusy(true);
        _indexCts?.Cancel();
        _indexCts?.Dispose();
        _indexCts = new CancellationTokenSource();

        var progress = new Progress<RagIndexProgress>(p =>
        {
            StatusText.Text = string.IsNullOrWhiteSpace(p.FileName)
                ? p.Message
                : $"{p.FileName}: {p.Message}";

            ProgressBar.Visibility = Visibility.Visible;
            if (p.Total > 0)
                ProgressBar.Value = Math.Clamp(p.Completed * 100.0 / p.Total, 0, 100);
            else
                ProgressBar.IsIndeterminate = true;
        });

        try
        {
            await _ragService.IndexFilesAsync(dlg.FileNames, _config, _hardware, progress, _indexCts.Token);

            // Vector RAG is now authoritative for PDF, especially for OCR pages.
            // The legacy lexical parser is kept only for non-PDF fallback so a scanned
            // PDF can never pollute fallback search with a parser warning or binary-like text.
            foreach (var file in dlg.FileNames)
            {
                _indexCts.Token.ThrowIfCancellationRequested();

                if (Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    var legacyPdf = _legacyService.GetDocuments()
                        .FirstOrDefault(x => string.Equals(x.SourcePath, Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase));
                    if (legacyPdf is not null)
                        _legacyService.RemoveDocument(legacyPdf.Id);
                    continue;
                }

                await _legacyService.IndexFileAsync(file);
            }

            StatusText.Text = "Векторный индекс построен. Semantic retrieval активен при включённой базе знаний.";
            Refresh();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Индексация остановлена.";
        }
        catch (Exception ex)
        {
            LogService.Error("RAG vector index", ex);
            MessageBox.Show(ex.Message, "Ошибка RAG-индексации", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Ошибка индексации.";
        }
        finally
        {
            SetBusy(false);
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (DocsList.SelectedItem is not KnowledgeDocument doc) return;

        await _ragService.RemoveDocumentAsync(doc.Id);

        var legacy = _legacyService.GetDocuments()
            .FirstOrDefault(x => string.Equals(x.SourcePath, doc.SourcePath, StringComparison.OrdinalIgnoreCase));
        if (legacy is not null)
            _legacyService.RemoveDocument(legacy.Id);

        Refresh();
        StatusText.Text = "Документ удалён из индексов.";
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Очистить весь новый RAG-индекс? Документы на диске удалены не будут. Для совместимости будет очищен и старый текстовый индекс.",
                "Очистка RAG", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        SetBusy(true);
        try
        {
            await _ragService.ClearAsync();
            foreach (var doc in _legacyService.GetDocuments())
                _legacyService.RemoveDocument(doc.Id);

            Refresh();
            StatusText.Text = "RAG-индекс очищен. Теперь можно выбрать другую embedding-модель.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static int ParseInt(string? text, int fallback, int min, int max) =>
        int.TryParse(text, out var value) ? Math.Clamp(value, min, max) : Math.Clamp(fallback, min, max);

    private static double ParseDouble(string? text, double fallback, double min, double max)
    {
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ||
            double.TryParse(text, out value))
            return Math.Clamp(value, min, max);
        return fallback;
    }

    private static string SafeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private static void SelectComboByText(System.Windows.Controls.ComboBox box, string? text)
    {
        foreach (var item in box.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    private void SetBusy(bool busy)
    {
        AddButton.IsEnabled = !busy;
        RemoveButton.IsEnabled = !busy;
        ClearButton.IsEnabled = !busy;
        PdfScannerButton.IsEnabled = !busy;
    }
}
