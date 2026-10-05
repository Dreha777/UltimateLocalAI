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

        Loaded += (_, _) => Refresh();
        Closed += (_, _) => _indexCts?.Cancel();
    }

    private void Refresh()
    {
        DocsList.ItemsSource = _ragService.GetDocuments();
        var manifest = _ragService.GetManifest();

        var model = string.IsNullOrWhiteSpace(manifest.EmbeddingModelName)
            ? "embedding-модель ещё не зафиксирована"
            : manifest.EmbeddingModelName;
        var dims = manifest.VectorDimensions > 0 ? $" · {manifest.VectorDimensions} dim" : "";
        IndexInfoText.Text = $"Документов: {manifest.Documents.Count} · {model}{dims}";
    }

    private void SaveEmbeddingSettings()
    {
        _config.EmbeddingModelPath = EmbeddingModelPathBox.Text.Trim();
        _config.EmbeddingDocumentPrefix = DocumentPrefixBox.Text;
        _config.EmbeddingQueryPrefix = QueryPrefixBox.Text;
        _config.EmbeddingPooling = (PoolingBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Auto";
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

        EmbeddingModelPathBox.Text = dlg.FileName;
        SaveEmbeddingSettings();

        var meta = GgufMetadataReader.Read(dlg.FileName);
        StatusText.Text = meta.IsValid
            ? $"Embedding GGUF выбрана: {Path.GetFileName(dlg.FileName)} · {meta.Architecture} · {meta.Quantization}"
            : $"Embedding GGUF выбрана: {Path.GetFileName(dlg.FileName)}";
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

            // Until Stage 7G-2 switches chat retrieval to vectors, keep the old lexical
            // index synchronized so existing "Использовать базу знаний" keeps working.
            foreach (var file in dlg.FileNames)
            {
                _indexCts.Token.ThrowIfCancellationRequested();
                await _legacyService.IndexFileAsync(file);
            }

            StatusText.Text = "Векторный индекс построен. Semantic retrieval будет подключён в Stage 7G-2.";
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
    }
}
