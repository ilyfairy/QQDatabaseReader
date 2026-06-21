using System;
using System.IO;
using QQDatabaseExplorer.Controls;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal static class AndroidMobileQQMediaPathResolver
{
    private const long ImageCacheCrc64Polynomial = -7661587058870466123L;
    private static readonly Lazy<long[]> ImageCacheCrc64Table = new(CreateImageCacheCrc64Table);

    public static string? ResolveImagePath(string? mobileQQPath, string? chatPicPath, QQMessageSegment segment)
    {
        if (segment.ImageMd5 is not { Length: > 0 } imageMd5)
        {
            return null;
        }

        return ResolveImagePath(mobileQQPath, chatPicPath, Convert.ToHexString(imageMd5));
    }

    public static string? ResolveImagePath(string? mobileQQPath, string? chatPicPath, string? imageMd5)
    {
        if (string.IsNullOrWhiteSpace(imageMd5))
            return null;

        var resolvedChatPicPath = ResolveChatPicPath(mobileQQPath, chatPicPath);
        if (string.IsNullOrWhiteSpace(resolvedChatPicPath))
            return null;

        var md5 = imageMd5.Trim();
        foreach (var folderName in new[] { "chatraw", "chatimg", "chatthumb" })
        {
            var cacheName = CreateImageCacheName(folderName, md5);
            var candidatePath = Path.Combine(resolvedChatPicPath, folderName, cacheName[^3..], cacheName);
            if (LocalImageFile.IsDisplayableImageFile(candidatePath))
                return candidatePath;
        }

        var legacyCacheName = CreateLegacyImageCacheName(md5);
        var legacyCandidatePath = Path.Combine(resolvedChatPicPath, "chatimg", legacyCacheName[^3..], legacyCacheName);
        if (LocalImageFile.IsDisplayableImageFile(legacyCandidatePath))
            return legacyCandidatePath;

        return null;
    }

    private static string? ResolveChatPicPath(string? mobileQQPath, string? chatPicPath)
    {
        if (!string.IsNullOrWhiteSpace(chatPicPath))
            return chatPicPath;

        return string.IsNullOrWhiteSpace(mobileQQPath)
            ? null
            : Path.Combine(mobileQQPath, "chatpic");
    }

    private static string CreateImageCacheName(string folderName, string md5)
    {
        var crc64 = ComputeImageCacheCrc64($"{folderName}:{md5.ToUpperInvariant()}");
        return "Cache_" + FormatImageCacheCrc64(crc64);
    }

    private static string CreateLegacyImageCacheName(string md5)
    {
        var crc64 = ComputeImageCacheCrc64($"chatimg:{md5}");
        return "Cache_" + FormatImageCacheCrc64(crc64);
    }

    private static long ComputeImageCacheCrc64(string value)
    {
        var table = ImageCacheCrc64Table.Value;
        var crc64 = -1L;
        foreach (var ch in value)
        {
            crc64 = table[((int)ch ^ (int)crc64) & 0xff] ^ (crc64 >> 8);
        }

        return crc64;
    }

    private static long[] CreateImageCacheCrc64Table()
    {
        var table = new long[256];
        for (var i = 0; i < table.Length; i++)
        {
            var value = (long)i;
            for (var j = 0; j < 8; j++)
            {
                value = (value & 1) != 0
                    ? (value >> 1) ^ ImageCacheCrc64Polynomial
                    : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    private static string FormatImageCacheCrc64(long value)
    {
        return value < 0
            ? "-" + unchecked((ulong)-value).ToString("x")
            : value.ToString("x");
    }
}
