using System.Windows;
using Microsoft.Win32;
using UltimateLocalAI.Models;
using UltimateLocalAI.Services;

namespace UltimateLocalAI;

public partial class KnowledgeBaseWindow : Window
{
    private readonly KnowledgeBaseService _service;
    public KnowledgeBaseWindow(KnowledgeBaseService service)
    {
        InitializeComponent(); _service = service; Loaded += (_, _) => Refresh();
    }
    private void Refresh() => DocsList.ItemsSource = _service.GetDocuments();
    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = "Документы и код|*.pdf;*.docx;*.xlsx;*.txt;*.md;*.csv;*.json;*.xml;*.yaml;*.yml;*.cs;*.cpp;*.c;*.h;*.hpp;*.py;*.js;*.ts;*.html;*.css;*.sql;*.java;*.go;*.rs;*.ps1;*.log|Все файлы|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var file in dlg.FileNames)
        {
            try { StatusText.Text = "Индексация: " + Path.GetFileName(file); await _service.IndexFileAsync(file); }
            catch (Exception ex) { LogService.Error("KB index", ex); MessageBox.Show($"{Path.GetFileName(file)}: {ex.Message}", "Ошибка индексации", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        StatusText.Text = "Готово"; Refresh();
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (DocsList.SelectedItem is not KnowledgeDocument doc) return;
        _service.RemoveDocument(doc.Id); Refresh(); StatusText.Text = "Удалено из индекса";
    }
}
