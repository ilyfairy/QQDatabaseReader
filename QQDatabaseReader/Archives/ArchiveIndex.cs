using System.Collections.Concurrent;
using System.IO.Compression;

namespace QQDatabaseReader.Archives;

/// <summary>归档里一个文件的存储方式</summary>
public enum ArchiveEntryStorage
{
    /// <summary>原样存放, 可以随机读 (SQLite 数据库只能读这种)</summary>
    Stored,

    /// <summary>deflate 压缩, 只能从头顺序解压</summary>
    Deflated,

    /// <summary>其它压缩或加密方式, 读不了</summary>
    Unsupported,
}

/// <summary>归档里的一个文件. Stored 时 DataOffset 是原始数据在归档文件中的位置; 其它方式是压缩数据的位置</summary>
public readonly record struct ArchiveEntry(string Name, long DataOffset, long Length, long CompressedLength, ArchiveEntryStorage Storage);

/// <summary>归档文件的只读索引: tar, zip</summary>
public abstract class ArchiveIndex
{
    private static readonly ConcurrentDictionary<string, ArchiveIndex> Cache = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    protected ArchiveIndex(string path, long length, DateTime lastWriteTimeUtc)
    {
        Path = path;
        Length = length;
        LastWriteTimeUtc = lastWriteTimeUtc;
        // 建立失败 (一次性的 IO 错误) 不缓存异常, 下次再试
        _directories = new Lazy<DirectoryTree>(BuildDirectoryTree, LazyThreadSafetyMode.PublicationOnly);
    }

    public string Path { get; }

    public long Length { get; }

    public DateTime LastWriteTimeUtc { get; }

    /// <summary>按扩展名识别的归档类型</summary>
    public static bool IsArchiveFileName(string fileName) =>
        fileName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>同一个归档文件共用一份索引; 文件被改动 (长度或修改时间变化) 后重新建立</summary>
    public static ArchiveIndex Open(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException("归档不存在", fullPath);

        return Cache.AddOrUpdate(
            fullPath,
            _ => Create(fullPath, info),
            (_, existing) => existing.Length == info.Length && existing.LastWriteTimeUtc == info.LastWriteTimeUtc
                ? existing
                : Create(fullPath, info));
    }

    private static ArchiveIndex Create(string fullPath, FileInfo info) =>
        fullPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? new ZipArchiveIndex(fullPath, info.Length, info.LastWriteTimeUtc)
            : new TarArchive(fullPath, info.Length, info.LastWriteTimeUtc);

    /// <summary>按归档内路径查找文件 (分隔符用 /, 开头的 ./ 可有可无). 先按大小写精确匹配, 找不到再忽略大小写</summary>
    public abstract bool TryFind(string name, out ArchiveEntry entry);

    /// <summary>列出所有文件 (会读完整个索引)</summary>
    public abstract IReadOnlyCollection<ArchiveEntry> GetAllEntries();

    private readonly Lazy<DirectoryTree> _directories;

    /// <summary>目录是否存在 (归档里有以它为前缀的文件; tar 里的空目录不算). 首次调用会读完整个索引</summary>
    public bool DirectoryExists(string name)
    {
        name = NormalizeName(name).TrimEnd('/');
        return name.Length == 0 || _directories.Value.Files.ContainsKey(name) || _directories.Value.FilesIgnoreCase.ContainsKey(name);
    }

    /// <summary>目录下直接包含的文件 (不含子目录里的). 首次调用会读完整个索引</summary>
    public IReadOnlyList<ArchiveEntry> GetDirectoryFiles(string name)
    {
        name = NormalizeName(name).TrimEnd('/');
        var tree = _directories.Value;
        return tree.Files.TryGetValue(name, out var files) || tree.FilesIgnoreCase.TryGetValue(name, out files) ? files : [];
    }

    private sealed record DirectoryTree(Dictionary<string, List<ArchiveEntry>> Files, Dictionary<string, List<ArchiveEntry>> FilesIgnoreCase);

    private DirectoryTree BuildDirectoryTree()
    {
        var files = new Dictionary<string, List<ArchiveEntry>>(StringComparer.Ordinal);
        foreach (var entry in GetAllEntries())
        {
            var slash = entry.Name.LastIndexOf('/');
            var directory = slash < 0 ? string.Empty : entry.Name[..slash];
            if (!files.TryGetValue(directory, out var list))
                files[directory] = list = [];
            list.Add(entry);

            // 上级目录也要存在, 即使它下面只有子目录没有文件
            for (var parent = directory; parent.Length > 0;)
            {
                var up = parent.LastIndexOf('/');
                parent = up < 0 ? string.Empty : parent[..up];
                if (!files.TryAdd(parent, []))
                    break;
            }
        }

        var ignoreCase = new Dictionary<string, List<ArchiveEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (directory, list) in files)
            ignoreCase.TryAdd(directory, list);
        return new DirectoryTree(files, ignoreCase);
    }

    // 小于这个长度的压缩文件解压到内存, 让调用方可以随机读 (例如读图片尺寸时要跳转)
    private const long MaxBufferedDecompressedLength = 256L * 1024 * 1024;

    /// <summary>
    /// 读取一个文件的内容: 原样存放的直接读那一段 (可随机读); deflate 的解压到内存 (可随机读),
    /// 超过 256 MiB 的只能顺序读
    /// </summary>
    public Stream OpenRead(ArchiveEntry entry)
    {
        var handle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.RandomAccess);
        switch (entry.Storage)
        {
            case ArchiveEntryStorage.Stored:
                return new ArchiveSliceStream(handle, entry.DataOffset, entry.Length);
            case ArchiveEntryStorage.Deflated:
                var deflate = new DeflateStream(new ArchiveSliceStream(handle, entry.DataOffset, entry.CompressedLength), CompressionMode.Decompress);
                if (entry.Length > MaxBufferedDecompressedLength)
                    return deflate;
                using (deflate)
                {
                    var buffer = new MemoryStream((int)entry.Length);
                    deflate.CopyTo(buffer);
                    buffer.Position = 0;
                    return buffer;
                }
            default:
                throw ThrowUnsupported(handle, entry);
        }
    }

    private static NotSupportedException ThrowUnsupported(IDisposable handle, ArchiveEntry entry)
    {
        handle.Dispose();
        return new NotSupportedException($"归档内文件 {entry.Name} 的压缩或加密方式不支持");
    }

    public static string NormalizeName(string name)
    {
        name = name.Replace('\\', '/');
        while (name.StartsWith("./", StringComparison.Ordinal))
            name = name[2..];
        return name.TrimStart('/');
    }
}

/// <summary>归档文件中一段数据的只读流</summary>
public sealed class ArchiveSliceStream(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long start, long length) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => _position = value is >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var available = length - _position;
        if (available <= 0)
            return 0;
        if (buffer.Length > available)
            buffer = buffer[..(int)available];
        var read = RandomAccess.Read(handle, buffer, start + _position);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => length + offset,
    };

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            handle.Dispose();
        base.Dispose(disposing);
    }
}
