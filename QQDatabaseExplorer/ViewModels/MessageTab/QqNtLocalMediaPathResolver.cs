using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QQDatabaseExplorer.Controls;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;
using QQDatabaseReader;
using QQDatabaseReader.Database;
using QQDatabaseReader.Archives;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal static class QqNtLocalMediaPathResolver
{
    public static string? ResolveImagePath(
        string? ntDataPath,
        int messageTime,
        QQMessageSegment segment,
        SubMessageType subMessageType)
    {
        if (!TryResolveNtDataRoot(ntDataPath, out var ntDataRoot))
            return null;

        var candidateFileNames = MessageMediaFileNameCandidateFactory.CreateImageCandidates(segment).ToArray();
        if (candidateFileNames.Length == 0)
            return ResolveExplicitMessageImagePath(ntDataRoot, segment.ImageLocalPath);

        if (!TryGetMessageUtcMonth(messageTime, out var month))
            return ResolveExplicitMessageImagePath(ntDataRoot, segment.ImageLocalPath);

        var primaryDir = GetMediaMonthDirectory(ntDataRoot, subMessageType, month);
        var result = ResolveMediaPath(ntDataRoot, primaryDir, candidateFileNames);
        if (result is not null)
            return result;

        // QQNT 有时会把 sticker 的图片放在 Pic，反之亦然。
        var fallbackDir = MessageMediaSegmentClassifier.IsStickerMessage(subMessageType)
            ? Path.Combine(ntDataRoot, "Pic", month)
            : Path.Combine(ntDataRoot, "Emoji", "emoji-recv", month);

        result = ResolveMediaPath(ntDataRoot, fallbackDir, candidateFileNames);
        return result ?? ResolveExplicitMessageImagePath(ntDataRoot, segment.ImageLocalPath);
    }

    public static string? ResolveVoicePath(
        string? ntDataPath,
        int messageTime,
        QQMessageSegment segment)
    {
        if (!TryResolveNtDataRoot(ntDataPath, out var ntDataRoot) ||
            !TryGetMessageUtcMonth(messageTime, out var month))
        {
            return null;
        }

        var voiceDirectory = Path.Combine(ntDataRoot, "Ptt", month, "Ori");
        return ResolveVoicePath(
            ntDataRoot,
            voiceDirectory,
            MessageMediaFileNameCandidateFactory.CreateVoiceCandidates(segment));
    }

    public static ResolvedVideoMediaPath ResolveVideoPath(
        string? ntDataPath,
        int messageTime,
        QQMessageSegment segment)
    {
        if (!TryResolveNtDataRoot(ntDataPath, out var ntDataRoot) ||
            !TryGetMessageUtcMonth(messageTime, out var month))
        {
            return ResolvedVideoMediaPath.Missing;
        }

        var videoMonthDirectory = Path.Combine(ntDataRoot, "Video", month);
        var videoPath = ResolveMediaOriginalPath(
            ntDataRoot,
            videoMonthDirectory,
            MessageMediaFileNameCandidateFactory.CreateVideoCandidates(segment).ToArray());
        var coverPath = ResolveMediaThumbnailPath(
            ntDataRoot,
            videoMonthDirectory,
            MessageMediaFileNameCandidateFactory.CreateVideoCoverCandidates(segment).ToArray())
            ?? ResolveExplicitLocalImagePathWithinSubdirectories(
                ntDataRoot,
                segment.ImageLocalPath,
                "Video",
                "Pic",
                "Emoji");

        return new ResolvedVideoMediaPath(
            videoPath,
            coverPath,
            !string.IsNullOrWhiteSpace(videoPath) && ArchiveFile.Exists(videoPath),
            LocalImageFile.IsDisplayableImageFile(coverPath));
    }

    public static string? ResolveExplicitLocalImagePath(
        string? imageLocalPath,
        params string?[] trustedRoots)
    {
        if (string.IsNullOrWhiteSpace(imageLocalPath))
            return null;

        foreach (var root in trustedRoots)
        {
            if (!string.IsNullOrWhiteSpace(root) &&
                TrustedMediaPath.TryResolveExistingFileWithinRoot(root, imageLocalPath, out var resolvedPath))
            {
                return resolvedPath;
            }
        }

        return null;
    }

    private static string? ResolveExplicitMessageImagePath(string ntDataRoot, string? imageLocalPath)
    {
        return ResolveExplicitLocalImagePathWithinSubdirectories(
            ntDataRoot,
            imageLocalPath,
            "Pic",
            "Emoji");
    }

    private static string? ResolveExplicitLocalImagePathWithinSubdirectories(
        string ntDataRoot,
        string? imageLocalPath,
        params string[] subdirectories)
    {
        if (string.IsNullOrWhiteSpace(imageLocalPath))
            return null;

        foreach (var subdirectory in subdirectories)
        {
            if (TrustedMediaPath.TryResolveExistingFileWithinSubdirectory(
                    ntDataRoot,
                    subdirectory,
                    imageLocalPath,
                    out var resolvedPath))
            {
                return resolvedPath;
            }
        }

        return null;
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

    private static string? ResolveOriginalImagePath(
        string trustedRoot,
        string picMonthDirectory,
        string imageFileName)
    {
        var oriDirectory = Path.Combine(picMonthDirectory, "Ori");
        return ResolveExistingFile(trustedRoot, oriDirectory, imageFileName) ??
               ResolveExistingFile(trustedRoot, oriDirectory, imageFileName.ToLowerInvariant());
    }

    private static string? ResolveMediaPath(
        string trustedRoot,
        string mediaMonthDirectory,
        IReadOnlyList<string> candidateFileNames)
    {
        var originalPath = ResolveMediaOriginalPath(trustedRoot, mediaMonthDirectory, candidateFileNames);
        if (originalPath is not null)
            return originalPath;

        return ResolveMediaThumbnailPath(trustedRoot, mediaMonthDirectory, candidateFileNames);
    }

    private static string? ResolveMediaOriginalPath(
        string trustedRoot,
        string mediaMonthDirectory,
        IReadOnlyList<string> candidateFileNames)
    {
        foreach (var imageFileName in candidateFileNames)
        {
            var result = ResolveOriginalImagePath(trustedRoot, mediaMonthDirectory, imageFileName);
            if (result is not null)
                return result;
        }

        return null;
    }

    private static string? ResolveMediaThumbnailPath(
        string trustedRoot,
        string mediaMonthDirectory,
        IReadOnlyList<string> candidateFileNames)
    {
        foreach (var imageFileName in candidateFileNames)
        {
            var thumbDirectory = Path.Combine(mediaMonthDirectory, "Thumb");
            var directResult = ResolveExistingFile(trustedRoot, thumbDirectory, imageFileName) ??
                               ResolveExistingFile(trustedRoot, thumbDirectory, imageFileName.ToLowerInvariant());
            if (directResult is not null)
                return directResult;

            var result = ResolveThumbnailImagePath(trustedRoot, mediaMonthDirectory, imageFileName);
            if (result is not null)
                return result;
        }

        return null;
    }

    private static string? ResolveVoicePath(
        string trustedRoot,
        string voiceOriginalDirectory,
        IEnumerable<string> candidateFileNames)
    {
        if (!TrustedMediaPath.TryResolveExistingDirectoryWithinRoot(
                trustedRoot,
                voiceOriginalDirectory,
                out var resolvedVoiceDirectory))
        {
            return null;
        }

        foreach (var fileName in candidateFileNames)
        {
            var result = ResolveExistingFile(trustedRoot, resolvedVoiceDirectory, fileName);
            if (result is not null)
                return result;
        }

        return null;
    }

    private static string? ResolveThumbnailImagePath(
        string trustedRoot,
        string picMonthDirectory,
        string imageFileName)
    {
        var thumbDirectory = Path.Combine(picMonthDirectory, "Thumb");
        if (!TrustedMediaPath.TryResolveExistingDirectoryWithinRoot(
                trustedRoot,
                thumbDirectory,
                out var resolvedThumbDirectory))
        {
            return null;
        }

        var imageName = Path.GetFileNameWithoutExtension(imageFileName);
        if (string.IsNullOrWhiteSpace(imageName))
            return null;

        var preferredExtension = Path.GetExtension(imageFileName);
        var thumbNamePrefix = imageName.ToLowerInvariant();
        var candidates = ArchiveFile.EnumerateFiles(resolvedThumbDirectory, $"{thumbNamePrefix}_*.*")
            .Select(path => TrustedMediaPath.TryResolveExistingFileWithinRoot(
                    trustedRoot,
                    path,
                    out var resolvedPath)
                ? CreateThumbnailCandidate(resolvedPath, thumbNamePrefix, preferredExtension)
                : null)
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

    private static string? ResolveExistingFile(string trustedRoot, string directory, string fileName)
    {
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
