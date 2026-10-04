using System.Diagnostics;
using System.Windows;

namespace UltimateLocalAI;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    private void OpenGuide_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "USER_GUIDE_RU.txt");
        if (!File.Exists(path))
        {
            MessageBox.Show(
                "Файл USER_GUIDE_RU.txt не найден рядом с программой.",
                "Инструкция",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Не удалось открыть инструкцию", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
