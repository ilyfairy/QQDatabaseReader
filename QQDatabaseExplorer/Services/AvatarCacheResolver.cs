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

        if (!TrustedMediaPath.TryResolveSubdirectoryWithinAnchor(
                ntDataFullPath,
                Path.Combine(ntDataFullPath, "avatar"),
                out var avatarRoot))
        {
            return null;
        }

        if (TryUseExistingAvatarPath(storedPath, avatarRoot, out var existingPath))
            return existingPath;

        var avatarRelativePath = GetAvatarRelativePath(storedPath);
        if (avatarRelativePath is null)
            return null;

        var repairedPath = Path.Combine(ntDataFullPath, avatarRelativePath);
        return TrustedMediaPath.TryResolveExistingFileWithinRoot(
            avatarRoot,
            repairedPath,
            out var resolvedPath)
            ? resolvedPath
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
            if (TrustedMediaPath.TryResolveExistingFileWithinRoot(
                    avatarRoot,
                    Path.GetFullPath(storedPath),
                    out var fullPath))
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
}
