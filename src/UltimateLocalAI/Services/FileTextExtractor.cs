using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public sealed class FileTextExtractor
{
    private const int MaxChars = 400_000;
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".xml", ".yaml", ".yml", ".ini", ".log",
        ".cs", ".cpp", ".c", ".h", ".hpp", ".py", ".js", ".ts", ".html", ".css", ".sql",
        ".java", ".kt", ".go", ".rs", ".ps1", ".bat", ".cmd", ".sh", ".tex"
    };

    public async Task<AttachmentInfo> ExtractAsync(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Файл не найден", path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        string text = ext switch
        {
            ".docx" => ExtractDocx(path),
            ".xlsx" => ExtractXlsx(path),
            ".pdf" => ExtractPdfLite(path),
            _ when TextExtensions.Contains(ext) => await ReadTextSafeAsync(path),
            _ => throw new NotSupportedException($"Формат {ext} пока не поддерживается для извлечения текста.")
        };

        if (text.Length > MaxChars)
            text = text[..MaxChars] + "\n\n[Текст файла обрезан приложением до 400 000 символов]";

        var fi = new FileInfo(path);
        return new AttachmentInfo { FileName = fi.Name, FullPath = fi.FullName, SizeBytes = fi.Length, ExtractedText = text };
    }

    private static async Task<string> ReadTextSafeAsync(string path)
    {
        using var sr = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxChars + 1];
        var read = await sr.ReadBlockAsync(buffer.AsMemory(0, buffer.Length));
        return new string(buffer, 0, read);
    }

    private static string ExtractDocx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("В DOCX отсутствует word/document.xml");
        using var s = entry.Open();
        var doc = XDocument.Load(s);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var sb = new StringBuilder();
        foreach (var p in doc.Descendants(w + "p"))
        {
            var line = string.Concat(p.Descendants(w + "t").Select(x => x.Value));
            if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private static string ExtractXlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var shared = new List<string>();
        var sharedEntry = zip.GetEntry("xl/sharedStrings.xml");
        if (sharedEntry is not null)
        {
            using var s = sharedEntry.Open();
            var x = XDocument.Load(s);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            shared.AddRange(x.Descendants(ns + "si").Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value))));
        }

        XNamespace ss = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var sb = new StringBuilder();
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml")))
        {
            sb.AppendLine($"### {Path.GetFileNameWithoutExtension(entry.Name)}");
            using var s = entry.Open();
            var doc = XDocument.Load(s);
            foreach (var row in doc.Descendants(ss + "row"))
            {
                var cells = new List<string>();
                foreach (var c in row.Elements(ss + "c"))
                {
                    var type = (string?)c.Attribute("t");
                    var v = c.Element(ss + "v")?.Value ?? "";
                    if (type == "s" && int.TryParse(v, out var idx) && idx >= 0 && idx < shared.Count) v = shared[idx];
                    cells.Add(v);
                }
                sb.AppendLine(string.Join("\t", cells));
                if (sb.Length > MaxChars) break;
            }
            if (sb.Length > MaxChars) break;
        }
        return sb.ToString();
    }

    // Встроенный, зависимостей не требующий PDF fallback. Он извлекает текст из многих обычных
    // текстовых PDF, но не заменяет полноценный OCR/PDF engine для сканов, шифрования и сложных CMaps.
    private static string ExtractPdfLite(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var source = Encoding.Latin1.GetString(bytes);
        var payloads = new List<string> { source };

        var pos = 0;
        while ((pos = source.IndexOf("stream", pos, StringComparison.Ordinal)) >= 0)
        {
            var streamStart = pos + 6;
            if (streamStart < source.Length && source[streamStart] == '\r') streamStart++;
            if (streamStart < source.Length && source[streamStart] == '\n') streamStart++;
            var end = source.IndexOf("endstream", streamStart, StringComparison.Ordinal);
            if (end < 0) break;

            var dictStart = source.LastIndexOf("<<", pos, StringComparison.Ordinal);
            var dictEnd = dictStart >= 0 ? source.IndexOf(">>", dictStart, StringComparison.Ordinal) : -1;
            var dict = dictStart >= 0 && dictEnd >= 0 && dictEnd < pos ? source[dictStart..(dictEnd + 2)] : "";

            try
            {
                var raw = bytes.AsSpan(streamStart, Math.Max(0, end - streamStart)).ToArray();
                if (dict.Contains("/FlateDecode", StringComparison.Ordinal))
                {
                    using var input = new MemoryStream(raw);
                    using var z = new ZLibStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    z.CopyTo(output);
                    payloads.Add(Encoding.Latin1.GetString(output.ToArray()));
                }
                else
                {
                    payloads.Add(Encoding.Latin1.GetString(raw));
                }
            }
            catch { }
            pos = end + 9;
            if (payloads.Sum(x => x.Length) > 8_000_000) break;
        }

        var sb = new StringBuilder();
        foreach (var payload in payloads)
        {
            foreach (Match block in Regex.Matches(payload, @"BT(?<body>.*?)ET", RegexOptions.Singleline))
            {
                var body = block.Groups["body"].Value;
                foreach (Match m in Regex.Matches(body, @"\((?<s>(?:\\.|[^\\)])*)\)\s*(?:Tj|'|"")", RegexOptions.Singleline))
                    AppendPdfString(sb, m.Groups["s"].Value);

                foreach (Match arr in Regex.Matches(body, @"\[(?<a>.*?)\]\s*TJ", RegexOptions.Singleline))
                    foreach (Match m in Regex.Matches(arr.Groups["a"].Value, @"\((?<s>(?:\\.|[^\\)])*)\)", RegexOptions.Singleline))
                        AppendPdfString(sb, m.Groups["s"].Value);

                foreach (Match h in Regex.Matches(body, @"<(?<h>[0-9A-Fa-f]{4,})>\s*Tj"))
                {
                    var decoded = DecodeHexPdfString(h.Groups["h"].Value);
                    if (!string.IsNullOrWhiteSpace(decoded)) sb.Append(decoded).Append(' ');
                }
                if (sb.Length > MaxChars) break;
            }
            if (sb.Length > MaxChars) break;
        }

        var text = Regex.Replace(sb.ToString(), @"[ \t]{2,}", " ");
        text = Regex.Replace(text, @"(\r?\n){3,}", "\n\n").Trim();
        if (text.Length < 20)
            return "[PDF распознан как сканированный/сложный или текст в нём закодирован нестандартно. Встроенный офлайн-парсер не смог надёжно извлечь текст. Для такого PDF используйте OCR или преобразуйте его в DOCX/TXT.]";
        return text;
    }

    private static void AppendPdfString(StringBuilder sb, string value)
    {
        value = Regex.Replace(value, @"\\([0-7]{1,3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());
        value = value.Replace("\\n", "\n").Replace("\\r", "\n").Replace("\\t", "\t")
                     .Replace("\\(", "(").Replace("\\)", ")").Replace("\\\\", "\\");
        if (!string.IsNullOrWhiteSpace(value)) sb.Append(value).Append(' ');
    }

    private static string DecodeHexPdfString(string hex)
    {
        try
        {
            if (hex.Length % 2 != 0) hex += "0";
            var data = Convert.FromHexString(hex);
            if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
                return Encoding.Unicode.GetString(data, 2, data.Length - 2);
            return Encoding.Latin1.GetString(data);
        }
        catch { return ""; }
    }
}
