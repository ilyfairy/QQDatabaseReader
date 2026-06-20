using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QQDatabaseExplorer.Controls;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal static class QqNtLocalMediaPathResolver
{
    public static string? ResolveImagePath(
        string? ntDataPath,
        int messageTime,
        QQMessageSegment segment,
        SubMessageType subMessageType)
    {
        if (string.IsNullOrWhiteSpace(ntDataPath))
            return ResolveExplicitLocalImagePath(segment.ImageLocalPath);

        var candidateFileNames = MessageMediaFileNameCandidateFactory.CreateImageCandidates(segment).ToArray();
        if (candidateFileNames.Length == 0)
            return ResolveExplicitLocalImagePath(segment.ImageLocalPath);

        if (!TryGetMessageUtcMonth(messageTime, out var month))
            return ResolveExplicitLocalImagePath(segment.ImageLocalPath);

        var primaryDir = GetMediaMonthDirectory(ntDataPath, subMessageType, month);
        var result = ResolveMediaPath(primaryDir, candidateFileNames);
        if (result is not null)
            return result;

        // QQNT 有时会把 sticker 的图片放在 Pic，反之亦然。
        var fallbackDir = MessageMediaSegmentClassifier.IsStickerMessage(subMessageType)
            ? Path.Combine(ntDataPath, "Pic", month)
            : Path.Combine(ntDataPath, "Emoji", "emoji-recv", month);

        result = ResolveMediaPath(fallbackDir, candidateFileNames);
        return result ?? ResolveExplicitLocalImagePath(segment.ImageLocalPath);
    }

    public static string? ResolveVoicePath(
        string? ntDataPath,
        int messageTime,
        QQMessageSegment segment)
    {
        if (string.IsNullOrWhiteSpace(ntDataPath) ||
            !TryGetMessageUtcMonth(messageTime, out var month))
        {
            return null;
        }

        var voiceDirectory = Path.Combine(ntDataPath, "Ptt", month, "Ori");
        return ResolveVoicePath(voiceDirectory, MessageMediaFileNameCandidateFactory.CreateVoiceCandidates(segment));
    }

    public static ResolvedVideoMediaPath ResolveVideoPath(
        string? ntDataPath,
        int messageTime,
        QQMessageSegment segment)
    {
        if (string.IsNullOrWhiteSpace(ntDataPath) ||
            !TryGetMessageUtcMonth(messageTime, out var month))
        {
            return ResolvedVideoMediaPath.Missing;
        }

        var videoMonthDirectory = Path.Combine(ntDataPath, "Video", month);
        var videoPath = ResolveMediaOriginalPath(
            videoMonthDirectory,
            MessageMediaFileNameCandidateFactory.CreateVideoCandidates(segment).ToArray());
        var coverPath = ResolveMediaThumbnailPath(
            videoMonthDirectory,
            MessageMediaFileNameCandidateFactory.CreateVideoCoverCandidates(segment).ToArray())
            ?? ResolveExplicitLocalImagePath(segment.ImageLocalPath);

        return new ResolvedVideoMediaPath(
            videoPath,
            coverPath,
            !string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath),
            LocalImageFile.IsDisplayableImageFile(coverPath));
    }

    public static string? ResolveExplicitLocalImagePath(string? imageLocalPath)
    {
        return !string.IsNullOrWhiteSpace(imageLocalPath) && File.Exists(imageLocalPath)
            ? imageLocalPath
            : null;
    }

    private static string GetMediaMonthDirectory(string ntDataPath, SubMessageType subMessageType, string month)
    {
        return MessageMediaSegmentClassifier.IsStickerMessage(subMessageType)
            ? Path.Combine(ntDataPath, "Emoji", "emoji-recv", month)
            : Path.Combine(ntDataPath, "Pic", month);
    }

    private static bool TryGetMessageUtcMonth(int messageTime, out string month)
    {
        if (messageTime <= 0)
        {
            month = string.Empty;
            return false;
        }

        try
        {
            month = DateTimeOffset.FromUnixTimeSeconds(messageTime)
                .UtcDateTime
                .ToString("yyyy-MM");
            return true;
        }
        catch
        {
            month = string.Empty;
            return false;
        }
    }

    private static string? ResolveOriginalImagePath(string picMonthDirectory, string imageFileName)
    {
        var oriDirectory = Path.Combine(picMonthDirectory, "Ori");
        return ResolveExistingFile(oriDirectory, imageFileName) ??
               ResolveExistingFile(oriDirectory, imageFileName.ToLowerInvariant());
    }

    private static string? ResolveMediaPath(string mediaMonthDirectory, IReadOnlyList<string> candidateFileNames)
    {
        var originalPath = ResolveMediaOriginalPath(mediaMonthDirectory, candidateFileNames);
        if (originalPath is not null)
            return originalPath;

        return ResolveMediaThumbnailPath(mediaMonthDirectory, candidateFileNames);
    }

    private static string? ResolveMediaOriginalPath(string mediaMonthDirectory, IReadOnlyList<string> candidateFileNames)
    {
        foreach (var imageFileName in candidateFileNames)
        {
            var result = ResolveOriginalImagePath(mediaMonthDirectory, imageFileName);
            if (result is not null)
                return result;
        }

        return null;
    }

    private static string? ResolveMediaThumbnailPath(string mediaMonthDirectory, IReadOnlyList<string> candidateFileNames)
    {
        foreach (var imageFileName in candidateFileNames)
        {
            var thumbDirectory = Path.Combine(mediaMonthDirectory, "Thumb");
            var directResult = ResolveExistingFile(thumbDirectory, imageFileName) ??
                               ResolveExistingFile(thumbDirectory, imageFileName.ToLowerInvariant());
            if (directResult is not null)
                return directResult;

            var result = ResolveThumbnailImagePath(mediaMonthDirectory, imageFileName);
            if (result is not null)
                return result;
        }

        return null;
    }

    private static string? ResolveVoicePath(string voiceOriginalDirectory, IEnumerable<string> candidateFileNames)
    {
        if (!Directory.Exists(voiceOriginalDirectory))
            return null;

        foreach (var fileName in candidateFileNames)
        {
            var result = ResolveExistingFile(voiceOriginalDirectory, fileName);
            if (result is not null)
                return result;
        }

        return null;
    }

    private static string? ResolveThumbnailImagePath(string picMonthDirectory, string imageFileName)
    {
        var thumbDirectory = Path.Combine(picMonthDirectory, "Thumb");
        if (!Directory.Exists(thumbDirectory))
            return null;

        var imageName = Path.GetFileNameWithoutExtension(imageFileName);
        if (string.IsNullOrWhiteSpace(imageName))
            return null;

        var preferredExtension = Path.GetExtension(imageFileName);
        var thumbNamePrefix = imageName.ToLowerInvariant();
        var candidates = Directory.EnumerateFiles(thumbDirectory, $"{thumbNamePrefix}_*.*")
            .Select(path => CreateThumbnailCandidate(path, thumbNamePrefix, preferredExtension))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!.Value)
            .ToList();

        // QQNT 的 Thumb 图片命名是 <图片hash>_<规格>.扩展名。优先使用 _0 作为聊天预览图；
        // 如果 _0 不存在，就取数字规格最大的文件，例如 _720。
        return candidates
                   .Where(candidate => candidate.Spec == 0)
                   .OrderByDescending(candidate => candidate.MatchesPreferredExtension)
                   .Select(candidate => candidate.Path)
                   .FirstOrDefault()
               ?? candidates
                   .OrderByDescending(candidate => candidate.Spec)
                   .ThenByDescending(candidate => candidate.MatchesPreferredExtension)
                   .Select(candidate => candidate.Path)
                   .FirstOrDefault();
    }

    private static ThumbnailCandidate? CreateThumbnailCandidate(
        string path,
        string thumbNamePrefix,
        string preferredExtension)
    {
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
        if (!fileNameWithoutExtension.StartsWith($"{thumbNamePrefix}_", StringComparison.OrdinalIgnoreCase))
            return null;

        var specText = fileNameWithoutExtension[(thumbNamePrefix.Length + 1)..];
        if (!int.TryParse(specText, out var spec))
            return null;

        var matchesPreferredExtension = string.IsNullOrEmpty(preferredExtension) ||
                                        string.Equals(Path.GetExtension(path), preferredExtension, StringComparison.OrdinalIgnoreCase);
        return new ThumbnailCandidate(path, spec, matchesPreferredExtension);
    }

    private static string? ResolveExistingFile(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        return File.Exists(path) ? path : null;
    }
}
