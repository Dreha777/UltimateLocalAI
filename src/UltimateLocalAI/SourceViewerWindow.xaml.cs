using System.Windows;
using UltimateLocalAI.Models;
using UltimateLocalAI.Services;

namespace UltimateLocalAI;

public partial class SourceViewerWindow : Window
{
    private readonly RagSourceCitation _source;

    public SourceViewerWindow(RagSourceCitation source)
    {
        InitializeComponent();
        _source = source;

        TitleText.Text = $"Источник {source.Number}: {source.DisplayName}";
        LocationText.Text = string.IsNullOrWhiteSpace(source.LocationLabel)
            ? "Местоположение внутри документа не определено"
            : source.LocationLabel;
        PathText.Text = source.SourcePath;
        SnippetBox.Text = source.Snippet;

        var semantic = double.IsFinite(source.SemanticScore)
            ? $"semantic={source.SemanticScore:0.0000}"
            : "semantic=—";
        var rerank = source.RerankScore.HasValue && double.IsFinite(source.RerankScore.Value)
            ? $" · rerank={source.RerankScore.Value:0.0000}"
            : "";
        DiagnosticsText.Text = $"Поиск: {source.RetrievalMethod} · {semantic}{rerank}";
    }

    private void OpenSource_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SourceNavigationService.OpenSource(_source);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Источник недоступен", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SourceNavigationService.OpenFolder(_source);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Папка недоступна", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
