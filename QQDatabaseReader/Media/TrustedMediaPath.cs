using System.Collections.Concurrent;
using QQDatabaseReader.Archives;

namespace QQDatabaseReader.Media;

/// <summary>
/// 把数据库里记录的媒体路径限制在可信的根目录内: 拒绝越出根目录和途经链接 (重解析点) 的路径.
/// 支持 tar / zip 归档内的路径
/// </summary>
public static class TrustedMediaPath
{
    // 已经确认过不是链接的目录. 每次定位媒体都要检查从盘符开始的每一级目录, 不缓存时大量的文件状态查询很慢;
    // 目录很少会变成链接, 进程内缓存
    private static readonly ConcurrentDictionary<string, bool> DirectoryReparseCache =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

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
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            currentPath = Path.Combine(currentPath, segments[index]);
            // 最后一级是要找的文件本身, 不缓存
            if (index < segments.Length - 1 ? IsReparsePointDirectory(currentPath) : IsReparsePoint(currentPath))
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
            if (IsReparsePointDirectory(currentPath))
                return true;
        }

        return false;
    }

    /// <summary>目录是不是链接; 存在的目录记住结果, 不存在的 (以后可能被创建) 每次都查</summary>
    private static bool IsReparsePointDirectory(string path)
    {
        if (DirectoryReparseCache.TryGetValue(path, out var cached))
            return cached;

        var result = IsReparsePoint(path);
        if (Directory.Exists(path))
        {
            if (DirectoryReparseCache.Count >= 100_000)
                DirectoryReparseCache.Clear();
            DirectoryReparseCache[path] = result;
        }

        return result;
    }

    private static bool IsReparsePoint(string path)
    {
        // tar / zip 里的路径没有链接
        if (ArchiveFile.IsArchivePath(path))
            return false;

        // 不存在时 Attributes 是 -1, 不抛异常 (异常比文件状态查询本身慢得多)
        var attributes = new FileInfo(path).Attributes;
        return (int)attributes != -1 && (attributes & FileAttributes.ReparsePoint) != 0;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
