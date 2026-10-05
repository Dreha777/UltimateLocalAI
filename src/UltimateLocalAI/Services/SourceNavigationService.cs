using System.Diagnostics;
using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public static class SourceNavigationService
{
    public static void OpenSource(RagSourceCitation source)
    {
        var path = source.SourcePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Исходный файл больше не найден. Возможно, он был перемещён или удалён.", path);

        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
            source.PageFrom is > 0)
        {
            try
            {
                var uri = new Uri(path).AbsoluteUri + $"#page={source.PageFrom.Value}";
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                return;
            }
            catch (Exception ex)
            {
                LogService.Warn("Open PDF page URI failed, opening file instead: " + ex.Message);
            }
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void OpenFolder(RagSourceCitation source)
    {
        var path = source.SourcePath;
        if (string.IsNullOrWhiteSpace(path))
            throw new FileNotFoundException("Путь к источнику отсутствует.");

        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            throw new DirectoryNotFoundException("Папка источника больше не найдена.");

        if (File.Exists(full))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"")
            {
                UseShellExecute = true
            });
        }
        else
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
    }
}
