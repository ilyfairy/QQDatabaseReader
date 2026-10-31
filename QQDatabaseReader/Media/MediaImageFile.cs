using QQDatabaseReader.Archives;

namespace QQDatabaseReader.Media;

/// <summary>按文件头识别图片, 以及 QQNT 商城表情本体文件的解混淆. 支持 tar / zip 归档内的路径</summary>
public static class MediaImageFile
{
    private const int MarketFaceXorChunkSize = 20;
    private const int MarketFaceKeepChunkSize = 30;
    private static readonly byte[] Gif87aHeader = "GIF87a"u8.ToArray();
    private static readonly byte[] Gif89aHeader = "GIF89a"u8.ToArray();
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>文件存在并且是 GIF, PNG, JPEG, WebP 或混淆过的商城表情 GIF</summary>
    public static bool IsImageFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !ArchiveFile.Exists(path))
            return false;

        Span<byte> header = stackalloc byte[12];
        try
        {
            using var stream = ArchiveFile.OpenRead(path);
            var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            return IsImageHeader(header[..read]);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsImageHeader(ReadOnlySpan<byte> value) =>
        value.StartsWith(Gif87aHeader) ||
        value.StartsWith(Gif89aHeader) ||
        value.StartsWith(PngHeader.AsSpan(0, 4)) ||
        value.Length >= 3 && value[0] == 0xFF && value[1] == 0xD8 && value[2] == 0xFF ||
        value.StartsWith("RIFF"u8) && value.Length >= 12 && value[8..12].SequenceEqual("WEBP"u8) ||
        IsMarketFaceEncodedGifBytes(value);

    /// <summary>QQNT 商城表情本体文件按 "20 字节 XOR 0xFF, 30 字节原样保留" 循环混淆, GIF 头落在第一段 XOR 区</summary>
    public static bool IsMarketFaceEncodedGifBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Gif89aHeader.Length)
            return false;

        for (var i = 0; i < Gif89aHeader.Length; i++)
        {
            var value = (byte)(bytes[i] ^ 0xFF);
            if (value != Gif87aHeader[i] && value != Gif89aHeader[i])
                return false;
        }

        return true;
    }

    /// <summary>原地还原混淆过的商城表情 (从文件开头算起)</summary>
    public static void DecodeMarketFaceBytes(Span<byte> bytes)
    {
        var index = 0;
        while (index < bytes.Length)
        {
            var xorEnd = Math.Min(index + MarketFaceXorChunkSize, bytes.Length);
            for (; index < xorEnd; index++)
                bytes[index] ^= 0xFF;

            index = Math.Min(index + MarketFaceKeepChunkSize, bytes.Length);
        }
    }

    /// <summary>读取图片的全部内容, 混淆过的商城表情还原成 GIF</summary>
    public static byte[] ReadImageBytes(string path)
    {
        var bytes = ArchiveFile.ReadAllBytes(path);
        if (IsMarketFaceEncodedGifBytes(bytes))
            DecodeMarketFaceBytes(bytes);
        return bytes;
    }
}
