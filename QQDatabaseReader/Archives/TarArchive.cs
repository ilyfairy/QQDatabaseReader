using System.Formats.Tar;

namespace QQDatabaseReader.Archives;

/// <summary>
/// 不压缩的 tar 归档的只读索引, 文件头由 System.Formats.Tar 解析 (ustar, pax, GNU 长文件名, base-256 长度).
/// 文件数据在 tar 里是连续存放的, TarEntry.DataOffset 就是它在归档中的位置, 可以随机读.
/// 索引按需建立: 查找时从上次停下的位置继续读文件头, 找到就停, 不需要一次扫完整个归档
/// </summary>
public sealed class TarArchive : ArchiveIndex
{
    private const int BlockSize = 512;
    private const int BatchSize = 4096;

    private readonly Dictionary<string, ArchiveEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ArchiveEntry> _entriesIgnoreCase = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private long _scanOffset;
    private bool _scanComplete;

    internal TarArchive(string path, long length, DateTime lastWriteTimeUtc)
        : base(path, length, lastWriteTimeUtc)
    {
    }

    public override bool TryFind(string name, out ArchiveEntry entry)
    {
        name = NormalizeName(name);
        lock (_lock)
        {
            while (true)
            {
                if (_entries.TryGetValue(name, out entry))
                    return true;
                if (_scanComplete)
                    return _entriesIgnoreCase.TryGetValue(name, out entry);

                ScanNextBatch();
            }
        }
    }

    public override IReadOnlyCollection<ArchiveEntry> GetAllEntries()
    {
        lock (_lock)
        {
            while (!_scanComplete)
                ScanNextBatch();
            return _entries.Values.ToList();
        }
    }

    // 每批最多读 BatchSize 个文件头, 找到要的文件就能尽早返回; 不长期占用文件句柄
    private void ScanNextBatch()
    {
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
        stream.Position = _scanOffset;
        using var reader = new TarReader(stream, leaveOpen: true);
        for (var count = 0; count < BatchSize; count++)
        {
            var entry = reader.GetNextEntry(copyData: false);
            if (entry is null)
            {
                _scanComplete = true;
                return;
            }

            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile)
            {
                // 同名文件出现多次 (tar -r 追加过) 时一律取第一次出现的, 按需扫描和全量扫描结果一致,
                // 也保证主库和 -wal 来自同一份
                var archiveEntry = new ArchiveEntry(NormalizeName(entry.Name), entry.DataOffset, entry.Length, entry.Length, ArchiveEntryStorage.Stored);
                _entries.TryAdd(archiveEntry.Name, archiveEntry);
                _entriesIgnoreCase.TryAdd(archiveEntry.Name, archiveEntry);
            }

            // 下一个文件头紧跟在数据区 (按 512 字节对齐) 之后; 没有数据的条目就是当前位置
            _scanOffset = entry.DataOffset >= 0 && entry.Length > 0
                ? entry.DataOffset + (entry.Length + BlockSize - 1) / BlockSize * BlockSize
                : stream.Position;
        }
    }
}
