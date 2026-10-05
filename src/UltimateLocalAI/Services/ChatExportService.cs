using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MigraDoc;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using MigraDoc.RtfRendering;
using PdfSharp.Fonts;
using UltimateLocalAI.Models;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace UltimateLocalAI.Services;

public static class ChatExportService
{
    static ChatExportService()
    {
        // PDFsharp 6.2 changed font resolution. Keep the WPF platform resolver first,
        // but provide a Windows fallback so PDF export does not fail on a missing
        // document/error/code font.
        try
        {
            if (GlobalFontSettings.FallbackFontResolver is null)
                GlobalFontSettings.FallbackFontResolver = new WindowsFallbackFontResolver();

            PredefinedFontsAndChars.ErrorFontName = "Arial";
            PredefinedFontsAndChars.Bullets.Level1FontName = "Arial";
            PredefinedFontsAndChars.Bullets.Level2FontName = "Arial";
            PredefinedFontsAndChars.Bullets.Level3FontName = "Arial";
        }
        catch (InvalidOperationException ex)
        {
            // Font management may already be initialized by another PDFsharp call.
            // The WPF package still has its platform resolver, so export can continue.
            LogService.Warn("PDF font resolver already initialized: " + ex.Message);
        }
    }

    public static void ExportByExtension(string path, string title, IEnumerable<UiMessage> messages)
    {
        var snapshot = messages.ToList();
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".docx":
                ExportDocx(path, title, snapshot);
                break;
            case ".pdf":
                ExportPdf(path, title, snapshot);
                break;
            case ".rtf":
                ExportWordRtf(path, title, snapshot);
                break;
            case ".txt":
                ExportText(path, title, snapshot);
                break;
            case ".md":
            case ".markdown":
                ExportMarkdown(path, title, snapshot);
                break;
            case ".html":
            case ".htm":
                ExportHtml(path, title, snapshot);
                break;
            case ".json":
                ExportJson(path, title, snapshot);
                break;
            default:
                throw new NotSupportedException($"Формат «{Path.GetExtension(path)}» не поддерживается.");
        }
    }

    public static void ExportPdf(string path, string title, IEnumerable<UiMessage> messages)
    {
        var document = BuildMigraDocument(title, messages, pdfSafe: true);
        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        renderer.Save(path);
    }

    public static void ExportWordRtf(string path, string title, IEnumerable<UiMessage> messages)
    {
        var document = BuildMigraDocument(title, messages, pdfSafe: false);
        var renderer = new RtfDocumentRenderer();
        renderer.Render(document, path, Environment.CurrentDirectory);
    }

    public static void ExportDocx(string path, string title, IEnumerable<UiMessage> messages)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        var body = new W.Body();
        var wordDocument = new W.Document(body);
        mainPart.Document = wordDocument;

        AddWordParagraph(body, title, 34, bold: true, font: "Arial");
        AddWordParagraph(body, $"Экспортировано из Ultimate Local AI · {DateTime.Now:dd.MM.yyyy HH:mm}", 17, font: "Arial");

        foreach (var message in messages)
        {
            var role = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ? "Вы" : "Ассистент";
            AddWordParagraph(body, $"{role}   {message.CreatedAt:dd.MM.yyyy HH:mm}", 18, bold: true, font: "Arial");
            AddWordMessage(body, message.Content);

            if (!string.IsNullOrWhiteSpace(message.AttachmentSummary))
                AddWordParagraph(body, message.AttachmentSummary, 17, font: "Arial");
        }

        wordDocument.Save();
    }

    public static void ExportText(string path, string title, IEnumerable<UiMessage> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine(title);
        sb.AppendLine($"Экспортировано из Ultimate Local AI · {DateTime.Now:dd.MM.yyyy HH:mm}");
        sb.AppendLine(new string('=', Math.Min(80, Math.Max(8, title.Length))));

        foreach (var message in messages)
        {
            var role = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ? "Вы" : "Ассистент";
            sb.AppendLine().AppendLine($"[{role} · {message.CreatedAt:dd.MM.yyyy HH:mm}]");
            sb.AppendLine(message.Content ?? "");
            if (!string.IsNullOrWhiteSpace(message.AttachmentSummary))
                sb.AppendLine(message.AttachmentSummary);
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static void ExportMarkdown(string path, string title, IEnumerable<UiMessage> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# " + title).AppendLine();
        sb.AppendLine($"_Экспортировано из Ultimate Local AI · {DateTime.Now:dd.MM.yyyy HH:mm}_").AppendLine();

        foreach (var message in messages)
        {
            var role = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ? "Вы" : "Ассистент";
            sb.AppendLine($"## {role} · {message.CreatedAt:dd.MM.yyyy HH:mm}").AppendLine();
            sb.AppendLine(message.Content ?? "").AppendLine();
            if (!string.IsNullOrWhiteSpace(message.AttachmentSummary))
                sb.AppendLine("> " + message.AttachmentSummary).AppendLine();
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static void ExportHtml(string path, string title, IEnumerable<UiMessage> messages)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>").Append(E(title)).AppendLine("</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;max-width:980px;margin:40px auto;padding:0 24px;line-height:1.55;color:#1d1d1f}h1{margin-bottom:4px}.meta{color:#6e6e73;margin-bottom:28px}.msg{margin:22px 0;padding:16px 18px;border:1px solid #ddd;border-radius:12px}.role{font-weight:700;margin-bottom:10px}.body{white-space:pre-wrap}.attachments{color:#6e6e73;margin-top:10px}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#111827;color:#e5e7eb;padding:14px;border-radius:10px;font-family:Consolas,monospace}</style></head><body>");
        sb.Append("<h1>").Append(E(title)).AppendLine("</h1>");
        sb.Append("<div class=\"meta\">Экспортировано из Ultimate Local AI · ").Append(DateTime.Now.ToString("dd.MM.yyyy HH:mm")).AppendLine("</div>");

        foreach (var message in messages)
        {
            var role = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ? "Вы" : "Ассистент";
            sb.AppendLine("<section class=\"msg\">");
            sb.Append("<div class=\"role\">").Append(E(role)).Append(" · ").Append(message.CreatedAt.ToString("dd.MM.yyyy HH:mm")).AppendLine("</div>");
            AppendHtmlMessage(sb, message.Content);
            if (!string.IsNullOrWhiteSpace(message.AttachmentSummary))
                sb.Append("<div class=\"attachments\">").Append(E(message.AttachmentSummary)).AppendLine("</div>");
            sb.AppendLine("</section>");
        }

        sb.AppendLine("</body></html>");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static void ExportJson(string path, string title, IEnumerable<UiMessage> messages)
    {
        var payload = new
        {
            format = "UltimateLocalAI.ChatExport",
            version = 1,
            title,
            exportedAt = DateTime.Now,
            messages = messages.Select(m => new
            {
                role = m.Role,
                content = m.Content,
                attachmentSummary = m.AttachmentSummary,
                createdAt = m.CreatedAt
            }).ToArray()
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static Document BuildMigraDocument(string title, IEnumerable<UiMessage> messages, bool pdfSafe)
    {
        var document = new Document();
        document.Info.Title = title;
        document.Info.Author = "Ultimate Local AI — Яхлов Андрей Васильевич";

        var normal = document.Styles[StyleNames.Normal];
        normal.Font.Name = "Arial";
        normal.Font.Size = Unit.FromPoint(10.5);
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(5);

        var section = document.AddSection();
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.6);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.6);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(1.8);
        section.PageSetup.RightMargin = Unit.FromCentimeter(1.8);

        var heading = section.AddParagraph();
        heading.AddFormattedText(NormalizeExportText(title, pdfSafe), TextFormat.Bold);
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

            AddMigraMessage(section, message.Content, pdfSafe);

            if (!string.IsNullOrWhiteSpace(message.AttachmentSummary))
            {
                var attachment = section.AddParagraph(NormalizeExportText(message.AttachmentSummary, pdfSafe));
                attachment.Format.Font.Size = Unit.FromPoint(8.5);
                attachment.Format.Font.Color = Colors.Gray;
            }
        }

        return document;
    }

    private static void AddMigraMessage(Section section, string text, bool pdfSafe)
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
                p.AddText(NormalizeExportText(string.Join(Environment.NewLine, code), pdfSafe));
                p.Format.Font.Name = "Courier New";
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
                AddFormulaParagraph(section, string.Join(" ", formula), pdfSafe);
                continue;
            }

            if ((trimmed.StartsWith("$$") && trimmed.EndsWith("$$") && trimmed.Length > 4) ||
                (trimmed.StartsWith(@"\[") && trimmed.EndsWith(@"\]") && trimmed.Length > 4))
            {
                AddFormulaParagraph(section, trimmed[2..^2].Trim(), pdfSafe);
                i++;
                continue;
            }

            var paragraph = section.AddParagraph(NormalizeExportText(StripSimpleMarkdown(line), pdfSafe));
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

    private static void AddFormulaParagraph(Section section, string formula, bool pdfSafe)
    {
        var p = section.AddParagraph(NormalizeExportText(formula, pdfSafe));
        p.Format.Font.Name = "Arial";
        p.Format.Font.Size = Unit.FromPoint(11);
        p.Format.LeftIndent = Unit.FromCentimeter(0.6);
        p.Format.SpaceBefore = Unit.FromPoint(4);
        p.Format.SpaceAfter = Unit.FromPoint(6);
    }

    private static void AddWordMessage(W.Body body, string text)
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

                var prefix = string.IsNullOrWhiteSpace(language) ? "" : language.ToUpperInvariant() + Environment.NewLine;
                AddWordParagraph(body, prefix + string.Join(Environment.NewLine, code), 17, font: "Consolas");
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
                AddWordParagraph(body, string.Join(" ", formula), 20, font: "Cambria Math");
                continue;
            }

            var fontSize = line.StartsWith("# ") ? 30 : line.StartsWith("## ") ? 26 : line.StartsWith("### ") ? 23 : 21;
            var bold = line.StartsWith("# ");
            AddWordParagraph(body, StripSimpleMarkdown(line), fontSize, bold, "Arial");
            i++;
        }
    }

    private static void AddWordParagraph(W.Body body, string text, int halfPoints, bool bold = false, string font = "Arial")
    {
        var runProperties = new W.RunProperties(
            new W.RunFonts { Ascii = font, HighAnsi = font, EastAsia = font },
            new W.FontSize { Val = halfPoints.ToString() });

        if (bold)
            runProperties.AppendChild(new W.Bold());

        var run = new W.Run(runProperties);
        var normalized = (text ?? "").Replace("\r\n", "\n");
        var parts = normalized.Split('\n');
        for (var i = 0; i < parts.Length; i++)
        {
            run.AppendChild(new W.Text(parts[i]) { Space = SpaceProcessingModeValues.Preserve });
            if (i + 1 < parts.Length)
                run.AppendChild(new W.Break());
        }

        var paragraph = new W.Paragraph(
            new W.ParagraphProperties(new W.SpacingBetweenLines { After = "120", Line = "276", LineRule = W.LineSpacingRuleValues.Auto }),
            run);
        body.AppendChild(paragraph);
    }

    private static void AppendHtmlMessage(StringBuilder sb, string text)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        var plain = new StringBuilder();
        var inCode = false;
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (plain.Length > 0)
                {
                    sb.Append("<div class=\"body\">").Append(E(plain.ToString().TrimEnd())).AppendLine("</div>");
                    plain.Clear();
                }

                if (!inCode)
                {
                    sb.AppendLine("<pre><code>");
                    inCode = true;
                }
                else
                {
                    sb.AppendLine("</code></pre>");
                    inCode = false;
                }
                continue;
            }

            if (inCode)
                sb.Append(E(line)).Append('\n');
            else
                plain.AppendLine(line);
        }

        if (inCode)
            sb.AppendLine("</code></pre>");
        if (plain.Length > 0)
            sb.Append("<div class=\"body\">").Append(E(plain.ToString().TrimEnd())).AppendLine("</div>");
    }

    private static string NormalizeExportText(string? value, bool pdfSafe)
    {
        var text = value ?? "";
        if (!pdfSafe) return text;

        // PDF fonts used by the exporter cover Cyrillic and common BMP symbols.
        // Replace supplementary-plane characters (most emoji) with a visible
        // placeholder instead of allowing a missing glyph to abort rendering.
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append('□');
                i++;
                continue;
            }

            if (!char.IsSurrogate(ch))
                sb.Append(ch);
        }
        return sb.ToString();
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

    private sealed class WindowsFallbackFontResolver : IFontResolver
    {
        private readonly string _fontDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
        {
            var mono = familyName.Contains("Courier", StringComparison.OrdinalIgnoreCase) ||
                       familyName.Contains("Consolas", StringComparison.OrdinalIgnoreCase);

            var regularFile = mono ? "cour.ttf" : "arial.ttf";
            var boldFile = mono ? "courbd.ttf" : "arialbd.ttf";
            var selected = bold && File.Exists(Path.Combine(_fontDirectory, boldFile)) ? boldFile : regularFile;
            var fullPath = Path.Combine(_fontDirectory, selected);
            if (!File.Exists(fullPath)) return null;

            var simulateBold = bold && !selected.Equals(boldFile, StringComparison.OrdinalIgnoreCase);
            return new FontResolverInfo("win:" + selected, simulateBold, italic);
        }

        public byte[]? GetFont(string faceName)
        {
            const string prefix = "win:";
            if (!faceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var fileName = faceName[prefix.Length..];
            var fullPath = Path.Combine(_fontDirectory, fileName);
            return File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null;
        }
    }
}
