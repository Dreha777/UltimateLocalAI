using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using UltimateLocalAI.Models;
using UltimateLocalAI.Services;

namespace UltimateLocalAI;

public partial class ModelManagerWindow : Window
{
    private readonly HardwareInfo _hardware;
    private readonly ModelCatalogService _catalog = new();
    private readonly string _currentModelPath;
    private CancellationTokenSource? _scanCts;

    public ObservableCollection<string> ModelFolders { get; } = [];
    public ObservableCollection<ModelCatalogItem> Models { get; } = [];

    public string? SelectedModelPath { get; private set; }
    public string SelectedAutoProfile { get; private set; } = "Качество";

    public ModelManagerWindow(IEnumerable<string> folders, HardwareInfo hardware, string currentModelPath, string autoProfile)
    {
        InitializeComponent();
        DataContext = this;

        _hardware = hardware;
        _currentModelPath = currentModelPath ?? "";

        foreach (var folder in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            ModelFolders.Add(folder);

        if (File.Exists(_currentModelPath))
        {
            var currentFolder = Path.GetDirectoryName(_currentModelPath);
            if (!string.IsNullOrWhiteSpace(currentFolder) &&
                !ModelFolders.Contains(currentFolder, StringComparer.OrdinalIgnoreCase))
            {
                ModelFolders.Add(currentFolder);
            }
        }

        if (ModelFolders.Count > 0)
            FoldersBox.SelectedIndex = 0;

        SelectProfile(string.IsNullOrWhiteSpace(autoProfile) ? "Качество" : autoProfile);
        Loaded += async (_, _) => await ScanAsync();
        Closed += (_, _) => _scanCts?.Cancel();
    }

    private async Task ScanAsync()
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();

        Models.Clear();
        UseButton.IsEnabled = false;

        if (ModelFolders.Count == 0)
        {
            StatusText.Text = "Добавьте папку, где хранятся GGUF-модели.";
            AdviceText.Text = "Каталог пуст. Файлы моделей никуда не копируются — программа только читает их метаданные.";
            return;
        }

        try
        {
            StatusText.Text = "Сканирование GGUF…";
            var result = await _catalog.ScanAsync(ModelFolders, _hardware, _currentModelPath, _scanCts.Token);
            foreach (var item in result)
                Models.Add(item);

            StatusText.Text = result.Count == 0
                ? "GGUF-файлы в выбранных папках не найдены."
                : $"Найдено моделей: {result.Count}";

            var current = Models.FirstOrDefault(x => x.IsCurrent);
            if (current is not null)
                ModelsList.SelectedItem = current;
            else if (Models.Count > 0)
                ModelsList.SelectedIndex = 0;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Сканирование отменено.";
        }
        catch (Exception ex)
        {
            LogService.Error("Model catalog scan failed", ex);
            StatusText.Text = "Ошибка сканирования.";
            MessageBox.Show(ex.Message, "Менеджер моделей", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку с GGUF-моделями",
            Multiselect = true
        };

        if (ModelFolders.Count > 0 && Directory.Exists(ModelFolders[0]))
            dialog.InitialDirectory = ModelFolders[0];

        if (dialog.ShowDialog(this) != true)
            return;

        foreach (var folder in dialog.FolderNames)
        {
            var full = Path.GetFullPath(folder);
            if (!ModelFolders.Contains(full, StringComparer.OrdinalIgnoreCase))
                ModelFolders.Add(full);
        }

        if (FoldersBox.SelectedIndex < 0 && ModelFolders.Count > 0)
            FoldersBox.SelectedIndex = 0;

        await ScanAsync();
    }

    private async void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FoldersBox.SelectedItem is not string folder)
            return;

        ModelFolders.Remove(folder);
        if (ModelFolders.Count > 0)
            FoldersBox.SelectedIndex = 0;

        await ScanAsync();
    }

    private void ModelsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelsList.SelectedItem is not ModelCatalogItem item)
        {
            UseButton.IsEnabled = false;
            AdviceText.Text = "Выберите модель в списке.";
            return;
        }

        UseButton.IsEnabled = true;
        var current = item.IsCurrent ? " Сейчас выбрана в программе." : "";
        AdviceText.Text = item.Advice + current;
    }

    private void ModelsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ModelsList.SelectedItem is ModelCatalogItem)
            UseSelected();
    }

    private void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded && ProfileBox.SelectedItem is null) return;
        SelectedAutoProfile = (ProfileBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Качество";
        UpdateProfileHint();
    }

    private void SelectProfile(string profile)
    {
        foreach (var item in ProfileBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), profile, StringComparison.OrdinalIgnoreCase))
            {
                ProfileBox.SelectedItem = item;
                SelectedAutoProfile = item.Content?.ToString() ?? "Качество";
                UpdateProfileHint();
                return;
            }
        }

        ProfileBox.SelectedIndex = 0;
        SelectedAutoProfile = "Качество";
        UpdateProfileHint();
    }

    private void UpdateProfileHint()
    {
        ProfileHintText.Text = SelectedAutoProfile switch
        {
            "Баланс" => "Context обычно 4096–8192, KV q8_0, умеренный batch. Хороший универсальный режим.",
            "Экономия" => "Context 2048–4096 и меньший batch. Для слабых ПК; качество сохраняется настолько, насколько позволяет модель.",
            _ => "Приоритет качества: большой context при наличии памяти, F16 KV при достаточном запасе RAM/VRAM и больше места для длинного ответа."
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SelectedAutoProfile = (ProfileBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Качество";
        SelectedModelPath = null;
        DialogResult = true;
    }

    private void UseSelected_Click(object sender, RoutedEventArgs e) => UseSelected();

    private void UseSelected()
    {
        if (ModelsList.SelectedItem is not ModelCatalogItem item)
            return;

        if (!File.Exists(item.FullPath))
        {
            MessageBox.Show("Файл модели больше не существует. Обновите список.", "Менеджер моделей",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedAutoProfile = (ProfileBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Качество";
        SelectedModelPath = item.FullPath;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
