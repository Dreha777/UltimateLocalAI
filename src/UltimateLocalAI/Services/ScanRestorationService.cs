using OpenCvSharp;

namespace UltimateLocalAI.Services;

public sealed class ScanRestorationService
{
    public IReadOnlyList<RestorationCandidate> BuildCandidates(
        string inputImage,
        string outputDirectory,
        double maxDeskewDegrees)
    {
        if (!File.Exists(inputImage))
            throw new FileNotFoundException("Исходное изображение страницы не найдено.", inputImage);

        Directory.CreateDirectory(outputDirectory);

        using var source = Cv2.ImRead(inputImage, ImreadModes.Grayscale);
        if (source.Empty())
            throw new InvalidDataException("OpenCV не смог открыть изображение страницы.");

        var candidates = new List<RestorationCandidate>
        {
            new()
            {
                Name = "Original",
                ImagePath = inputImage,
                DeskewDegrees = 0,
                Processed = false
            }
        };

        using var oriented = EnsureLightBackground(source);
        var detectedAngle = DetectDeskewAngle(oriented, Math.Clamp(maxDeskewDegrees, 0, 20));
        using var deskewed = Math.Abs(detectedAngle) >= 0.15
            ? Rotate(oriented, -detectedAngle)
            : oriented.Clone();

        using var cropped = ConservativeCrop(deskewed);
        using var normalized = NormalizeBackground(cropped);
        using var clean = EnhanceGray(normalized);

        var cleanPath = Path.Combine(outputDirectory, "restored-gray.png");
        Cv2.ImWrite(cleanPath, clean);
        candidates.Add(new RestorationCandidate
        {
            Name = "Restored gray",
            ImagePath = cleanPath,
            DeskewDegrees = -detectedAngle,
            Processed = true
        });

        using var adaptive = new Mat();
        var blockSize = Math.Clamp((Math.Min(clean.Width, clean.Height) / 35) | 1, 31, 81);
        if (blockSize % 2 == 0) blockSize++;
        Cv2.AdaptiveThreshold(
            clean,
            adaptive,
            255,
            AdaptiveThresholdTypes.GaussianC,
            ThresholdTypes.Binary,
            blockSize,
            13);

        var adaptivePath = Path.Combine(outputDirectory, "restored-adaptive.png");
        Cv2.ImWrite(adaptivePath, adaptive);
        candidates.Add(new RestorationCandidate
        {
            Name = "Restored adaptive",
            ImagePath = adaptivePath,
            DeskewDegrees = -detectedAngle,
            Processed = true
        });

        using var otsu = new Mat();
        Cv2.Threshold(clean, otsu, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        var otsuPath = Path.Combine(outputDirectory, "restored-otsu.png");
        Cv2.ImWrite(otsuPath, otsu);
        candidates.Add(new RestorationCandidate
        {
            Name = "Restored Otsu",
            ImagePath = otsuPath,
            DeskewDegrees = -detectedAngle,
            Processed = true
        });

        return candidates;
    }

    private static Mat EnsureLightBackground(Mat source)
    {
        var result = source.Clone();
        var mean = Cv2.Mean(result).Val0;
        if (mean < 110)
            Cv2.BitwiseNot(result, result);
        return result;
    }

    private static double DetectDeskewAngle(Mat gray, double maxDeskewDegrees)
    {
        if (maxDeskewDegrees <= 0)
            return 0;

        using var edges = new Mat();
        Cv2.Canny(gray, edges, 50, 150, 3, false);

        var minLine = Math.Max(60.0, gray.Width * 0.18);
        var lines = Cv2.HoughLinesP(
            edges,
            1,
            Math.PI / 180.0,
            80,
            minLineLength: minLine,
            maxLineGap: 24);

        if (lines.Length == 0)
            return 0;

        var angles = new List<double>();
        foreach (var line in lines)
        {
            var dx = line.P2.X - line.P1.X;
            var dy = line.P2.Y - line.P1.Y;
            if (dx == 0 && dy == 0) continue;

            var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            while (angle > 90) angle -= 180;
            while (angle < -90) angle += 180;

            if (Math.Abs(angle) <= maxDeskewDegrees)
                angles.Add(angle);
        }

        if (angles.Count < 2)
            return 0;

        angles.Sort();
        var mid = angles.Count / 2;
        return angles.Count % 2 == 0
            ? (angles[mid - 1] + angles[mid]) / 2.0
            : angles[mid];
    }

    private static Mat Rotate(Mat source, double degrees)
    {
        var center = new Point2f(source.Width / 2f, source.Height / 2f);
        using var matrix = Cv2.GetRotationMatrix2D(center, degrees, 1.0);
        var result = new Mat();
        Cv2.WarpAffine(
            source,
            result,
            matrix,
            source.Size(),
            InterpolationFlags.Cubic,
            BorderTypes.Constant,
            Scalar.White);
        return result;
    }

    private static Mat ConservativeCrop(Mat source)
    {
        using var binary = new Mat();
        Cv2.Threshold(source, binary, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        Cv2.FindContours(
            binary,
            out Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        var minX = source.Width;
        var minY = source.Height;
        var maxX = 0;
        var maxY = 0;
        var found = false;

        foreach (var contour in contours)
        {
            var rect = Cv2.BoundingRect(contour);
            if (rect.Width * rect.Height < 30)
                continue;

            found = true;
            minX = Math.Min(minX, rect.Left);
            minY = Math.Min(minY, rect.Top);
            maxX = Math.Max(maxX, rect.Right);
            maxY = Math.Max(maxY, rect.Bottom);
        }

        if (!found)
            return source.Clone();

        const int padding = 24;
        minX = Math.Max(0, minX - padding);
        minY = Math.Max(0, minY - padding);
        maxX = Math.Min(source.Width, maxX + padding);
        maxY = Math.Min(source.Height, maxY + padding);

        var width = maxX - minX;
        var height = maxY - minY;

        if (width < source.Width * 0.75 ||
            height < source.Height * 0.75 ||
            width <= 0 ||
            height <= 0)
            return source.Clone();

        var rectCrop = new Rect(minX, minY, width, height);
        return new Mat(source, rectCrop).Clone();
    }

    private static Mat NormalizeBackground(Mat source)
    {
        using var background = new Mat();
        var sigma = Math.Max(12.0, Math.Min(source.Width, source.Height) / 70.0);
        Cv2.GaussianBlur(source, background, new Size(0, 0), sigma, sigma);

        var flattened = new Mat();
        Cv2.AddWeighted(source, 1.25, background, -0.25, 32, flattened);
        Cv2.Normalize(flattened, flattened, 0, 255, NormTypes.MinMax);
        return flattened;
    }

    private static Mat EnhanceGray(Mat source)
    {
        using var denoised = new Mat();
        Cv2.MedianBlur(source, denoised, 3);

        var result = new Mat();
        using var clahe = Cv2.CreateCLAHE(2.2, new Size(8, 8));
        clahe.Apply(denoised, result);
        return result;
    }
}

public sealed class RestorationCandidate
{
    public string Name { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public double DeskewDegrees { get; set; }
    public bool Processed { get; set; }
}

public sealed class OcrCandidateSelection
{
    public RestorationCandidate Candidate { get; set; } = new();
    public OcrRecognitionResult Recognition { get; set; } = new();
    public double Score { get; set; }
    public OcrRecognitionResult RawRecognition { get; set; } = new();
}

public static class OcrCandidateSelector
{
    public static OcrCandidateSelection SelectBest(
        LocalOcrSession ocr,
        IReadOnlyList<RestorationCandidate> candidates,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0)
            throw new InvalidOperationException("Не получено ни одного OCR-кандидата.");

        OcrCandidateSelection? best = null;
        OcrRecognitionResult? raw = null;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var recognition = ocr.Recognize(candidate.ImagePath);
            if (!candidate.Processed)
                raw = recognition;

            var score = Score(recognition);
            var current = new OcrCandidateSelection
            {
                Candidate = candidate,
                Recognition = recognition,
                Score = score
            };

            if (best is null || current.Score > best.Score)
                best = current;
        }

        if (best is null)
            throw new InvalidOperationException("OCR не смог оценить кандидатов.");
        raw ??= best.Recognition;

        // Conservative rule: preprocessing must clearly beat the original,
        // otherwise preserve the unmodified rendered page.
        var rawCandidate = candidates
            .Select((candidate, index) => (candidate, index))
            .FirstOrDefault(x => !x.candidate.Processed);

        if (best.Candidate.Processed && rawCandidate.candidate is not null)
        {
            var originalRecognition = raw;
            var originalScore = Score(originalRecognition);
            if (best.Score < originalScore + 0.012)
            {
                best = new OcrCandidateSelection
                {
                    Candidate = rawCandidate.candidate,
                    Recognition = originalRecognition,
                    Score = originalScore
                };
            }
        }

        best.RawRecognition = raw;
        return best;
    }

    private static double Score(OcrRecognitionResult result)
    {
        var text = PdfImportAnalyzer.Normalize(result.Text);
        var quality = PdfImportAnalyzer.EvaluatePage(1, text);
        var useful = Math.Clamp(quality.UsefulTextRatio, 0, 1);
        var meaningfulChars = text.Count(char.IsLetterOrDigit);

        var lengthBonus = meaningfulChars switch
        {
            < 10 => -0.20,
            < 30 => -0.08,
            < 100 => 0.00,
            < 500 => 0.025,
            _ => 0.05
        };

        return result.Confidence * 0.80 + useful * 0.15 + lengthBonus;
    }
}
