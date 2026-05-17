using System;
using System.Buffers.Binary;
using System.IO;

namespace QQDatabaseExplorer.Controls;

public static class LocalImageFile
{
    private const int MarketFaceXorChunkSize = 20;
    private const int MarketFaceKeepChunkSize = 30;
    private static readonly byte[] Gif87aHeader = "GIF87a"u8.ToArray();
    private static readonly byte[] Gif89aHeader = "GIF89a"u8.ToArray();
    private static readonly byte[] PngHeader = "\x89PNG\r\n\x1A\n"u8.ToArray();

    public static byte[] ReadDisplayBytes(string sourcePath)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        if (IsMarketFaceEncodedGifBytes(bytes))
            DecodeMarketFaceBytes(bytes);

        return bytes;
    }

    public static Stream OpenDisplayStream(string sourcePath)
    {
        return IsMarketFaceEncodedGifImage(sourcePath)
            ? new MemoryStream(ReadDisplayBytes(sourcePath), writable: false)
            : File.OpenRead(sourcePath);
    }

    public static bool IsGifImage(string sourcePath)
    {
        return TryReadHeader(sourcePath, out var header) &&
               (header.SequenceEqual(Gif87aHeader) || header.SequenceEqual(Gif89aHeader));
    }

    public static bool IsMarketFaceEncodedGifImage(string sourcePath)
    {
        return TryReadHeader(sourcePath, out var header) && IsMarketFaceEncodedGifBytes(header);
    }

    public static bool IsDisplayableImageFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        Span<byte> header = stackalloc byte[12];
        try
        {
            using var stream = File.OpenRead(path);
            var read = stream.Read(header);
            var value = header[..read];
            return value.StartsWith(Gif87aHeader) ||
                   value.StartsWith(Gif89aHeader) ||
                   value.StartsWith("\x89PNG"u8) ||
                   value.Length >= 3 && value[0] == 0xFF && value[1] == 0xD8 && value[2] == 0xFF ||
                   value.StartsWith("RIFF"u8) && value.Length >= 12 && value[8..12].SequenceEqual("WEBP"u8) ||
                   IsMarketFaceEncodedGifBytes(value);
        }
        catch
        {
            return false;
        }
    }

    public static (int Width, int Height)? TryGetImageSize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            using var stream = OpenDisplayStream(path);
            return TryReadImageSize(stream);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsMarketFaceEncodedGifBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Gif89aHeader.Length)
            return false;

        // QQNT 市场表情本体文件按“20 字节 XOR 0xFF，30 字节原样保留”循环混淆。
        // GIF 头落在第一段 XOR 区，所以判断时只需要把前 6 字节临时取反。
        for (var i = 0; i < Gif89aHeader.Length; i++)
        {
            var value = (byte)(bytes[i] ^ 0xFF);
            if (value != Gif87aHeader[i] && value != Gif89aHeader[i])
                return false;
        }

        return true;
    }

    private static bool TryReadHeader(string sourcePath, out byte[] header)
    {
        header = new byte[6];
        try
        {
            using var stream = File.OpenRead(sourcePath);
            return stream.Read(header, 0, header.Length) == header.Length;
        }
        catch
        {
            header = [];
            return false;
        }
    }

    private static (int Width, int Height)? TryReadImageSize(Stream stream)
    {
        Span<byte> header = stackalloc byte[30];
        var read = stream.Read(header);

        if (read >= 24 && header[..8].SequenceEqual(PngHeader))
        {
            var width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
            var height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
            return CreateImageSize(width, height);
        }

        if (read >= 10 &&
            (header[..6].SequenceEqual(Gif87aHeader) || header[..6].SequenceEqual(Gif89aHeader)))
        {
            var width = BinaryPrimitives.ReadUInt16LittleEndian(header[6..8]);
            var height = BinaryPrimitives.ReadUInt16LittleEndian(header[8..10]);
            return CreateImageSize(width, height);
        }

        if (read >= 30 &&
            header[..4].SequenceEqual("RIFF"u8) &&
            header[8..12].SequenceEqual("WEBP"u8))
        {
            if (TryReadWebPSize(header[..read]) is { } webPSize)
                return webPSize;
        }

        if (read >= 2 && header[0] == 0xFF && header[1] == 0xD8)
        {
            return TryReadJpegSize(stream);
        }

        return null;
    }

    private static (int Width, int Height)? TryReadJpegSize(Stream stream)
    {
        stream.Position = 2;
        while (stream.Position < stream.Length)
        {
            var markerPrefix = stream.ReadByte();
            if (markerPrefix < 0)
                return null;

            if (markerPrefix != 0xFF)
                continue;

            int marker;
            do
            {
                marker = stream.ReadByte();
            } while (marker == 0xFF);

            if (marker < 0 || marker is 0xD9 or 0xDA)
                return null;

            if (marker is 0x01 or >= 0xD0 and <= 0xD7)
                continue;

            var segmentLength = ReadBigEndianUInt16(stream);
            if (segmentLength < 2)
                return null;

            if (IsJpegStartOfFrameMarker(marker))
            {
                if (stream.ReadByte() < 0)
                    return null;

                var height = ReadBigEndianUInt16(stream);
                var width = ReadBigEndianUInt16(stream);
                return CreateImageSize(width, height);
            }

            stream.Seek(segmentLength - 2, SeekOrigin.Current);
        }

        return null;
    }

    private static (int Width, int Height)? TryReadWebPSize(ReadOnlySpan<byte> header)
    {
        if (header[12..16].SequenceEqual("VP8X"u8) && header.Length >= 30)
        {
            var width = ReadLittleEndian24(header[24..27]) + 1;
            var height = ReadLittleEndian24(header[27..30]) + 1;
            return CreateImageSize(width, height);
        }

        if (header[12..16].SequenceEqual("VP8L"u8) && header.Length >= 25 && header[20] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(header[21..25]);
            var width = (int)(bits & 0x3FFF) + 1;
            var height = (int)((bits >> 14) & 0x3FFF) + 1;
            return CreateImageSize(width, height);
        }

        if (header[12..16].SequenceEqual("VP8 "u8) &&
            header.Length >= 30 &&
            header[23] == 0x9D &&
            header[24] == 0x01 &&
            header[25] == 0x2A)
        {
            var width = BinaryPrimitives.ReadUInt16LittleEndian(header[26..28]) & 0x3FFF;
            var height = BinaryPrimitives.ReadUInt16LittleEndian(header[28..30]) & 0x3FFF;
            return CreateImageSize(width, height);
        }

        return null;
    }

    private static int ReadBigEndianUInt16(Stream stream)
    {
        var high = stream.ReadByte();
        var low = stream.ReadByte();
        return high < 0 || low < 0 ? -1 : (high << 8) | low;
    }

    private static int ReadLittleEndian24(ReadOnlySpan<byte> value)
    {
        return value[0] | (value[1] << 8) | (value[2] << 16);
    }

    private static bool IsJpegStartOfFrameMarker(int marker)
    {
        return marker is >= 0xC0 and <= 0xC3
            or >= 0xC5 and <= 0xC7
            or >= 0xC9 and <= 0xCB
            or >= 0xCD and <= 0xCF;
    }

    private static (int Width, int Height)? CreateImageSize(int width, int height)
    {
        return width > 0 && height > 0 ? (width, height) : null;
    }

    private static void DecodeMarketFaceBytes(byte[] bytes)
    {
        var index = 0;
        while (index < bytes.Length)
        {
            var xorEnd = Math.Min(index + MarketFaceXorChunkSize, bytes.Length);
            for (; index < xorEnd; index++)
            {
                bytes[index] ^= 0xFF;
            }

            index = Math.Min(index + MarketFaceKeepChunkSize, bytes.Length);
        }
    }
}
