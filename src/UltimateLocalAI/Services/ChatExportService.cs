using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using MigraDoc.RtfRendering;
using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public static class ChatExportService
{
    public static void ExportPdf(string path, string title, IEnumerable<UiMessage> messages)
    {
        var document = BuildDocument(title, messages);
        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        renderer.Save(path);
    }

    public static void ExportWordRtf(string path, string title, IEnumerable<UiMessage> messages)
    {
        var document = BuildDocument(title, messages);
        var renderer = new RtfDocumentRenderer();
        renderer.Render(document, path, Environment.CurrentDirectory);
    }

    private static Document BuildDocument(string title, IEnumerable<UiMessage> messages)
    {
        var document = new Document();
        document.Info.Title = title;
        document.Info.Author = "Ultimate Local AI — Яхлов Андрей Васильевич";

        var normal = document.Styles[StyleNames.Normal];
        normal.Font.Name = "Segoe UI";
        normal.Font.Size = Unit.FromPoint(10.5);
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(5);

        var section = document.AddSection();
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.6);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.6);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(1.8);
        section.PageSetup.RightMargin = Unit.FromCentimeter(1.8);

        var heading = section.AddParagraph();
        heading.AddFormattedText(title, TextFormat.Bold);
        heading.Format.Font.Size = Unit.FromPoint(18);
        heading.Format.SpaceAfter = Unit.FromPoint(3);

        var meta = section.AddParagraph($"Экспортировано из Ultimate Local AI · {DateTime.Now:dd.MM.yyyy HH:mm}");
        meta.Format.Font.Size = Unit.FromPoint(8.5);
        meta.Format.Font.Color = Colors.Gray;
        meta.Format.SpaceAfter = Unit.FromPoint(14);

        foreach (var message in messages)
        {
            var role = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ? "Вы" : "Ассистент";
            var roleParagraph = section.AddParagraph();
            roleParagraph.AddFormattedText(role, TextFormat.Bold);
            roleParagraph.AddText($"   {message.CreatedAt:dd.MM.yyyy HH:mm}");
            roleParagraph.Format.Font.Size = Unit.FromPoint(9);
            roleParagraph.Format.SpaceBefore = Unit.FromPoint(8);
            roleParagraph.Format.SpaceAfter = Unit.FromPoint(3);

            AddMessage(section, message.Content);

            if (!string.IsNullOrWhiteSpace(message.AttachmentSummary))
            {
                var attachment = section.AddParagraph(message.AttachmentSummary);
                attachment.Format.Font.Size = Unit.FromPoint(8.5);
                attachment.Format.Font.Color = Colors.Gray;
            }
        }

        return document;
    }

    private static void AddMessage(Section section, string text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
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

                var p = section.AddParagraph();
                if (!string.IsNullOrWhiteSpace(language))
                    p.AddFormattedText(language.ToUpperInvariant() + Environment.NewLine, TextFormat.Bold);
                p.AddText(string.Join(Environment.NewLine, code));
                p.Format.Font.Name = "Consolas";
                p.Format.Font.Size = Unit.FromPoint(8.5);
                p.Format.LeftIndent = Unit.FromCentimeter(0.4);
                p.Format.RightIndent = Unit.FromCentimeter(0.4);
                p.Format.SpaceBefore = Unit.FromPoint(4);
                p.Format.SpaceAfter = Unit.FromPoint(7);
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed == "$$" || trimmed == @"\[")
            {
                var end = trimmed == "$$" ? "$$" : @"\]";
                var formula = new List<string>();
                i++;
                while (i < lines.Length && lines[i].Trim() != end)
                    formula.Add(lines[i++]);
                if (i < lines.Length) i++;
                AddFormulaParagraph(section, string.Join(" ", formula));
                continue;
            }
            if ((trimmed.StartsWith("$$") && trimmed.EndsWith("$$") && trimmed.Length > 4) ||
                (trimmed.StartsWith(@"\[") && trimmed.EndsWith(@"\]") && trimmed.Length > 4))
            {
                AddFormulaParagraph(section, trimmed[2..^2].Trim());
                i++;
                continue;
            }

            var pText = StripSimpleMarkdown(line);
            var paragraph = section.AddParagraph(pText);
            if (line.StartsWith("# "))
            {
                paragraph.Format.Font.Bold = true;
                paragraph.Format.Font.Size = Unit.FromPoint(15);
            }
            else if (line.StartsWith("## "))
            {
                paragraph.Format.Font.Bold = true;
                paragraph.Format.Font.Size = Unit.FromPoint(13);
            }
            else if (line.StartsWith("### "))
            {
                paragraph.Format.Font.Bold = true;
                paragraph.Format.Font.Size = Unit.FromPoint(11.5);
            }
            i++;
        }
    }

    private static void AddFormulaParagraph(Section section, string formula)
    {
        var p = section.AddParagraph(formula);
        p.Format.Font.Name = "Cambria Math";
        p.Format.Font.Size = Unit.FromPoint(11);
        p.Format.LeftIndent = Unit.FromCentimeter(0.6);
        p.Format.SpaceBefore = Unit.FromPoint(4);
        p.Format.SpaceAfter = Unit.FromPoint(6);
    }

    private static string StripSimpleMarkdown(string line)
    {
        var text = line;
        if (text.StartsWith("### ")) text = text[4..];
        else if (text.StartsWith("## ")) text = text[3..];
        else if (text.StartsWith("# ")) text = text[2..];
        else if (text.StartsWith("- ") || text.StartsWith("* ")) text = "• " + text[2..];

        return text.Replace("**", "").Replace("`", "");
    }
}
