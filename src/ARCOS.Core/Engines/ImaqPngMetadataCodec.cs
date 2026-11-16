using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ARCOS.Core.Model;

namespace ARCOS.Core.Engines;

/// <summary>
/// 標準 PNG iTXt Chunk Vision Metadata 編解碼器 (Keyword: ARCOS_IMAQ_META)。
/// 支援於 PNG 的 IEND 區塊前無損嵌入與還原 Timestamp、ROI、非破壞性 Overlays、
/// 空間校正資料 (Calibration) 與自訂鍵值字典 (Metadata)，並保持與所有標準看圖軟體及 OpenCV 相容。
/// </summary>
public static class ImaqPngMetadataCodec
{
    public const string ChunkKeyword = "ARCOS_IMAQ_META";

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] ITxtType = Encoding.ASCII.GetBytes("iTXt");
    private static readonly byte[] IEndType = Encoding.ASCII.GetBytes("IEND");
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// 將 Vision Metadata 寫入既有 PNG 位元組陣列中（自動替換任何既有的同關鍵字 ARCOS_IMAQ_META 區塊）。
    /// </summary>
    public static byte[] EmbedMetadata(byte[] pngBytes, ImaqPngMetadataPayload payload, bool compressOverThreshold = true)
    {
        if (pngBytes == null || pngBytes.Length < PngSignature.Length || !IsPngSignature(pngBytes))
            throw new ArgumentException("Input bytes are not a valid PNG stream.", nameof(pngBytes));

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);

        bool useCompression = compressOverThreshold && jsonBytes.Length > 16 * 1024;
        byte[] textData;
        if (useCompression)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                z.Write(jsonBytes, 0, jsonBytes.Length);
            }
            textData = ms.ToArray();
        }
        else
        {
            textData = jsonBytes;
        }

        byte[] chunkData = BuildITxtPayload(ChunkKeyword, textData, compressed: useCompression);
        byte[] metaChunk = BuildPngChunk(ITxtType, chunkData);

        using var output = new MemoryStream(pngBytes.Length + metaChunk.Length);
        output.Write(PngSignature, 0, PngSignature.Length);

        int pos = PngSignature.Length;
        bool inserted = false;

        while (pos + 12 <= pngBytes.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(pos, 4));
            if (len > int.MaxValue || pos + 12L + len > pngBytes.Length)
                break;

            int chunkTotalLen = 12 + (int)len;
            var chunkType = pngBytes.AsSpan(pos + 4, 4);
            var chunkPayload = pngBytes.AsSpan(pos + 8, (int)len);

            // Replace any stale ARCOS_IMAQ_META chunk cleanly.
            if (chunkType.SequenceEqual(ITxtType) && IsMatchingKeyword(chunkPayload, ChunkKeyword))
            {
                pos += chunkTotalLen;
                continue;
            }

            if (chunkType.SequenceEqual(IEndType) && !inserted)
            {
                output.Write(metaChunk, 0, metaChunk.Length);
                inserted = true;
            }

            output.Write(pngBytes, pos, chunkTotalLen);
            pos += chunkTotalLen;
        }

        if (!inserted)
        {
            output.Write(metaChunk, 0, metaChunk.Length);
            output.Write(BuildPngChunk(IEndType, Array.Empty<byte>()));
        }

        return output.ToArray();
    }

    /// <summary>
    /// 從 PNG 位元組陣列讀取並反序列化 ARCOS_IMAQ_META Vision Metadata；若無該區塊則回傳 null。
    /// </summary>
    public static ImaqPngMetadataPayload? ExtractMetadata(byte[] pngBytes)
    {
        if (pngBytes == null || pngBytes.Length < PngSignature.Length + 12 || !IsPngSignature(pngBytes))
            return null;

        int pos = PngSignature.Length;
        while (pos + 12 <= pngBytes.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(pos, 4));
            if (len > int.MaxValue || pos + 12L + len > pngBytes.Length)
                break;

            var chunkType = pngBytes.AsSpan(pos + 4, 4);
            var chunkPayload = pngBytes.AsSpan(pos + 8, (int)len);

            if (chunkType.SequenceEqual(ITxtType) && IsMatchingKeyword(chunkPayload, ChunkKeyword))
            {
                string? json = DecodeITxtText(chunkPayload);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    try
                    {
                        var payload = JsonSerializer.Deserialize<ImaqPngMetadataPayload>(json, JsonOptions);
                        if (payload != null)
                        {
                            // Ensure case-insensitive metadata lookup after JSON deserialization.
                            payload.Metadata = new Dictionary<string, string>(
                                payload.Metadata ?? new Dictionary<string, string>(),
                                StringComparer.OrdinalIgnoreCase);
                            return payload;
                        }
                    }
                    catch
                    {
                        return null;
                    }
                }
            }

            if (chunkType.SequenceEqual(IEndType))
                break;

            pos += 12 + (int)len;
        }

        return null;
    }

    private static bool IsPngSignature(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 8 && bytes[..8].SequenceEqual(PngSignature);

    private static bool IsMatchingKeyword(ReadOnlySpan<byte> payload, string keyword)
    {
        int nulIdx = payload.IndexOf((byte)0);
        if (nulIdx <= 0) return false;
        return Encoding.Latin1.GetString(payload[..nulIdx]).Equals(keyword, StringComparison.Ordinal);
    }

    private static byte[] BuildITxtPayload(string keyword, byte[] textBytes, bool compressed)
    {
        byte[] kwBytes = Encoding.Latin1.GetBytes(keyword);
        // Keyword + NUL + CompressionFlag(1) + CompressionMethod(1) + LanguageTag(NUL) + TranslatedKeyword(NUL) + Text
        using var ms = new MemoryStream(kwBytes.Length + 5 + textBytes.Length);
        ms.Write(kwBytes, 0, kwBytes.Length);
        ms.WriteByte(0);
        ms.WriteByte(compressed ? (byte)1 : (byte)0);
        ms.WriteByte(0); // compression method 0 = zlib deflate
        ms.WriteByte(0); // empty language tag
        ms.WriteByte(0); // empty translated keyword
        ms.Write(textBytes, 0, textBytes.Length);
        return ms.ToArray();
    }

    private static string? DecodeITxtText(ReadOnlySpan<byte> payload)
    {
        int kwEnd = payload.IndexOf((byte)0);
        if (kwEnd < 0 || kwEnd + 3 > payload.Length) return null;

        byte compFlag = payload[kwEnd + 1];
        // byte compMethod = payload[kwEnd + 2];
        int cursor = kwEnd + 3;

        int langEnd = payload[cursor..].IndexOf((byte)0);
        if (langEnd < 0) return null;
        cursor += langEnd + 1;

        int transEnd = payload[cursor..].IndexOf((byte)0);
        if (transEnd < 0) return null;
        cursor += transEnd + 1;

        if (cursor > payload.Length) return null;
        var textSpan = payload[cursor..];

        if (compFlag == 0)
            return Encoding.UTF8.GetString(textSpan);

        if (compFlag == 1)
        {
            using var inMs = new MemoryStream(textSpan.ToArray());
            using var z = new ZLibStream(inMs, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            z.CopyTo(outMs);
            return Encoding.UTF8.GetString(outMs.ToArray());
        }

        return null;
    }

    private static byte[] BuildPngChunk(ReadOnlySpan<byte> type4, ReadOnlySpan<byte> data)
    {
        byte[] chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, 4), (uint)data.Length);
        type4.CopyTo(chunk.AsSpan(4, 4));
        data.CopyTo(chunk.AsSpan(8, data.Length));
        uint crc = ComputeCrc32(chunk.AsSpan(4, 4 + data.Length));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length, 4), crc);
        return chunk;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> span)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < span.Length; i++)
        {
            crc = CrcTable[(crc ^ span[i]) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : (c >> 1);
            }
            table[n] = c;
        }
        return table;
    }
}
