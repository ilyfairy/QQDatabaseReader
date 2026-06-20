using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using QQDatabaseExplorer.Controls;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal static class QqNtMarketFacePathResolver
{
    public static string? ResolvePath(string? ntDataPath, QQMessageSegment segment)
    {
        if (string.IsNullOrWhiteSpace(ntDataPath) ||
            segment.MarketFacePackageId is not { } packageId)
        {
            return null;
        }

        var packageDirectory = Path.Combine(ntDataPath, "Emoji", "marketface", packageId.ToString());
        if (!Directory.Exists(packageDirectory))
            return null;

        var imageId = segment.MarketFaceImageId;
        var result = string.IsNullOrWhiteSpace(imageId)
            ? null
            : ResolveImagePath(packageDirectory, imageId);
        if (result is not null)
            return result;

        imageId = ResolveImageIdFromMetadata(ntDataPath, packageId, segment.MarketFaceName);
        return string.IsNullOrWhiteSpace(imageId)
            ? null
            : ResolveImagePath(packageDirectory, imageId);
    }

    private static string? ResolveImagePath(string packageDirectory, string imageId)
    {
        var normalizedImageId = NormalizeImageId(imageId);
        if (string.IsNullOrWhiteSpace(normalizedImageId))
            return null;

        var originalPath = ResolveExistingFile(packageDirectory, normalizedImageId);
        if (LocalImageFile.IsDisplayableImageFile(originalPath))
            return originalPath;

        // 商城表情只认 nt_data/Emoji/marketface/<表情包 ID>/<图片 ID> 这个本体文件；
        // 同目录里的静态预览图不能拿来替代动图本体。
        return null;
    }

    private static string? ResolveImageIdFromMetadata(
        string ntDataPath,
        int packageId,
        string? marketFaceName)
    {
        var normalizedName = NormalizeName(marketFaceName);
        if (string.IsNullOrWhiteSpace(normalizedName))
            return null;

        var metadataPath = Path.Combine(ntDataPath, "Emoji", "marketface", "json", $"{packageId}.jtmp");
        if (!File.Exists(metadataPath))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            if (!document.RootElement.TryGetProperty("imgs", out var images) ||
                images.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var image in images.EnumerateArray())
            {
                if (!IsMatchingName(image, normalizedName))
                    continue;

                if (TryGetImageId(image, out var imageId))
                    return imageId;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool TryGetImageId(JsonElement image, out string imageId)
    {
        imageId = string.Empty;
        if (!image.TryGetProperty("id", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        imageId = NormalizeImageId(idElement.GetString());
        return !string.IsNullOrWhiteSpace(imageId);
    }

    private static bool IsMatchingName(JsonElement image, string normalizedName)
    {
        if (image.TryGetProperty("name", out var nameElement) &&
            nameElement.ValueKind == JsonValueKind.String &&
            string.Equals(NormalizeName(nameElement.GetString()), normalizedName, StringComparison.Ordinal))
        {
            return true;
        }

        if (!image.TryGetProperty("keywords", out var keywordsElement) ||
            keywordsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return keywordsElement
            .EnumerateArray()
            .Any(keyword =>
                keyword.ValueKind == JsonValueKind.String &&
                string.Equals(NormalizeName(keyword.GetString()), normalizedName, StringComparison.Ordinal));
    }

    private static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var normalized = name.Trim();
        while (normalized.Length >= 2 &&
               ((normalized[0] == '[' && normalized[^1] == ']') ||
                (normalized[0] == '【' && normalized[^1] == '】')))
        {
            normalized = normalized[1..^1].Trim();
        }

        return normalized;
    }

    private static string NormalizeImageId(string? imageId)
    {
        if (string.IsNullOrWhiteSpace(imageId))
            return string.Empty;

        return Path.GetFileNameWithoutExtension(imageId.Trim()).ToLowerInvariant();
    }

    private static string? ResolveExistingFile(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        return File.Exists(path) ? path : null;
    }
}
