using QQDatabaseReader.Archives;

namespace QQDatabaseReader.Media;

/// <summary>
/// 把数据库里记录的媒体路径限制在可信的根目录内: 拒绝越出根目录和途经链接 (重解析点) 的路径.
/// 支持 tar / zip 归档内的路径
/// </summary>
public static class TrustedMediaPath
{
    public static bool TryResolveWithinRoot(string root, string candidate, out string fullPath)
    {
        return TryResolveWithinRoot(root, candidate, rejectRootReparsePoint: true, out fullPath);
    }

    public static bool TryResolveExistingFileWithinRoot(string root, string candidate, out string fullPath)
    {
        if (TryResolveWithinRoot(root, candidate, out fullPath) && ArchiveFile.Exists(fullPath))
            return true;

        fullPath = string.Empty;
        return false;
    }

    public static bool TryResolveExistingDirectoryWithinRoot(string root, string candidate, out string fullPath)
    {
        if (TryResolveWithinRoot(root, candidate, out fullPath) && ArchiveFile.DirectoryExists(fullPath))
            return true;

        fullPath = string.Empty;
        return false;
    }

    public static bool TryResolveSubdirectoryWithinAnchor(string anchor, string candidate, out string fullPath)
    {
        return TryResolveWithinRoot(anchor, candidate, rejectRootReparsePoint: true, out fullPath);
    }

    public static bool TryResolveExistingFileWithinSubdirectory(
        string anchor,
        string subdirectory,
        string candidate,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (!TryResolveSubdirectoryWithinAnchor(anchor, subdirectory, out var trustedSubdirectory))
            return false;

        return TryResolveExistingFileWithinRoot(trustedSubdirectory, candidate, out fullPath);
    }

    private static bool TryResolveWithinRoot(
        string root,
        string candidate,
        bool rejectRootReparsePoint,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate))
            return false;

        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var resolvedCandidate = Path.IsPathFullyQualified(candidate)
                ? Path.GetFullPath(candidate)
                : Path.GetFullPath(Path.Combine(normalizedRoot, candidate));
            if (!IsSameOrChildPath(resolvedCandidate, normalizedRoot) ||
                string.Equals(resolvedCandidate, normalizedRoot, PathComparison) ||
                ContainsReparsePoint(
                    normalizedRoot,
                    resolvedCandidate,
                    includeRoot: rejectRootReparsePoint))
            {
                return false;
            }

            fullPath = resolvedCandidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsSameOrChildPath(string path, string root)
    {
        try
        {
            var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (string.Equals(normalizedPath, normalizedRoot, PathComparison))
                return true;

            var rootPrefix = Path.EndsInDirectorySeparator(normalizedRoot)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;
            return normalizedPath.StartsWith(rootPrefix, PathComparison);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool ContainsReparsePoint(string root, string path, bool includeRoot)
    {
        if (includeRoot && ContainsReparsePointFromPathRoot(root))
            return true;

        var relativePath = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relativePath) ||
            string.Equals(relativePath, "..", StringComparison.Ordinal) ||
            relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return true;
        }

        var currentPath = root;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (IsReparsePoint(currentPath))
                return true;
        }

        return false;
    }

    private static bool ContainsReparsePointFromPathRoot(string path)
    {
        var pathRoot = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(pathRoot))
            return true;

        var relativePath = Path.GetRelativePath(pathRoot, path);
        if (string.Equals(relativePath, ".", StringComparison.Ordinal))
            return IsReparsePoint(path);

        var currentPath = pathRoot;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (IsReparsePoint(currentPath))
                return true;
        }

        return false;
    }

    private static bool IsReparsePoint(string path)
    {
        // tar / zip 里的路径没有链接
        if (ArchiveFile.IsArchivePath(path))
            return false;

        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
