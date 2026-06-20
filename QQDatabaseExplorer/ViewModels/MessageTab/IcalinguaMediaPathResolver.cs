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
    public static string? ResolveLocalFilePath(
        IcalinguaMessageFile file,
        AvaQQGroup? conversation,
        IcalinguaMessageDatabaseSet? databases)
    {
        foreach (var candidate in CreateLocalFileCandidates(file, conversation, databases))
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
        if (!string.IsNullOrWhiteSpace(videoPath))
        {
            var directory = Path.GetDirectoryName(videoPath);
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(videoPath);
            if (!string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(nameWithoutExtension))
            {
                foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".webp" })
                {
                    var candidate = Path.Combine(directory, nameWithoutExtension + extension);
                    if (LocalImageFile.IsDisplayableImageFile(candidate))
                        return candidate;
                }
            }
        }

        return IsImageType(file.Type)
            ? ResolveLocalFilePath(file, conversation, databases)
            : null;
    }

    private static IEnumerable<string> CreateLocalFileCandidates(
        IcalinguaMessageFile file,
        AvaQQGroup? conversation,
        IcalinguaMessageDatabaseSet? databases)
    {
        foreach (var rawPath in new[] { file.Url, file.Name, file.Fid })
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                continue;

            var normalized = rawPath.Trim();
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                yield return uri.LocalPath;
                continue;
            }

            if (Path.IsPathFullyQualified(normalized))
                yield return normalized;
        }

        var fileNames = CreateFileNameCandidates(file).ToArray();
        var roots = CreateMediaSearchRoots(conversation, databases).ToArray();
        foreach (var root in roots)
        {
            foreach (var fileName in fileNames)
            {
                yield return Path.Combine(root, fileName);
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
        var roots = new List<string>();
        void AddRoot(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                roots.Add(Path.GetFullPath(path));
        }

        AddRoot(conversation?.IcalinguaDownloadPath);
        if (databases is not null)
        {
            foreach (var dataPath in databases.DataPaths)
                AddRoot(dataPath);
            foreach (var entry in databases.Databases)
            {
                var databaseDirectory = Path.GetDirectoryName(entry.Reader.DatabaseFilePath);
                AddRoot(databaseDirectory);
                if (IsIcalinguaDatabaseDirectory(databaseDirectory))
                    AddRoot(Path.GetDirectoryName(Path.GetFullPath(databaseDirectory!)));
            }
        }

        foreach (var root in roots.ToArray())
        {
            if (IsIcalinguaDataDirectory(root))
                AddRoot(Path.GetDirectoryName(Path.GetFullPath(root)));
        }

        foreach (var root in roots.ToArray())
        {
            AddRoot(Path.Combine(root, "downloads"));
            AddRoot(Path.Combine(root, "download"));
            AddRoot(Path.Combine(root, "images"));
            AddRoot(Path.Combine(root, "image"));
            AddRoot(Path.Combine(root, "files"));
            AddRoot(Path.Combine(root, "records"));
            AddRoot(Path.Combine(root, "stickers"));
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsIcalinguaDatabaseDirectory(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
               string.Equals(Path.GetFileName(Path.GetFullPath(path)), "databases", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIcalinguaDataDirectory(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
               string.Equals(Path.GetFileName(Path.GetFullPath(path)), "data", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImageType(string? type)
    {
        return !string.IsNullOrWhiteSpace(type) &&
               type.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    }
}
