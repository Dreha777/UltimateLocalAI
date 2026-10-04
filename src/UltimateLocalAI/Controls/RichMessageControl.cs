using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WpfMath.Controls;

namespace UltimateLocalAI.Controls;

public sealed class RichMessageControl : UserControl
{
    private readonly StackPanel _root = new();

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(RichMessageControl),
        new PropertyMetadata("", OnVisualPropertyChanged));

    public static readonly DependencyProperty RoleProperty = DependencyProperty.Register(
        nameof(Role), typeof(string), typeof(RichMessageControl),
        new PropertyMetadata("assistant", OnVisualPropertyChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Role
    {
        get => (string)GetValue(RoleProperty);
        set => SetValue(RoleProperty, value);
    }

    public RichMessageControl()
    {
        Content = _root;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((RichMessageControl)d).Render();

    private void Render()
    {
        _root.Children.Clear();
        var source = (Text ?? "").Replace("\r\n", "\n");
        if (source.Length == 0)
        {
            AddTextLine("");
            return;
        }

        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length;)
        {
            var line = lines[i];

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var language = line.Trim().Length > 3 ? line.Trim()[3..].Trim() : "";
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    code.Add(lines[i++]);
                if (i < lines.Length) i++;
                AddCodeBlock(language, string.Join(Environment.NewLine, code));
                continue;
            }

            if (line.Trim() == "$$")
            {
                var formula = new List<string>();
                i++;
                while (i < lines.Length && lines[i].Trim() != "$$")
                    formula.Add(lines[i++]);
                if (i < lines.Length) i++;
                AddFormula(string.Join(" ", formula), true);
                continue;
            }

            if (line.Trim() == @"\[")
            {
                var formula = new List<string>();
                i++;
                while (i < lines.Length && lines[i].Trim() != @"\]")
                    formula.Add(lines[i++]);
                if (i < lines.Length) i++;
                AddFormula(string.Join(" ", formula), true);
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.StartsWith("$$", StringComparison.Ordinal) && trimmed.EndsWith("$$", StringComparison.Ordinal) && trimmed.Length > 4)
            {
                AddFormula(trimmed[2..^2].Trim(), true);
                i++;
                continue;
            }
            if (trimmed.StartsWith(@"\[", StringComparison.Ordinal) && trimmed.EndsWith(@"\]", StringComparison.Ordinal) && trimmed.Length > 4)
            {
                AddFormula(trimmed[2..^2].Trim(), true);
                i++;
                continue;
            }
            if (trimmed.StartsWith(@"\(", StringComparison.Ordinal) && trimmed.EndsWith(@"\)", StringComparison.Ordinal) && trimmed.Length > 4)
            {
                AddFormula(trimmed[2..^2].Trim(), false);
                i++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                _root.Children.Add(new Border { Height = 6 });
                i++;
                continue;
            }

            AddTextLine(line);
            i++;
        }
    }

    private void AddTextLine(string line)
    {
        var text = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 1, 0, 1)
        };
        SetTextForeground(text);

        var content = line;
        if (content.StartsWith("### ", StringComparison.Ordinal))
        {
            text.FontWeight = FontWeights.SemiBold;
            text.FontSize = 15;
            content = content[4..];
            text.Margin = new Thickness(0, 8, 0, 2);
        }
        else if (content.StartsWith("## ", StringComparison.Ordinal))
        {
            text.FontWeight = FontWeights.SemiBold;
            text.FontSize = 16;
            content = content[3..];
            text.Margin = new Thickness(0, 9, 0, 3);
        }
        else if (content.StartsWith("# ", StringComparison.Ordinal))
        {
            text.FontWeight = FontWeights.Bold;
            text.FontSize = 18;
            content = content[2..];
            text.Margin = new Thickness(0, 10, 0, 4);
        }
        else if (content.StartsWith("- ", StringComparison.Ordinal) || content.StartsWith("* ", StringComparison.Ordinal))
        {
            content = "• " + content[2..];
        }

        AddInlineMarkup(text, content);
        _root.Children.Add(text);
    }

    private void AddInlineMarkup(TextBlock text, string source)
    {
        var tokenRegex = new Regex(@"(\*\*.+?\*\*|`[^`]+`|\\\(.+?\\\)|(?<!\$)\$(?!\$).+?(?<!\$)\$(?!\$))");
        var index = 0;
        foreach (Match match in tokenRegex.Matches(source))
        {
            if (match.Index > index)
                text.Inlines.Add(new Run(source[index..match.Index]));

            var token = match.Value;
            if (token.StartsWith("**", StringComparison.Ordinal) && token.EndsWith("**", StringComparison.Ordinal))
            {
                text.Inlines.Add(new Run(token[2..^2]) { FontWeight = FontWeights.SemiBold });
            }
            else if (token.StartsWith("`", StringComparison.Ordinal) && token.EndsWith("`", StringComparison.Ordinal))
            {
                var run = new Run(token[1..^1])
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 13,
                    Background = new SolidColorBrush(Color.FromArgb(45, 110, 118, 129))
                };
                text.Inlines.Add(run);
            }
            else
            {
                var formulaText = token.StartsWith(@"\(", StringComparison.Ordinal)
                    ? token[2..^2]
                    : token[1..^1];
                var formula = CreateFormulaControl(formulaText, 15);
                if (formula.HasError)
                {
                    text.Inlines.Add(new Run(formulaText) { FontFamily = new FontFamily("Cambria Math") });
                }
                else
                {
                    var inline = new InlineUIContainer(formula)
                    {
                        BaselineAlignment = BaselineAlignment.Center
                    };
                    text.Inlines.Add(inline);
                }
            }
            index = match.Index + match.Length;
        }

        if (index < source.Length)
            text.Inlines.Add(new Run(source[index..]));
    }

    private void AddFormula(string formulaText, bool display)
    {
        var formula = CreateFormulaControl(formulaText, display ? 20 : 17);
        if (formula.HasError)
        {
            var fallback = new TextBlock
            {
                Text = formulaText,
                FontFamily = new FontFamily("Cambria Math"),
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 5, 4, 5)
            };
            SetTextForeground(fallback);
            _root.Children.Add(fallback);
            return;
        }

        var container = new Border
        {
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 5, 0, 5),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = formula
        };
        container.SetResourceReference(Border.BackgroundProperty, "FormulaBackgroundBrush");
        _root.Children.Add(container);
    }

    private FormulaControl CreateFormulaControl(string formulaText, double scale)
    {
        var formula = new FormulaControl
        {
            Formula = formulaText.Trim(),
            Scale = scale,
            SystemTextFontName = "Segoe UI",
            Margin = new Thickness(2, 0, 2, 0)
        };
        if (string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase))
            formula.Foreground = Brushes.White;
        else
            formula.SetResourceReference(ForegroundProperty, "TextBrush");
        TextOptions.SetTextRenderingMode(formula, TextRenderingMode.ClearType);
        TextOptions.SetTextHintingMode(formula, TextHintingMode.Fixed);
        TextOptions.SetTextFormattingMode(formula, TextFormattingMode.Display);
        return formula;
    }

    private void AddCodeBlock(string language, string code)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(9),
            Margin = new Thickness(0, 6, 0, 6),
            Padding = new Thickness(0)
        };
        border.SetResourceReference(Border.BackgroundProperty, "CodeBackgroundBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "CodeBorderBrush");
        border.BorderThickness = new Thickness(1);

        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(10, 6, 8, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lang = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(language) ? "CODE" : language.ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold
        };
        lang.SetResourceReference(TextBlock.ForegroundProperty, "CodeMutedBrush");
        header.Children.Add(lang);

        var copy = new Button
        {
            Content = "Копировать",
            FontSize = 10,
            Padding = new Thickness(8, 3, 8, 3),
            Tag = code,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        copy.SetResourceReference(Button.BackgroundProperty, "CodeButtonBrush");
        copy.SetResourceReference(Button.ForegroundProperty, "CodeTextBrush");
        copy.BorderThickness = new Thickness(0);
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(code); }
            catch { }
        };
        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);
        panel.Children.Add(header);

        var codeText = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Margin = new Thickness(10, 4, 10, 10),
            TextWrapping = TextWrapping.NoWrap
        };
        AddHighlightedCode(codeText, code, language);
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = codeText
        };
        Grid.SetRow(scroll, 1);
        panel.Children.Add(scroll);
        border.Child = panel;
        _root.Children.Add(border);
    }

    private static readonly HashSet<string> CommonKeywords = new(StringComparer.Ordinal)
    {
        "class","public","private","protected","internal","static","void","string","int","long","double","float","decimal",
        "bool","true","false","null","new","return","if","else","for","foreach","while","do","switch","case","break",
        "continue","try","catch","finally","throw","using","namespace","async","await","var","this","base","get","set",
        "def","import","from","as","in","is","None","True","False","lambda","yield","with","pass","elif",
        "function","const","let","export","default","interface","extends","implements","package","struct","enum","record",
        "SELECT","FROM","WHERE","JOIN","INNER","LEFT","RIGHT","INSERT","UPDATE","DELETE","CREATE","TABLE","VALUES","AND","OR"
    };

    private static void AddHighlightedCode(TextBlock target, string code, string language)
    {
        var regex = new Regex("""(?<comment>//[^\r\n]*|#[^\r\n]*|/\*[\s\S]*?\*/)|(?<string>@?"(?:""|\\.|[^"])*"|'(?:\\.|[^'])*')|(?<number>\b\d+(?:\.\d+)?\b)|(?<word>\b[A-Za-z_][A-Za-z0-9_]*\b)""");
        var index = 0;
        foreach (Match match in regex.Matches(code))
        {
            if (match.Index > index)
                AddCodeRun(target, code[index..match.Index], "CodeTextBrush");

            var brush = match.Groups["comment"].Success ? "CodeCommentBrush"
                : match.Groups["string"].Success ? "CodeStringBrush"
                : match.Groups["number"].Success ? "CodeNumberBrush"
                : CommonKeywords.Contains(match.Value) || CommonKeywords.Contains(match.Value.ToUpperInvariant())
                    ? "CodeKeywordBrush"
                    : "CodeTextBrush";
            AddCodeRun(target, match.Value, brush);
            index = match.Index + match.Length;
        }
        if (index < code.Length)
            AddCodeRun(target, code[index..], "CodeTextBrush");
    }

    private static void AddCodeRun(TextBlock target, string text, string brushKey)
    {
        var run = new Run(text);
        run.SetResourceReference(TextElement.ForegroundProperty, brushKey);
        target.Inlines.Add(run);
    }

    private void SetTextForeground(TextBlock text)
    {
        if (string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase))
            text.Foreground = Brushes.White;
        else
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
    }
}
