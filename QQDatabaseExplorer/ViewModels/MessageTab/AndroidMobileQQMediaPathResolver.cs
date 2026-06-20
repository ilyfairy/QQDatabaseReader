using System;
using System.IO;
using QQDatabaseExplorer.Controls;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal static class AndroidMobileQQMediaPathResolver
{
    private const long ImageCacheCrc64Polynomial = -7661587058870466123L;
    private static readonly Lazy<long[]> ImageCacheCrc64Table = new(CreateImageCacheCrc64Table);

    public static string? ResolveImagePath(string? mobileQQPath, QQMessageSegment segment)
    {
        if (string.IsNullOrWhiteSpace(mobileQQPath) ||
            segment.ImageMd5 is not { Length: > 0 } imageMd5)
        {
            return null;
        }

        var chatPicPath = Path.Combine(mobileQQPath, "chatpic");
        var md5 = Convert.ToHexString(imageMd5);
        foreach (var folderName in new[] { "chatraw", "chatimg", "chatthumb" })
        {
            var cacheName = CreateImageCacheName(folderName, md5);
            var candidatePath = Path.Combine(chatPicPath, folderName, cacheName[^3..], cacheName);
            if (LocalImageFile.IsDisplayableImageFile(candidatePath))
                return candidatePath;
        }

        return null;
    }

    private static string CreateImageCacheName(string folderName, string md5)
    {
        var crc64 = ComputeImageCacheCrc64($"{folderName}:{md5.ToUpperInvariant()}");
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
