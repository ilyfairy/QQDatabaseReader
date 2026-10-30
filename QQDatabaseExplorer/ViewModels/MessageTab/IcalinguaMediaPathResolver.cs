using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QQDatabaseExplorer.Controls;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal static class IcalinguaMediaPathResolver
{
    private static readonly string[] MediaDirectoryNames =
    [
        "downloads",
        "download",
        "images",
        "image",
        "files",
        "records",
        "stickers",
    ];

    public static string? ResolveLocalFilePath(
        IcalinguaMessageFile file,
        AvaQQGroup? conversation,
        IcalinguaMessageDatabaseSet? databases)
    {
        var roots = CreateMediaSearchRoots(conversation, databases).ToArray();
        foreach (var candidate in CreateLocalFileCandidates(file, roots))
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    public static string? ResolveVideoCoverPath(
        IcalinguaMessageFile file,
        string? videoPath,
        AvaQQGroup? conversation,
        IcalinguaMessageDatabaseSet? databases)
    {
        var roots = CreateMediaSearchRoots(conversation, databases).ToArray();
        var coverPath = ResolveVideoCoverPathWithinRoots(videoPath, roots);
        if (coverPath is not null)
            return coverPath;

        return IsImageType(file.Type)
            ? ResolveLocalFilePath(file, conversation, databases)
            : null;
    }

    private static string? ResolveVideoCoverPathWithinRoots(
        string? videoPath,
        IReadOnlyList<string> roots)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
            return null;

        foreach (var root in roots)
        {
            if (!TrustedMediaPath.TryResolveExistingFileWithinRoot(root, videoPath, out var resolvedVideoPath))
                continue;

            var directory = Path.GetDirectoryName(resolvedVideoPath);
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(resolvedVideoPath);
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(nameWithoutExtension))
                continue;

            foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".webp" })
            {
                var candidate = Path.Combine(directory, nameWithoutExtension + extension);
                if (TrustedMediaPath.TryResolveExistingFileWithinRoot(
                        root,
                        candidate,
                        out var resolvedCoverPath) &&
                    LocalImageFile.IsDisplayableImageFile(resolvedCoverPath))
                {
                    return resolvedCoverPath;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> CreateLocalFileCandidates(
        IcalinguaMessageFile file,
        IReadOnlyList<string> roots)
    {
        foreach (var rawPath in new[] { file.Url, file.Name, file.Fid })
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                continue;

            var normalized = rawPath.Trim();
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                foreach (var root in roots)
                {
                    if (TrustedMediaPath.TryResolveWithinRoot(root, uri.LocalPath, out var localPath))
                        yield return localPath;
                }
                continue;
            }

            if (Path.IsPathFullyQualified(normalized))
            {
                foreach (var root in roots)
                {
                    if (TrustedMediaPath.TryResolveWithinRoot(root, normalized, out var localPath))
                        yield return localPath;
                }
            }
        }

        var fileNames = CreateFileNameCandidates(file).ToArray();
        foreach (var root in roots)
        {
            foreach (var fileName in fileNames)
            {
                if (TrustedMediaPath.TryResolveWithinRoot(root, fileName, out var localPath))
                    yield return localPath;
            }
        }
    }

    private static IEnumerable<string> CreateFileNameCandidates(IcalinguaMessageFile file)
    {
        foreach (var value in new[] { file.Name, file.Fid, TryGetUrlFileName(file.Url) })
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var fileName = Path.GetFileName(value);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                yield return fileName;
                if (file.Type?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
                    yield return "QQ_Record_" + fileName;
            }
        }
    }

    private static string? TryGetUrlFileName(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Path.GetFileName(uri.LocalPath);

        return Path.GetFileName(url);
    }

    private static IEnumerable<string> CreateMediaSearchRoots(
        AvaQQGroup? conversation,
        IcalinguaMessageDatabaseSet? databases)
    {
        var basePaths = new List<string>();
        void AddBasePath(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                basePaths.Add(Path.GetFullPath(path));
        }

        if (databases is not null)
        {
            foreach (var dataPath in databases.DataPaths)
                AddBasePath(dataPath);
            foreach (var entry in databases.Databases)
            {
                var databaseDirectory = Path.GetDirectoryName(entry.Reader.DatabaseFilePath);
                if (IsIcalinguaDatabaseDirectory(databaseDirectory))
                    AddBasePath(Path.GetDirectoryName(Path.GetFullPath(databaseDirectory!)));
                else
                    AddBasePath(databaseDirectory);
            }
        }

        return CreateTrustedMediaRoots(basePaths, conversation?.IcalinguaDownloadPath);
    }

    private static IReadOnlyList<string> CreateTrustedMediaRoots(
        IEnumerable<string> basePaths,
        string? conversationDownloadPath)
    {
        var roots = new List<string>();
        void AddRoot(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                roots.Add(Path.GetFullPath(path));
        }

        void AddDerivedRoot(string anchor, string path)
        {
            if (Directory.Exists(path) &&
                TrustedMediaPath.TryResolveSubdirectoryWithinAnchor(anchor, path, out var resolvedPath))
            {
                roots.Add(resolvedPath);
            }
        }

        foreach (var basePath in basePaths)
        {
            var fullBasePath = Path.GetFullPath(basePath);
            if (IsMediaDirectory(fullBasePath))
                AddRoot(fullBasePath);

            foreach (var directoryName in MediaDirectoryNames)
            {
                AddDerivedRoot(fullBasePath, Path.Combine(fullBasePath, directoryName));
                AddDerivedRoot(fullBasePath, Path.Combine(fullBasePath, "data", directoryName));
            }
        }

        var distinctRoots = roots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!string.IsNullOrWhiteSpace(conversationDownloadPath))
        {
            try
            {
                var fullDownloadPath = Path.GetFullPath(conversationDownloadPath);
                if (Directory.Exists(fullDownloadPath) &&
                    distinctRoots.Any(root =>
                        string.Equals(fullDownloadPath, root, StringComparison.OrdinalIgnoreCase) ||
                        TrustedMediaPath.TryResolveWithinRoot(root, fullDownloadPath, out _)))
                {
                    distinctRoots.Add(fullDownloadPath);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
            }
        }

        return distinctRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsIcalinguaDatabaseDirectory(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
               string.Equals(Path.GetFileName(Path.GetFullPath(path)), "databases", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMediaDirectory(string path)
    {
        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return MediaDirectoryNames.Contains(directoryName, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsImageType(string? type)
    {
        return !string.IsNullOrWhiteSpace(type) &&
               type.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    }
}
