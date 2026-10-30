using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace QQDatabaseReader.Archives;

/// <summary>
/// zip 归档的只读索引: 一次读完中央目录 (支持 ZIP64), 文件数据的真实位置在首次查找时读本地文件头算出.
/// 仅存储 (method 0) 的文件可以随机读; deflate (method 8) 只能顺序解压; 加密和其它压缩方式不支持.
/// 文件名: 标志位 11 为 UTF-8, 否则先按 UTF-8 严格解码, 失败按 GBK (Windows 中文系统打的包); 有 Info-ZIP Unicode Path 扩展时以它为准
/// </summary>
public sealed class ZipArchiveIndex : ArchiveIndex
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Lazy<Encoding> Gbk = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936);
    });

    private readonly Lock _lock = new();
    private Dictionary<string, CentralEntry>? _entries;
    private Dictionary<string, CentralEntry>? _entriesIgnoreCase;
    private Exception? _loadError;

    internal ZipArchiveIndex(string path, long length, DateTime lastWriteTimeUtc)
        : base(path, length, lastWriteTimeUtc)
    {
    }

    public override bool TryFind(string name, out ArchiveEntry entry)
    {
        name = NormalizeName(name);
        lock (_lock)
        {
            EnsureLoaded();
            if (!_entries!.TryGetValue(name, out var central) && !_entriesIgnoreCase!.TryGetValue(name, out central))
            {
                entry = default;
                return false;
            }

            entry = Resolve(central);
            return true;
        }
    }

    public override IReadOnlyCollection<ArchiveEntry> GetAllEntries()
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _entries!.Values.Select(Resolve).ToList();
        }
    }

    // 归档损坏时记住错误, 不必每次都重读文件尾; 文件被改动后 ArchiveIndex.Open 会建新的索引
    private void EnsureLoaded()
    {
        if (_entries is not null)
            return;
        if (_loadError is not null)
            throw new InvalidDataException(_loadError.Message, _loadError);

        try
        {
            Load();
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            _loadError = ex;
            throw;
        }
    }

    private void Load()
    {
        using var handle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.RandomAccess);
        var (directoryOffset, directorySize, _) = ReadEndOfCentralDirectory(handle);
        if (directorySize > int.MaxValue)
            throw new InvalidDataException("zip 中央目录过大");

        var directory = ReadBytes(handle, directoryOffset, (int)directorySize);
        var entries = new Dictionary<string, CentralEntry>(StringComparer.Ordinal);
        var entriesIgnoreCase = new Dictionary<string, CentralEntry>(StringComparer.OrdinalIgnoreCase);
        var position = 0;
        // 按中央目录的实际长度读完, 不依赖条目数 (有的打包工具让 16 位计数回绕却不写 ZIP64)
        while (position + 46 <= directory.Length)
        {
            var record = directory.AsSpan(position);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x02014b50)
                throw new InvalidDataException("zip 中央目录损坏");

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[8..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
            long compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(record[20..]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(record[24..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[28..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(record[30..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(record[32..]);
            long localOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[42..]);
            var nameBytes = record.Slice(46, nameLength);
            var extra = record.Slice(46 + nameLength, extraLength);

            string? unicodeName = null;
            ParseExtra(extra, ref size, ref compressedSize, ref localOffset, ref unicodeName);
            var name = NormalizeName(unicodeName ?? DecodeName(nameBytes, (flags & 0x0800) != 0));
            position += 46 + nameLength + extraLength + commentLength;

            if (name.EndsWith('/'))
                continue;

            var storage = (flags & 0x0001) != 0 ? ArchiveEntryStorage.Unsupported
                : method switch
                {
                    0 => ArchiveEntryStorage.Stored,
                    8 => ArchiveEntryStorage.Deflated,
                    _ => ArchiveEntryStorage.Unsupported,
                };
            var central = new CentralEntry(name, localOffset, size, compressedSize, storage);
            // 同名文件出现多次时取第一次出现的, 和 tar 一致
            entries.TryAdd(name, central);
            entriesIgnoreCase.TryAdd(name, central);
        }

        _entriesIgnoreCase = entriesIgnoreCase;
        _entries = entries;
    }

    // 本地文件头的文件名和扩展字段长度可能和中央目录不同, 数据位置要以本地文件头为准
    private ArchiveEntry Resolve(CentralEntry central)
    {
        if (central.DataOffset is { } dataOffset)
            return new ArchiveEntry(central.Name, dataOffset, central.Length, central.CompressedLength, central.Storage);

        using var handle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.RandomAccess);
        var header = ReadBytes(handle, central.LocalHeaderOffset, 30);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x04034b50)
            throw new InvalidDataException($"zip 本地文件头损坏: {central.Name}");

        var offset = central.LocalHeaderOffset + 30 +
                     BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26)) +
                     BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
        central.DataOffset = offset;
        return new ArchiveEntry(central.Name, offset, central.Length, central.CompressedLength, central.Storage);
    }

    private (long Offset, long Size, long Count) ReadEndOfCentralDirectory(SafeFileHandle handle)
    {
        // EOCD 在文件末尾, 后面最多跟 65535 字节注释
        var tailLength = (int)Math.Min(Length, 22 + 65535);
        var tail = ReadBytes(handle, Length - tailLength, tailLength);
        var index = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            throw new InvalidDataException("不是 zip 归档 (找不到中央目录结束标记)");

        var eocd = tail.AsSpan(index);
        long count = BinaryPrimitives.ReadUInt16LittleEndian(eocd[10..]);
        long size = BinaryPrimitives.ReadUInt32LittleEndian(eocd[12..]);
        long offset = BinaryPrimitives.ReadUInt32LittleEndian(eocd[16..]);
        if (count != 0xFFFF && size != 0xFFFFFFFF && offset != 0xFFFFFFFF)
            return (offset, size, count);

        // ZIP64: EOCD 前面是 ZIP64 EOCD 定位器
        var locatorPosition = Length - tailLength + index - 20;
        var locator = locatorPosition >= 0 ? ReadBytes(handle, locatorPosition, 20) : [];
        if (locator.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(locator) != 0x07064b50)
        {
            // 计数正好是 0xFFFF (或长度正好是 0xFFFFFFFF) 但没有 ZIP64 记录: 按普通值处理
            return (offset, size, count);
        }

        var zip64Offset = BinaryPrimitives.ReadInt64LittleEndian(locator.AsSpan(8));
        var zip64 = ReadBytes(handle, zip64Offset, 56);
        if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != 0x06064b50)
            throw new InvalidDataException("zip64 中央目录结束记录损坏");

        return (BinaryPrimitives.ReadInt64LittleEndian(zip64.AsSpan(48)),
            BinaryPrimitives.ReadInt64LittleEndian(zip64.AsSpan(40)),
            BinaryPrimitives.ReadInt64LittleEndian(zip64.AsSpan(32)));
    }

    // 0x0001: ZIP64 (只包含中央目录里为 0xFFFFFFFF 的字段, 顺序固定); 0x7075: Info-ZIP Unicode Path
    private static void ParseExtra(ReadOnlySpan<byte> extra, ref long size, ref long compressedSize, ref long localOffset, ref string? unicodeName)
    {
        while (extra.Length >= 4)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
            if (4 + length > extra.Length)
                break;
            var data = extra.Slice(4, length);
            if (id == 0x0001)
            {
                var position = 0;
                if (size == 0xFFFFFFFF && position + 8 <= data.Length)
                {
                    size = BinaryPrimitives.ReadInt64LittleEndian(data[position..]);
                    position += 8;
                }

                if (compressedSize == 0xFFFFFFFF && position + 8 <= data.Length)
                {
                    compressedSize = BinaryPrimitives.ReadInt64LittleEndian(data[position..]);
                    position += 8;
                }

                if (localOffset == 0xFFFFFFFF && position + 8 <= data.Length)
                    localOffset = BinaryPrimitives.ReadInt64LittleEndian(data[position..]);
            }
            else if (id == 0x7075 && data.Length > 5 && data[0] == 1)
            {
                unicodeName = Encoding.UTF8.GetString(data[5..]);
            }

            extra = extra[(4 + length)..];
        }
    }

    private static string DecodeName(ReadOnlySpan<byte> bytes, bool utf8Flag)
    {
        if (utf8Flag)
            return Encoding.UTF8.GetString(bytes);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Gbk.Value.GetString(bytes);
        }
    }

    private static byte[] ReadBytes(SafeFileHandle handle, long offset, int length)
    {
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = RandomAccess.Read(handle, buffer.AsSpan(read), offset + read);
            if (n <= 0)
                throw new EndOfStreamException("zip 文件不完整");
            read += n;
        }

        return buffer;
    }

    private sealed class CentralEntry(string name, long localHeaderOffset, long length, long compressedLength, ArchiveEntryStorage storage)
    {
        public string Name { get; } = name;
        public long LocalHeaderOffset { get; } = localHeaderOffset;
        public long Length { get; } = length;
        public long CompressedLength { get; } = compressedLength;
        public ArchiveEntryStorage Storage { get; } = storage;
        public long? DataOffset { get; set; }
    }
}
