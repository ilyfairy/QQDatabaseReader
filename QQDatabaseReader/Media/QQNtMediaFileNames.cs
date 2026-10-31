namespace QQDatabaseReader.Media;

/// <summary>QQNT 媒体文件在本地可能的文件名: 消息里记录的文件名 (含小写, 去掉花括号和连字符的哈希名) 和按 md5 命名的文件</summary>
public static class QQNtMediaFileNames
{
    public static IEnumerable<string> CreateImageCandidates(QQMessageSegment segment)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fileName in CreateFileNameCandidates(segment.ImageFileName))
        {
            if (seen.Add(fileName))
                yield return fileName;
        }

        if (segment.ImageMd5 is { Length: > 0 } md5)
        {
            var extension = GetFileExtension(segment.ImageFileName);
            var md5Name = Convert.ToHexString(md5).ToLowerInvariant();
            var md5FileName = string.IsNullOrEmpty(extension) ? md5Name : md5Name + extension;
            if (seen.Add(md5FileName))
                yield return md5FileName;
        }
    }

    public static IEnumerable<string> CreateVoiceCandidates(QQMessageSegment segment)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in CreateFileNameCandidates(segment.VoiceFileName))
        {
            if (seen.Add(fileName))
                yield return fileName;
        }

        if (segment.VoiceMd5 is { Length: > 0 } md5)
        {
            var md5Name = Convert.ToHexString(md5).ToLowerInvariant();
            var extension = GetFileExtension(segment.VoiceFileName);
            if (string.IsNullOrWhiteSpace(extension))
                extension = ".amr";

            var md5FileName = md5Name + extension;
            if (seen.Add(md5FileName))
                yield return md5FileName;

            var amrFileName = md5Name + ".amr";
            if (seen.Add(amrFileName))
                yield return amrFileName;
        }
    }

    public static IEnumerable<string> CreateVideoCandidates(QQMessageSegment segment)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in CreateFileNameCandidates(segment.VideoFileName))
        {
            if (seen.Add(fileName))
                yield return fileName;
        }

        if (segment.VideoMd5 is { Length: > 0 } md5)
        {
            var md5Name = Convert.ToHexString(md5).ToLowerInvariant();
            var extension = GetFileExtension(segment.VideoFileName);
            if (string.IsNullOrWhiteSpace(extension))
                extension = ".mp4";

            var md5FileName = md5Name + extension;
            if (seen.Add(md5FileName))
                yield return md5FileName;

            var mp4FileName = md5Name + ".mp4";
            if (seen.Add(mp4FileName))
                yield return mp4FileName;
        }
    }

    public static IEnumerable<string> CreateVideoCoverCandidates(QQMessageSegment segment)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in CreateFileNameCandidates(segment.VideoCoverFileName))
        {
            if (seen.Add(fileName))
                yield return fileName;
        }

        if (segment.VideoMd5 is { Length: > 0 } md5)
        {
            var md5Name = Convert.ToHexString(md5).ToLowerInvariant();
            foreach (var fileName in new[] { $"{md5Name}_0.png", $"{md5Name}_0.jpg", $"{md5Name}.png", $"{md5Name}.jpg" })
            {
                if (seen.Add(fileName))
                    yield return fileName;
            }
        }
    }

    private static IEnumerable<string> CreateFileNameCandidates(string? rawFileName)
    {
        if (string.IsNullOrWhiteSpace(rawFileName))
            yield break;

        var fileName = Path.GetFileName(rawFileName);
        if (string.IsNullOrWhiteSpace(fileName))
            yield break;

        yield return fileName;

        var lowerFileName = fileName.ToLowerInvariant();
        if (!string.Equals(lowerFileName, fileName, StringComparison.Ordinal))
            yield return lowerFileName;

        var extension = Path.GetExtension(fileName);
        var normalizedName = NormalizeHashName(Path.GetFileNameWithoutExtension(fileName));
        if (!string.IsNullOrWhiteSpace(normalizedName))
        {
            yield return normalizedName + extension;

            var lowerNormalizedFileName = (normalizedName + extension).ToLowerInvariant();
            if (!string.Equals(lowerNormalizedFileName, normalizedName + extension, StringComparison.Ordinal))
                yield return lowerNormalizedFileName;
        }
    }

    private static string NormalizeHashName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var normalized = name.Trim();
        if (normalized.Length >= 2 && normalized[0] == '{' && normalized[^1] == '}')
            normalized = normalized[1..^1];

        normalized = normalized.Replace("-", string.Empty, StringComparison.Ordinal);
        return normalized.Length > 0 && normalized.All(char.IsAsciiHexDigit)
            ? normalized.ToLowerInvariant()
            : string.Empty;
    }

    private static string GetFileExtension(string? fileName)
    {
        return string.IsNullOrWhiteSpace(fileName)
            ? string.Empty
            : Path.GetExtension(Path.GetFileName(fileName)).ToLowerInvariant();
    }
}
