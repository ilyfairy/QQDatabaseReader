using System.Text.Json;

namespace QQDatabaseReader.Media;

/// <summary>PC QQNT 商城表情的本地文件: nt_data/Emoji/marketface/&lt;表情包 ID&gt;/&lt;图片 ID&gt; (内容是混淆过的, 见 <see cref="MediaImageFile"/>)</summary>
public static class QQNtMarketFacePathResolver
{
    public static string? ResolvePath(string? ntDataPath, QQMessageSegment segment)
    {
        if (!TryResolveNtDataRoot(ntDataPath, out var ntDataRoot) ||
            segment.MarketFacePackageId is not { } packageId)
        {
            return null;
        }

        var packageDirectory = Path.Combine(ntDataRoot, "Emoji", "marketface", packageId.ToString());
        if (!TrustedMediaPath.TryResolveExistingDirectoryWithinRoot(
                ntDataRoot,
                packageDirectory,
                out var resolvedPackageDirectory))
        {
            return null;
        }

        var imageId = segment.MarketFaceImageId;
        var result = string.IsNullOrWhiteSpace(imageId)
            ? null
            : ResolveImagePath(ntDataRoot, resolvedPackageDirectory, imageId);
        if (result is not null)
            return result;

        imageId = ResolveImageIdFromMetadata(ntDataRoot, packageId, segment.MarketFaceName);
        return string.IsNullOrWhiteSpace(imageId)
            ? null
            : ResolveImagePath(ntDataRoot, resolvedPackageDirectory, imageId);
    }

    private static string? ResolveImagePath(
        string trustedRoot,
        string packageDirectory,
        string imageId)
    {
        var normalizedImageId = NormalizeImageId(imageId);
        if (string.IsNullOrWhiteSpace(normalizedImageId))
            return null;

        var originalPath = ResolveExistingFile(trustedRoot, packageDirectory, normalizedImageId);
        if (MediaImageFile.IsImageFile(originalPath))
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
        if (!TrustedMediaPath.TryResolveExistingFileWithinRoot(
                ntDataPath,
                metadataPath,
                out var resolvedMetadataPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(resolvedMetadataPath));
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

    private static string? ResolveExistingFile(string trustedRoot, string directory, string fileName)
    {
        // 先查目录列表, 不在列表里的不用再逐个检查路径
        if (!MediaDirectoryListing.Contains(directory, fileName))
            return null;

        var path = Path.Combine(directory, fileName);
        return TrustedMediaPath.TryResolveExistingFileWithinRoot(trustedRoot, path, out var resolvedPath)
            ? resolvedPath
            : null;
    }

    private static bool TryResolveNtDataRoot(string? ntDataPath, out string ntDataRoot)
    {
        ntDataRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(ntDataPath))
            return false;

        try
        {
            ntDataRoot = Path.GetFullPath(ntDataPath);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}
