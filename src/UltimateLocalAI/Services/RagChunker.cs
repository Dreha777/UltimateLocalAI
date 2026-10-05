using System.Text;
using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public static class RagChunker
{
    public static List<RagChunkRecord> Build(
        IReadOnlyList<RagSourceSegment> segments,
        long documentId,
        string sourcePath,
        ref long nextChunkId,
        int targetChars = 1400,
        int overlapChars = 220)
    {
        var result = new List<RagChunkRecord>();
        if (segments.Count == 0) return result;

        var chunkId = nextChunkId;
        targetChars = Math.Clamp(targetChars, 600, 4000);
        overlapChars = Math.Clamp(overlapChars, 0, Math.Min(800, targetChars / 2));

        var buffer = new StringBuilder();
        int? pageFrom = null;
        int? pageTo = null;
        var section = "";
        var extractionModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ocrConfidences = new List<double>();

        void Flush()
        {
            var content = buffer.ToString().Trim();
            if (content.Length < 40)
            {
                buffer.Clear();
                return;
            }

            result.Add(new RagChunkRecord
            {
                ChunkId = chunkId++,
                DocumentId = documentId,
                ChunkIndex = result.Count,
                PageFrom = pageFrom,
                PageTo = pageTo,
                Section = section,
                SourcePath = sourcePath,
                Content = content,
                ExtractionMode = ResolveExtractionMode(extractionModes),
                OcrConfidence = ocrConfidences.Count == 0 ? 0 : ocrConfidences.Average(),
                VectorIndex = result.Count
            });

            var overlap = Tail(content, overlapChars);
            buffer.Clear();
            if (!string.IsNullOrWhiteSpace(overlap))
                buffer.Append(overlap).AppendLine();

            pageFrom = pageTo;

            var carriedMode = ResolveExtractionMode(extractionModes);
            var carriedConfidence = ocrConfidences.Count == 0 ? 0 : ocrConfidences.Average();
            extractionModes.Clear();
            ocrConfidences.Clear();
            if (!string.IsNullOrWhiteSpace(overlap))
            {
                if (!string.IsNullOrWhiteSpace(carriedMode))
                    extractionModes.Add(carriedMode);
                if (carriedConfidence > 0)
                    ocrConfidences.Add(carriedConfidence);
            }
        }

        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment.Content)) continue;

            var text = segment.Content.Trim();
            if (buffer.Length > 0 && buffer.Length + text.Length + 2 > targetChars)
                Flush();

            if (pageFrom is null && segment.PageNumber.HasValue)
                pageFrom = segment.PageNumber;
            if (segment.PageNumber.HasValue)
                pageTo = segment.PageNumber;
            if (!string.IsNullOrWhiteSpace(segment.Section))
                section = segment.Section;
            if (!string.IsNullOrWhiteSpace(segment.ExtractionMode))
                extractionModes.Add(segment.ExtractionMode);
            if (segment.OcrConfidence > 0)
                ocrConfidences.Add(segment.OcrConfidence);

            if (!string.IsNullOrWhiteSpace(segment.Section) &&
                (buffer.Length == 0 || !buffer.ToString().Contains(segment.Section, StringComparison.Ordinal)))
            {
                buffer.Append("Раздел: ").Append(segment.Section).AppendLine();
            }

            if (text.Length <= targetChars)
            {
                buffer.AppendLine(text);
                continue;
            }

            var start = 0;
            while (start < text.Length)
            {
                var room = Math.Max(200, targetChars - buffer.Length);
                var take = Math.Min(room, text.Length - start);
                var boundary = FindBoundary(text, start, take);
                buffer.Append(text.AsSpan(start, boundary - start)).AppendLine();
                start = boundary;

                if (buffer.Length >= targetChars * 0.85)
                    Flush();
            }
        }

        Flush();
        nextChunkId = chunkId;
        return result;
    }

    private static string ResolveExtractionMode(IEnumerable<string> modes)
    {
        var set = modes.Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0) return "Text";
        if (set.Any(x => x.Contains("LowConfidence", StringComparison.OrdinalIgnoreCase))) return "OCR-LowConfidence";
        if (set.Count > 1) return "Mixed";
        return set.First();
    }

    private static int FindBoundary(string text, int start, int take)
    {
        var end = start + take;
        if (end >= text.Length) return text.Length;

        var min = start + Math.Max(100, take / 2);
        for (var i = end; i > min; i--)
        {
            var ch = text[i - 1];
            if (ch is '.' or '!' or '?' or '\n' or ';')
                return i;
        }

        for (var i = end; i > min; i--)
            if (char.IsWhiteSpace(text[i - 1])) return i;

        return end;
    }

    private static string Tail(string text, int length)
    {
        if (length <= 0 || string.IsNullOrEmpty(text)) return "";
        if (text.Length <= length) return text;

        var start = text.Length - length;
        while (start < text.Length - 1 && !char.IsWhiteSpace(text[start]))
            start++;

        return text[start..].Trim();
    }
}
