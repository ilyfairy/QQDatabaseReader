namespace QQDatabaseReader.Media;

/// <summary>
/// 安卓 QQ (旧版 MobileQQ 和 QQNT) 在 sdcard 的 Tencent/MobileQQ 下的媒体文件.
/// 图片缓存在 chatpic/{chatraw,chatimg,chatthumb}/&lt;名字末 3 位&gt;/Cache_&lt;crc64&gt;, 名字由图片 md5 算出
/// </summary>
public static class AndroidQQMediaPathResolver
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
            if (TrustedMediaPath.TryResolveExistingFileWithinRoot(
                    resolvedChatPicPath,
                    candidatePath,
                    out var resolvedCandidatePath) &&
                MediaImageFile.IsImageFile(resolvedCandidatePath))
            {
                return resolvedCandidatePath;
            }
        }

        var legacyCacheName = CreateLegacyImageCacheName(md5);
        var legacyCandidatePath = Path.Combine(resolvedChatPicPath, "chatimg", legacyCacheName[^3..], legacyCacheName);
        if (TrustedMediaPath.TryResolveExistingFileWithinRoot(
                resolvedChatPicPath,
                legacyCandidatePath,
                out var resolvedLegacyCandidatePath) &&
            MediaImageFile.IsImageFile(resolvedLegacyCandidatePath))
        {
            return resolvedLegacyCandidatePath;
        }

        return null;
    }

    /// <summary>
    /// 语音: &lt;MobileQQ&gt;/&lt;QQ 号&gt;/ptt/&lt;文件名 或 md5.amr&gt;. 不知道 QQ 号 (<paramref name="accountUin"/> 为空) 时
    /// 依次找 MobileQQ 下每个 QQ 号的目录 (只支持普通目录, 不支持归档)
    /// </summary>
    public static string? ResolveVoicePath(string? mobileQQPath, string? accountUin, QQMessageSegment segment)
    {
        if (string.IsNullOrWhiteSpace(mobileQQPath))
            return null;

        var candidates = QQNtMediaFileNames.CreateVoiceCandidates(segment).ToArray();
        if (candidates.Length == 0)
            return null;

        IEnumerable<string> accounts;
        if (!string.IsNullOrWhiteSpace(accountUin))
        {
            accounts = [accountUin];
        }
        else
        {
            try
            {
                accounts = Directory.Exists(mobileQQPath)
                    ? Directory.EnumerateDirectories(mobileQQPath).Select(path => Path.GetFileName(path)).Where(name => name.Length > 0 && name.All(char.IsAsciiDigit))
                    : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        foreach (var account in accounts)
        {
            if (!TrustedMediaPath.TryResolveExistingDirectoryWithinRoot(mobileQQPath, Path.Combine(account, "ptt"), out var pttDirectory))
                continue;
            foreach (var fileName in candidates)
            {
                if (TrustedMediaPath.TryResolveExistingFileWithinRoot(pttDirectory, fileName, out var path))
                    return path;
            }
        }

        return null;
    }

    private static string? ResolveChatPicPath(string? mobileQQPath, string? chatPicPath)
    {
        if (!string.IsNullOrWhiteSpace(chatPicPath))
        {
            try
            {
                return Path.GetFullPath(chatPicPath);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(mobileQQPath))
            return null;

        return TrustedMediaPath.TryResolveSubdirectoryWithinAnchor(
            mobileQQPath,
            "chatpic",
            out var resolvedPath)
            ? resolvedPath
            : null;
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
