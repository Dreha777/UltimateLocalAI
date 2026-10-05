using System.Text;
using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

/// <summary>
/// Reads only lightweight GGUF metadata from the file header. Tensor data is never loaded.
/// Supports modern GGUF v2/v3 files and stops before large tokenizer arrays when possible.
/// </summary>
public static class GgufMetadataReader
{
    private const uint GgufMagic = 0x46554747; // ASCII "GGUF" in little-endian.

    private enum GgufValueType : uint
    {
        UInt8 = 0,
        Int8 = 1,
        UInt16 = 2,
        Int16 = 3,
        UInt32 = 4,
        Int32 = 5,
        Float32 = 6,
        Bool = 7,
        String = 8,
        Array = 9,
        UInt64 = 10,
        Int64 = 11,
        Float64 = 12
    }

    public static GgufModelMetadata Read(string path)
    {
        var result = new GgufModelMetadata();

        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return result;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 64 * 1024, options: FileOptions.SequentialScan);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

            if (reader.ReadUInt32() != GgufMagic)
                return result;

            var version = reader.ReadUInt32();
            result.Version = version;
            if (version is < 2 or > 3)
            {
                LogService.Warn($"GGUF metadata: unsupported version {version}");
                return result;
            }

            _ = reader.ReadUInt64(); // tensor_count
            var metadataCount = reader.ReadUInt64();
            if (metadataCount > 1_000_000)
                throw new InvalidDataException($"Unreasonable GGUF metadata count: {metadataCount}");

            for (ulong i = 0; i < metadataCount; i++)
            {
                var key = ReadString(reader);
                var type = (GgufValueType)reader.ReadUInt32();

                var wanted =
                    key == "general.name" ||
                    key == "general.architecture" ||
                    key == "general.size_label" ||
                    key == "general.file_type" ||
                    key == "general.sampling.temp" ||
                    key == "general.sampling.top_p" ||
                    key == "general.sampling.top_k" ||
                    key == "general.sampling.min_p" ||
                    key == "general.sampling.penalty_repeat" ||
                    key.EndsWith(".context_length", StringComparison.Ordinal);

                if (wanted)
                {
                    var value = ReadScalarOrSkip(reader, type);
                    Assign(result, key, value);
                }
                else
                {
                    SkipValue(reader, type);
                }

                // Tokenizer metadata can contain very large string arrays. All model-level
                // keys we care about are conventionally emitted before tokenizer metadata.
                if (key.StartsWith("tokenizer.", StringComparison.Ordinal) &&
                    result.FileTypeCode.HasValue &&
                    !string.IsNullOrWhiteSpace(result.Architecture))
                {
                    break;
                }
            }

            result.Quantization = FileTypeName(result.FileTypeCode);
            result.IsValid = true;
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   EndOfStreamException or InvalidDataException or OverflowException)
        {
            LogService.Warn("GGUF metadata unavailable: " + ex.Message);
            return result;
        }
    }

    private static void Assign(GgufModelMetadata result, string key, object? value)
    {
        switch (key)
        {
            case "general.name":
                result.Name = value as string ?? "";
                return;
            case "general.architecture":
                result.Architecture = value as string ?? "";
                return;
            case "general.size_label":
                result.SizeLabel = value as string ?? "";
                return;
            case "general.file_type":
                if (TryInt64(value, out var fileType) && fileType is >= 0 and <= int.MaxValue)
                    result.FileTypeCode = (int)fileType;
                return;
            case "general.sampling.temp":
                if (TryDouble(value, out var temp)) result.RecommendedTemperature = temp;
                return;
            case "general.sampling.top_p":
                if (TryDouble(value, out var topP)) result.RecommendedTopP = topP;
                return;
            case "general.sampling.top_k":
                if (TryInt64(value, out var topK) && topK is >= 0 and <= int.MaxValue)
                    result.RecommendedTopK = (int)topK;
                return;
            case "general.sampling.min_p":
                if (TryDouble(value, out var minP)) result.RecommendedMinP = minP;
                return;
            case "general.sampling.penalty_repeat":
                if (TryDouble(value, out var repeat)) result.RecommendedRepeatPenalty = repeat;
                return;
        }

        if (key.EndsWith(".context_length", StringComparison.Ordinal) &&
            TryInt64(value, out var context) && context > 0)
        {
            result.NativeContextSize = context;
        }
    }

    private static object? ReadScalarOrSkip(BinaryReader reader, GgufValueType type) => type switch
    {
        GgufValueType.UInt8 => reader.ReadByte(),
        GgufValueType.Int8 => reader.ReadSByte(),
        GgufValueType.UInt16 => reader.ReadUInt16(),
        GgufValueType.Int16 => reader.ReadInt16(),
        GgufValueType.UInt32 => reader.ReadUInt32(),
        GgufValueType.Int32 => reader.ReadInt32(),
        GgufValueType.Float32 => reader.ReadSingle(),
        GgufValueType.Bool => reader.ReadByte() != 0,
        GgufValueType.String => ReadString(reader),
        GgufValueType.UInt64 => reader.ReadUInt64(),
        GgufValueType.Int64 => reader.ReadInt64(),
        GgufValueType.Float64 => reader.ReadDouble(),
        _ => SkipAndReturnNull(reader, type)
    };

    private static object? SkipAndReturnNull(BinaryReader reader, GgufValueType type)
    {
        SkipValue(reader, type);
        return null;
    }

    private static void SkipValue(BinaryReader reader, GgufValueType type)
    {
        switch (type)
        {
            case GgufValueType.UInt8:
            case GgufValueType.Int8:
            case GgufValueType.Bool:
                Seek(reader, 1);
                return;
            case GgufValueType.UInt16:
            case GgufValueType.Int16:
                Seek(reader, 2);
                return;
            case GgufValueType.UInt32:
            case GgufValueType.Int32:
            case GgufValueType.Float32:
                Seek(reader, 4);
                return;
            case GgufValueType.UInt64:
            case GgufValueType.Int64:
            case GgufValueType.Float64:
                Seek(reader, 8);
                return;
            case GgufValueType.String:
                SkipString(reader);
                return;
            case GgufValueType.Array:
            {
                var elementType = (GgufValueType)reader.ReadUInt32();
                var count = reader.ReadUInt64();
                if (count > 100_000_000)
                    throw new InvalidDataException($"Unreasonable GGUF array length: {count}");

                var fixedSize = FixedSize(elementType);
                if (fixedSize > 0)
                {
                    checked
                    {
                        Seek(reader, (long)count * fixedSize);
                    }
                    return;
                }

                if (elementType == GgufValueType.String)
                {
                    for (ulong i = 0; i < count; i++)
                        SkipString(reader);
                    return;
                }

                throw new InvalidDataException($"Unsupported GGUF array element type: {(uint)elementType}");
            }
            default:
                throw new InvalidDataException($"Unknown GGUF metadata value type: {(uint)type}");
        }
    }

    private static int FixedSize(GgufValueType type) => type switch
    {
        GgufValueType.UInt8 or GgufValueType.Int8 or GgufValueType.Bool => 1,
        GgufValueType.UInt16 or GgufValueType.Int16 => 2,
        GgufValueType.UInt32 or GgufValueType.Int32 or GgufValueType.Float32 => 4,
        GgufValueType.UInt64 or GgufValueType.Int64 or GgufValueType.Float64 => 8,
        _ => 0
    };

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();
        if (length > 64 * 1024 * 1024)
            throw new InvalidDataException($"Unreasonable GGUF string length: {length}");

        var bytes = reader.ReadBytes(checked((int)length));
        if ((ulong)bytes.Length != length)
            throw new EndOfStreamException();

        return Encoding.UTF8.GetString(bytes);
    }

    private static void SkipString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();
        if (length > long.MaxValue)
            throw new InvalidDataException("GGUF string is too large.");
        Seek(reader, (long)length);
    }

    private static void Seek(BinaryReader reader, long bytes)
    {
        if (bytes < 0 || reader.BaseStream.Position > reader.BaseStream.Length - bytes)
            throw new EndOfStreamException();
        reader.BaseStream.Seek(bytes, SeekOrigin.Current);
    }

    private static bool TryInt64(object? value, out long result)
    {
        switch (value)
        {
            case byte v: result = v; return true;
            case sbyte v: result = v; return true;
            case ushort v: result = v; return true;
            case short v: result = v; return true;
            case uint v when v <= long.MaxValue: result = v; return true;
            case int v: result = v; return true;
            case ulong v when v <= long.MaxValue: result = (long)v; return true;
            case long v: result = v; return true;
            default: result = 0; return false;
        }
    }

    private static bool TryDouble(object? value, out double result)
    {
        switch (value)
        {
            case float v: result = v; return true;
            case double v: result = v; return true;
            case byte v: result = v; return true;
            case sbyte v: result = v; return true;
            case ushort v: result = v; return true;
            case short v: result = v; return true;
            case uint v: result = v; return true;
            case int v: result = v; return true;
            case ulong v: result = v; return true;
            case long v: result = v; return true;
            default: result = 0; return false;
        }
    }

    private static string FileTypeName(int? code) => code switch
    {
        0 => "F32",
        1 => "F16",
        2 => "Q4_0",
        3 => "Q4_1",
        7 => "Q8_0",
        8 => "Q5_0",
        9 => "Q5_1",
        10 => "Q2_K",
        11 => "Q3_K_S",
        12 => "Q3_K_M",
        13 => "Q3_K_L",
        14 => "Q4_K_S",
        15 => "Q4_K_M",
        16 => "Q5_K_S",
        17 => "Q5_K_M",
        18 => "Q6_K",
        19 => "IQ2_XXS",
        20 => "IQ2_XS",
        21 => "Q2_K_S",
        22 => "IQ3_XS",
        23 => "IQ3_XXS",
        24 => "IQ1_S",
        25 => "IQ4_NL",
        26 => "IQ3_S",
        27 => "IQ3_M",
        28 => "IQ2_S",
        29 => "IQ2_M",
        30 => "IQ4_XS",
        31 => "IQ1_M",
        32 => "BF16",
        36 => "TQ1_0",
        37 => "TQ2_0",
        38 => "MXFP4_MOE",
        39 => "NVFP4",
        40 => "Q1_0",
        41 => "Q2_0",
        1024 => "GUESSED",
        int v => $"ftype:{v}",
        null => ""
    };
}
