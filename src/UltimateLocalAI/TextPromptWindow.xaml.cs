using System.Windows;

namespace UltimateLocalAI;

public partial class TextPromptWindow : Window
{
    public string ResultText => ValueBox.Text;
    public TextPromptWindow(string title, string description, string initial)
    {
        InitializeComponent();
        Title = title; HeaderText.Text = title; DescriptionText.Text = description; ValueBox.Text = initial;
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }
    private void Save_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
}
