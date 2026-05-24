using System;
using System.IO;

namespace QQDatabaseExplorer.Services;

public static class AvatarCacheResolver
{
    public static string? ResolveStoredAvatarPath(string? storedPath, string? ntDataPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath) ||
            string.IsNullOrWhiteSpace(ntDataPath))
        {
            return null;
        }

        string ntDataFullPath;
        try
        {
            ntDataFullPath = Path.GetFullPath(ntDataPath);
        }
        catch
        {
            return null;
        }

        var avatarRoot = Path.Combine(ntDataFullPath, "avatar");
        if (TryUseExistingAvatarPath(storedPath, avatarRoot, out var existingPath))
            return existingPath;

        var avatarRelativePath = GetAvatarRelativePath(storedPath);
        if (avatarRelativePath is null)
            return null;

        var repairedPath = Path.GetFullPath(Path.Combine(ntDataFullPath, avatarRelativePath));
        return IsUnderDirectory(repairedPath, avatarRoot) && File.Exists(repairedPath)
            ? repairedPath
            : null;
    }

    private static bool TryUseExistingAvatarPath(
        string storedPath,
        string avatarRoot,
        out string? existingPath)
    {
        existingPath = null;

        try
        {
            var fullPath = Path.GetFullPath(storedPath);
            if (IsUnderDirectory(fullPath, avatarRoot) && File.Exists(fullPath))
            {
                existingPath = fullPath;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static string? GetAvatarRelativePath(string storedPath)
    {
        var normalizedPath = storedPath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var avatarPrefix = $"avatar{Path.DirectorySeparatorChar}";
        if (normalizedPath.StartsWith(avatarPrefix, StringComparison.OrdinalIgnoreCase))
            return normalizedPath;

        var avatarSegment = $"{Path.DirectorySeparatorChar}{avatarPrefix}";
        var segmentIndex = normalizedPath.IndexOf(avatarSegment, StringComparison.OrdinalIgnoreCase);
        return segmentIndex < 0
            ? null
            : normalizedPath[(segmentIndex + 1)..];
    }

    private static bool IsUnderDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory);
        if (!fullDirectory.EndsWith(Path.DirectorySeparatorChar))
            fullDirectory += Path.DirectorySeparatorChar;

        return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }
}
