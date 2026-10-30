using System.Diagnostics.CodeAnalysis;

namespace QQDatabaseReader.Archives;

/// <summary>
/// 普通文件和归档内文件的统一访问. 路径中某一段是存在的 .tar / .zip 文件时, 其后的部分是归档内路径,
/// 例如 D:\backup\data.tar\databases\nt_db\nt_qq_xxx\nt_msg.db 或 /mnt/backup/data.zip/files/a.png
/// </summary>
public static class ArchiveFile
{
    /// <summary>拆分归档路径; 不是归档内路径时返回 false</summary>
    public static bool TryResolve(string path, out ArchiveIndex archive, out string entryName)
    {
        archive = null!;
        entryName = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var fullPath = Path.GetFullPath(path);
        var position = 0;
        while (true)
        {
            var separator = fullPath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], position);
            if (separator < 0)
                return false;

            var prefix = fullPath[..separator];
            if (ArchiveIndex.IsArchiveFileName(prefix) && File.Exists(prefix))
            {
                entryName = ArchiveIndex.NormalizeName(fullPath[(separator + 1)..]);
                if (entryName.Length == 0)
                    return false;
                archive = ArchiveIndex.Open(prefix);
                return true;
            }

            position = separator + 1;
        }
    }

    public static bool IsArchivePath(string? path) => path is not null && TryResolve(path, out _, out _);

    /// <summary>找归档内的文件; 不是归档路径或文件不存在时返回 false</summary>
    public static bool TryGetEntry(string path, out ArchiveIndex archive, out ArchiveEntry entry)
    {
        entry = default;
        return TryResolve(path, out archive, out var name) && archive.TryFind(name, out entry);
    }

    /// <summary>普通文件或归档内文件是否存在. 归档损坏或读不了 (例如还没复制完) 时按不存在处理</summary>
    public static bool Exists([NotNullWhen(true)] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (File.Exists(path))
            return true;
        try
        {
            return TryGetEntry(path, out _, out _);
        }
        catch (Exception ex) when (IsArchiveReadError(ex))
        {
            return false;
        }
    }

    private static bool IsArchiveReadError(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or ArgumentException;

    /// <summary>打开普通文件或归档内文件 (只读)</summary>
    public static Stream OpenRead(string path)
    {
        if (File.Exists(path))
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (TryGetEntry(path, out var archive, out var entry))
            return archive.OpenRead(entry);
        throw new FileNotFoundException("文件不存在", path);
    }

    /// <summary>读取普通文件或归档内文件的全部内容</summary>
    public static byte[] ReadAllBytes(string path)
    {
        if (File.Exists(path))
            return File.ReadAllBytes(path);
        using var stream = OpenRead(path);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>把普通文件或归档内文件复制到 destinationPath</summary>
    public static void Copy(string sourcePath, string destinationPath, bool overwrite = false)
    {
        if (File.Exists(sourcePath))
        {
            File.Copy(sourcePath, destinationPath, overwrite);
            return;
        }

        using var source = OpenRead(sourcePath);
        using var destination = new FileStream(destinationPath, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(destination);
    }

    /// <summary>普通目录, 归档内的目录, 或者归档文件本身 (当作根目录) 是否存在</summary>
    public static bool DirectoryExists(string path)
    {
        if (Directory.Exists(path))
            return true;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var trimmed = Path.TrimEndingDirectorySeparator(path);
        if (ArchiveIndex.IsArchiveFileName(trimmed) && File.Exists(trimmed))
            return true;
        try
        {
            return TryResolve(trimmed, out var archive, out var name) && archive.DirectoryExists(name);
        }
        catch (Exception ex) when (IsArchiveReadError(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// 列出目录下直接包含的文件 (普通目录或归档内目录), 返回完整路径.
    /// searchPattern 支持 * 和 ?, 和 Directory.EnumerateFiles 一致
    /// </summary>
    public static IEnumerable<string> EnumerateFiles(string directory, string searchPattern = "*")
    {
        if (Directory.Exists(directory))
            return Directory.EnumerateFiles(directory, searchPattern);

        var trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        ArchiveIndex archive;
        string name;
        if (ArchiveIndex.IsArchiveFileName(trimmed) && File.Exists(trimmed))
        {
            archive = ArchiveIndex.Open(trimmed);
            name = string.Empty;
        }
        else if (!TryResolve(trimmed, out archive, out name))
        {
            return [];
        }

        return archive.GetDirectoryFiles(name)
            .Select(entry => entry.Name[(entry.Name.LastIndexOf('/') + 1)..])
            .Where(fileName => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(searchPattern, fileName, ignoreCase: OperatingSystem.IsWindows()))
            .Select(fileName => Path.Combine(trimmed, fileName))
            .ToList();
    }

    /// <summary>
    /// 需要真实文件路径的场合 (外部程序打开, 复制文件到剪贴板, 解码器): 普通文件原样返回;
    /// 归档内文件解压到临时目录 (同一个文件只解一次), 返回临时文件路径
    /// </summary>
    public static string GetLocalPath(string path)
    {
        if (File.Exists(path) || !TryGetEntry(path, out var archive, out var entry))
            return path;

        var key = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{archive.Path}|{archive.LastWriteTimeUtc.Ticks}|{entry.Name}")))[..16];
        var directory = Path.Combine(Path.GetTempPath(), "QQDatabaseReader-archive", key);
        var localPath = Path.Combine(directory, SafeFileName(entry.Name[(entry.Name.LastIndexOf('/') + 1)..]));
        if (File.Exists(localPath) && new FileInfo(localPath).Length == entry.Length)
            return localPath;

        Directory.CreateDirectory(directory);
        var temporary = localPath + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var source = archive.OpenRead(entry))
            using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                source.CopyTo(destination);
            File.Move(temporary, localPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        return localPath;
    }

    // 归档里的文件名可能含 Windows 不允许的字符 (例如手机上带冒号或换行的名字)
    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).TrimEnd('.', ' ');
        return result.Length == 0 ? "file" : result;
    }

    /// <summary>普通文件或归档内文件的长度</summary>
    public static long GetLength(string path)
    {
        if (File.Exists(path))
            return new FileInfo(path).Length;
        if (TryGetEntry(path, out _, out var entry))
            return entry.Length;
        throw new FileNotFoundException("文件不存在", path);
    }
}
