using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace QQDatabaseReader;

/// <summary>
/// 读取 PCQQ 的 Info.db。Info.db 是 OLE/CFB 复合文件，内部 ES stream 需要 QQ TEA 解密，
/// 解密后通常还要 zlib 解压，最后得到 TD/TA 结构的业务数据。
/// </summary>
public sealed class PCQQInfoDbReader
{
    private readonly byte[] _key;
    private IReadOnlyDictionary<string, byte[]>? _plainStreams;
    private IReadOnlyList<PCQQInfoGroup>? _groups;
    private IReadOnlyDictionary<uint, PCQQInfoGroup>? _groupsByAnyId;
    private IReadOnlyDictionary<uint, PCQQInfoContact>? _contactsByUin;

    public PCQQInfoDbReader(string infoDbPath, string key)
    {
        if (string.IsNullOrWhiteSpace(infoDbPath))
            throw new ArgumentException("Info.db path is required.", nameof(infoDbPath));

        InfoDbPath = infoDbPath;
        _key = PcqqDatabaseDecryptor.ParseKey(key);
        if (_key.Length != 16)
            throw new ArgumentException("PCQQ Info.db key must be exactly 16 bytes.", nameof(key));
    }

    public string InfoDbPath { get; }

    public byte[] Key => _key.ToArray();

    public IReadOnlyDictionary<string, byte[]> ReadPlainStreams()
    {
        if (_plainStreams is not null)
            return _plainStreams;

        var streams = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var stream in CfbReader.ReadStreams(File.ReadAllBytes(InfoDbPath)))
        {
            streams[stream.Name] = IsEsStream(stream.Data)
                ? DecryptEsStream(stream.Data, _key)
                : stream.Data;
        }

        _plainStreams = streams;
        return streams;
    }

    public IReadOnlyList<PCQQInfoGroup> GetGroups()
    {
        EnsureGroups();
        return _groups!;
    }

    public bool TryGetGroup(uint groupIdOrCode, out PCQQInfoGroup group)
    {
        EnsureGroups();
        return _groupsByAnyId!.TryGetValue(groupIdOrCode, out group!);
    }

    public IReadOnlyDictionary<uint, PCQQInfoContact> GetContacts()
    {
        EnsureContacts();
        return _contactsByUin!;
    }

    public bool TryGetContact(uint uin, out PCQQInfoContact contact)
    {
        EnsureContacts();
        return _contactsByUin!.TryGetValue(uin, out contact!);
    }

    public static IReadOnlyList<PCQQTxDataObject> ParseTxDataObjects(byte[] data) =>
        TxDataParser.ParseObjects(data).ToArray();

    private void EnsureGroups()
    {
        if (_groups is not null && _groupsByAnyId is not null)
            return;

        var groups = new List<PCQQInfoGroup>();
        if (ReadPlainStreams().TryGetValue("Basic.db", out var basicDb))
        {
            foreach (var obj in TxDataParser.ParseObjects(basicDb))
            {
                if (!obj.TryGetUInt32("dwGroupId", out var groupId) ||
                    !obj.TryGetUInt32("dwGroupCode", out var groupCode) ||
                    !obj.TryGetString("strGroupName", out var groupName))
                {
                    continue;
                }

                if (groupId == 0 && groupCode == 0)
                    continue;

                groups.Add(new PCQQInfoGroup(groupId, groupCode, groupName));
            }
        }

        _groups = groups
            .DistinctBy(group => (group.GroupId, group.GroupCode))
            .OrderBy(group => group.GroupCode == 0 ? group.GroupId : group.GroupCode)
            .ToArray();

        var map = new Dictionary<uint, PCQQInfoGroup>();
        foreach (var group in _groups)
        {
            if (group.GroupId != 0)
                map[group.GroupId] = group;
            if (group.GroupCode != 0)
                map[group.GroupCode] = group;
        }

        _groupsByAnyId = map;
    }

    private void EnsureContacts()
    {
        if (_contactsByUin is not null)
            return;

        var contacts = new Dictionary<uint, PCQQInfoContact>();
        ReadContactStream("QQInfo.db", contacts);
        ReadContactStream("NonRelationQQInfo.db", contacts);

        _contactsByUin = contacts;
    }

    private void ReadContactStream(string streamName, Dictionary<uint, PCQQInfoContact> contacts)
    {
        if (!ReadPlainStreams().TryGetValue(streamName, out var stream))
            return;

        foreach (var obj in TxDataParser.ParseObjects(stream))
        {
            if (!obj.TryGetUInt32("dwUin", out var uin) || uin == 0)
                continue;

            var nickname = GetOptionalString(obj, "strNickname");
            var remarkName = GetOptionalString(
                obj,
                "strRemark",
                "strRemarkName",
                "cstrRemark",
                "cstrRealname",
                "cstrMemo");
            var contact = new PCQQInfoContact(uin, nickname, remarkName);

            contacts[uin] = contacts.TryGetValue(uin, out var existing)
                ? MergeContact(existing, contact)
                : contact;
        }
    }

    private static PCQQInfoContact MergeContact(PCQQInfoContact existing, PCQQInfoContact incoming)
    {
        return new PCQQInfoContact(
            existing.Uin,
            FirstNonEmpty(existing.Nickname, incoming.Nickname),
            FirstNonEmpty(existing.RemarkName, incoming.RemarkName));
    }

    private static string? GetOptionalString(PCQQTxDataObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetString(name, out var value))
                return NullIfWhiteSpace(value);
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsEsStream(byte[] data) =>
        data.Length >= 16 && data[0] == (byte)'E' && data[1] == (byte)'S';

    private static byte[] DecryptEsStream(byte[] data, byte[] key)
    {
        using var all = new MemoryStream();
        var pos = 8;
        while (pos + 8 <= data.Length)
        {
            var tag = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
            var cipherLen = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 4, 4)));
            pos += 8;
            if (cipherLen < 0 || pos + cipherLen > data.Length)
                throw new InvalidDataException($"Invalid Info.db ES record length: tag=0x{tag:X8}, length={cipherLen}.");

            var cipher = data.AsSpan(pos, cipherLen).ToArray();
            pos += cipherLen;
            if (!QqTea.TryDecrypt(cipher, key, out var packed))
                throw new InvalidDataException($"Failed to decrypt Info.db ES record: tag=0x{tag:X8}.");

            var plain = TryInflate(packed, out var inflated) ? inflated : packed;
            all.Write(plain);
        }

        return all.ToArray();
    }

    private static bool TryInflate(byte[] input, out byte[] output)
    {
        output = [];
        try
        {
            using var inputStream = new MemoryStream(input);
            using var zlib = new ZLibStream(inputStream, CompressionMode.Decompress);
            using var outputStream = new MemoryStream();
            zlib.CopyTo(outputStream);
            output = outputStream.ToArray();
            return true;
        }
        catch
        {
            try
            {
                using var inputStream = new MemoryStream(input.Length > 2 ? input.AsSpan(2).ToArray() : input);
                using var deflate = new DeflateStream(inputStream, CompressionMode.Decompress);
                using var outputStream = new MemoryStream();
                deflate.CopyTo(outputStream);
                output = outputStream.ToArray();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private sealed record CfbStream(int Index, string Name, byte[] Data);

    private static class CfbReader
    {
        public static IEnumerable<CfbStream> ReadStreams(byte[] file)
        {
            ReadOnlySpan<byte> cfbMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
            if (file.Length < 512 || !file.AsSpan(0, 8).SequenceEqual(cfbMagic))
                throw new InvalidDataException("Info.db is not an OLE/CFB file.");

            var sectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x1E, 2));
            var miniSectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x20, 2));
            var firstDirSector = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x30, 4));
            var miniStreamCutoff = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x38, 4));
            var firstMiniFatSector = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x3C, 4));
            var numMiniFatSectors = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x40, 4));
            var firstDifatSector = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x44, 4));
            var numDifatSectors = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x48, 4));

            byte[] ReadSector(int sector)
            {
                var offset = (sector + 1) * sectorSize;
                if (sector < 0 || offset < 0 || offset >= file.Length)
                    throw new ArgumentOutOfRangeException(nameof(sector), $"sector={sector}");

                var output = new byte[sectorSize];
                var available = Math.Min(sectorSize, file.Length - offset);
                Buffer.BlockCopy(file, offset, output, 0, available);
                return output;
            }

            var fatSectors = new List<int>();
            for (var i = 0; i < 109; i++)
            {
                var sector = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x4C + i * 4, 4));
                if (sector >= 0)
                    fatSectors.Add(sector);
            }

            var difat = firstDifatSector;
            for (var i = 0; i < numDifatSectors && difat >= 0; i++)
            {
                var sector = ReadSector(difat);
                var difatEntryCount = sectorSize / 4 - 1;
                for (var entry = 0; entry < difatEntryCount; entry++)
                {
                    var fatSector = BinaryPrimitives.ReadInt32LittleEndian(sector.AsSpan(entry * 4, 4));
                    if (fatSector >= 0)
                        fatSectors.Add(fatSector);
                }

                difat = BinaryPrimitives.ReadInt32LittleEndian(sector.AsSpan(difatEntryCount * 4, 4));
            }

            var fat = fatSectors
                .SelectMany(sector => ReadSector(sector).Chunk(4).Select(chunk => BinaryPrimitives.ReadInt32LittleEndian(chunk)))
                .ToArray();

            IEnumerable<int> Chain(int start, int[] table)
            {
                const int end = unchecked((int)0xFFFFFFFE);
                var seen = new HashSet<int>();
                for (var current = start; current >= 0 && current < table.Length && current != end && seen.Add(current); current = table[current])
                    yield return current;
            }

            byte[] ReadChain(int start, int[] table) => Chain(start, table).SelectMany(ReadSector).ToArray();

            var miniFat = Chain(firstMiniFatSector, fat)
                .Take(numMiniFatSectors)
                .SelectMany(sector => ReadSector(sector).Chunk(4).Select(chunk => BinaryPrimitives.ReadInt32LittleEndian(chunk)))
                .ToArray();

            var directoryBytes = ReadChain(firstDirSector, fat);
            var entries = new List<DirEntry>();
            for (var offset = 0; offset + 128 <= directoryBytes.Length; offset += 128)
            {
                var span = directoryBytes.AsSpan(offset, 128);
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(64, 2));
                var type = span[66];
                if (type == 0 || nameLength < 2 || nameLength > 64)
                    continue;

                var name = Encoding.Unicode.GetString(span.Slice(0, nameLength - 2));
                var start = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(116, 4));
                var size = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(120, 8));
                entries.Add(new DirEntry(entries.Count, name, type, start, size));
            }

            var rootMini = Array.Empty<byte>();
            var root = entries.FirstOrDefault(entry => entry.Type == 5);
            if (root is not null && root.Start >= 0 && root.Size > 0)
            {
                var rootChain = ReadChain(root.Start, fat);
                rootMini = rootChain.AsSpan(0, checked((int)Math.Min(root.Size, rootChain.Length))).ToArray();
            }

            foreach (var entry in entries.Where(entry => entry.Type == 2))
            {
                yield return new CfbStream(entry.Index, entry.Name, ReadStream(entry));
            }

            byte[] ReadStream(DirEntry entry)
            {
                if (entry.Size <= 0)
                    return [];

                var output = new byte[checked((int)entry.Size)];
                if (entry.Size < miniStreamCutoff && rootMini.Length > 0)
                {
                    var copied = 0;
                    foreach (var miniSector in Chain(entry.Start, miniFat))
                    {
                        var source = miniSector * miniSectorSize;
                        var take = Math.Min(miniSectorSize, output.Length - copied);
                        if (source < 0 || source + take > rootMini.Length)
                            break;

                        Buffer.BlockCopy(rootMini, source, output, copied, take);
                        copied += take;
                        if (copied >= output.Length)
                            break;
                    }

                    return output;
                }

                var copiedLarge = 0;
                foreach (var sectorNumber in Chain(entry.Start, fat))
                {
                    var sector = ReadSector(sectorNumber);
                    var take = Math.Min(sectorSize, output.Length - copiedLarge);
                    Buffer.BlockCopy(sector, 0, output, copiedLarge, take);
                    copiedLarge += take;
                    if (copiedLarge >= output.Length)
                        break;
                }

                return output;
            }
        }

        private sealed record DirEntry(int Index, string Name, byte Type, int Start, long Size);
    }

    private static class QqTea
    {
        public static bool TryDecrypt(byte[] cipher, byte[] key, out byte[] plain)
        {
            plain = [];
            if (key.Length != 16 || cipher.Length < 16 || (cipher.Length & 7) != 0)
                return false;

            var prePlain = DecipherBlock(cipher.AsSpan(0, 8), key);
            var pos = prePlain[0] & 7;
            var plainLength = cipher.Length - pos - 10;
            if (plainLength < 0)
                return false;

            plain = new byte[plainLength];
            var preCrypt = 0;
            var crypt = 8;
            var outPos = 0;
            var firstPlainBlock = true;
            pos++;

            var padding = 1;
            while (padding <= 2)
            {
                if (pos < 8)
                {
                    pos++;
                    padding++;
                }

                if (pos == 8)
                {
                    if (crypt >= cipher.Length)
                        return false;
                    if (!NextPlain(cipher, key, ref prePlain, ref preCrypt, ref crypt))
                        return false;
                    firstPlainBlock = false;
                    pos = 0;
                }
            }

            while (plainLength > 0)
            {
                if (pos < 8)
                {
                    var previous = firstPlainBlock ? (byte)0 : cipher[preCrypt + pos];
                    plain[outPos++] = (byte)(prePlain[pos] ^ previous);
                    pos++;
                    plainLength--;
                }

                if (pos == 8)
                {
                    if (crypt >= cipher.Length)
                        return false;
                    if (!NextPlain(cipher, key, ref prePlain, ref preCrypt, ref crypt))
                        return false;
                    firstPlainBlock = false;
                    pos = 0;
                }
            }

            padding = 1;
            while (padding <= 7)
            {
                if (pos < 8)
                {
                    var previous = firstPlainBlock ? (byte)0 : cipher[preCrypt + pos];
                    if ((prePlain[pos] ^ previous) != 0)
                        return false;
                    pos++;
                    padding++;
                }

                if (pos == 8 && padding <= 7)
                {
                    if (crypt >= cipher.Length)
                        return false;
                    if (!NextPlain(cipher, key, ref prePlain, ref preCrypt, ref crypt))
                        return false;
                    firstPlainBlock = false;
                    pos = 0;
                }
            }

            return true;
        }

        private static bool NextPlain(byte[] cipher, byte[] key, ref byte[] prePlain, ref int preCrypt, ref int crypt)
        {
            if (crypt + 8 > cipher.Length)
                return false;

            Span<byte> x = stackalloc byte[8];
            for (var i = 0; i < 8; i++)
                x[i] = (byte)(cipher[crypt + i] ^ prePlain[i]);

            prePlain = DecipherBlock(x, key);
            preCrypt = crypt - 8;
            crypt += 8;
            return true;
        }

        private static byte[] DecipherBlock(ReadOnlySpan<byte> input, byte[] key)
        {
            var y = ReadUInt32BigEndian(input, 0);
            var z = ReadUInt32BigEndian(input, 4);
            var a = ReadUInt32BigEndian(key, 0);
            var b = ReadUInt32BigEndian(key, 4);
            var c = ReadUInt32BigEndian(key, 8);
            var d = ReadUInt32BigEndian(key, 12);
            var sum = 0xE3779B90U;
            const uint delta = 0x9E3779B9U;
            for (var i = 0; i < 16; i++)
            {
                z -= ((y << 4) + c) ^ (y + sum) ^ ((y >> 5) + d);
                y -= ((z << 4) + a) ^ (z + sum) ^ ((z >> 5) + b);
                sum -= delta;
            }

            return [(byte)(y >> 24), (byte)(y >> 16), (byte)(y >> 8), (byte)y, (byte)(z >> 24), (byte)(z >> 16), (byte)(z >> 8), (byte)z];
        }

        private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> bytes, int offset) =>
            BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
    }

    private static class TxDataParser
    {
        public static IEnumerable<PCQQTxDataObject> ParseObjects(byte[] data)
        {
            for (var offset = 0; offset <= data.Length - 4; offset++)
            {
                if (data[offset] != (byte)'T' ||
                    data[offset + 1] != (byte)'D' ||
                    data[offset + 2] != 1 ||
                    data[offset + 3] != 1)
                {
                    continue;
                }

                if (TryParseTd(data, offset, out var obj))
                    yield return obj;
            }
        }

        private static bool TryParseTd(byte[] data, int offset, out PCQQTxDataObject obj)
        {
            obj = new PCQQTxDataObject(offset, new Dictionary<string, PCQQTxDataField>());
            if (offset + 6 > data.Length)
                return false;

            var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4, 2));
            if (count > 4096)
                return false;

            var pos = offset + 6;
            var fields = new Dictionary<string, PCQQTxDataField>(StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                if (pos + 7 > data.Length)
                    return false;

                var fieldOffset = pos;
                var type = data[pos++];
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
                pos += 2;
                if (nameLength == 0 || nameLength > 4096 || pos + nameLength + 4 > data.Length)
                    return false;

                var nameRaw = data.AsSpan(pos, nameLength).ToArray();
                pos += nameLength;
                var valueLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
                pos += 4;
                if (valueLength > data.Length - pos)
                    return false;

                var valueRaw = data.AsSpan(pos, checked((int)valueLength)).ToArray();
                pos += checked((int)valueLength);

                var name = DecodeAsciiObfuscated(nameRaw);
                if (string.IsNullOrWhiteSpace(name))
                    return false;

                fields[name] = new PCQQTxDataField(fieldOffset, type, DecodeValue(type, valueRaw), valueRaw);
            }

            obj = new PCQQTxDataObject(offset, fields);
            return true;
        }

        private static object? DecodeValue(byte type, byte[] raw)
        {
            return type switch
            {
                0x01 when raw.Length == 4 => BinaryPrimitives.ReadInt32LittleEndian(raw),
                0x02 when raw.Length == 1 => raw[0],
                0x03 when raw.Length == 2 => BinaryPrimitives.ReadUInt16LittleEndian(raw),
                0x04 when raw.Length == 2 => BinaryPrimitives.ReadInt16LittleEndian(raw),
                0x05 when raw.Length == 4 => BinaryPrimitives.ReadInt32LittleEndian(raw),
                0x06 when raw.Length == 4 => BinaryPrimitives.ReadUInt32LittleEndian(raw),
                0x07 when raw.Length == 4 => BinaryPrimitives.ReadInt32LittleEndian(raw),
                0x08 => DecodeStringValue(raw),
                0x0C => ParseArrayValue(raw),
                _ => raw,
            };
        }

        private static IReadOnlyList<object?> ParseArrayValue(byte[] raw)
        {
            if (raw.Length < 8 ||
                raw[0] != (byte)'T' ||
                raw[1] != (byte)'A' ||
                raw[2] != 1 ||
                raw[3] != 1)
            {
                return [raw];
            }

            var count = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4, 4));
            var items = new List<object?>();
            var pos = 8;
            while (pos + 5 <= raw.Length && items.Count < count)
            {
                var itemType = raw[pos++];
                var length = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(pos, 4));
                pos += 4;
                if (length > raw.Length - pos)
                    break;

                var payload = raw.AsSpan(pos, checked((int)length)).ToArray();
                pos += checked((int)length);
                if (itemType == 0x0B &&
                    payload.Length >= 4 &&
                    payload[0] == (byte)'T' &&
                    payload[1] == (byte)'D' &&
                    TryParseTd(payload, 0, out var td))
                {
                    items.Add(td);
                }
                else
                {
                    items.Add(payload);
                }
            }

            return items;
        }

        private static string DecodeStringValue(byte[] raw)
        {
            var decoded = DecodeObfuscated(raw);
            if (decoded.Length >= 2 && decoded.Length % 2 == 0)
            {
                var value = Encoding.Unicode.GetString(decoded).TrimEnd('\0');
                if (LooksText(value))
                    return FixMojibake(value);
            }

            try
            {
                var gbk = Encoding.GetEncoding(936).GetString(decoded).TrimEnd('\0');
                if (LooksText(gbk))
                    return FixMojibake(gbk);
            }
            catch
            {
                // 没有注册 GBK code page 时保留十六进制，避免解析失败。
            }

            return Convert.ToHexString(raw);
        }

        private static string DecodeAsciiObfuscated(byte[] raw) =>
            Encoding.ASCII.GetString(DecodeObfuscated(raw)).TrimEnd('\0');

        private static byte[] DecodeObfuscated(byte[] raw)
        {
            var key = (byte)((raw.Length & 0xFF) ^ ((raw.Length >> 8) & 0xFF));
            var output = new byte[raw.Length];
            for (var i = 0; i < raw.Length; i++)
                output[i] = (byte)(((~raw[i]) & 0xFF) ^ key);
            return output;
        }

        private static bool LooksText(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            var good = value.Count(ch => ch >= 0x20 && !char.IsControl(ch));
            return good >= Math.Max(1, value.Length / 2);
        }

        private static string FixMojibake(string value)
        {
            try
            {
                var gbkBytes = Encoding.GetEncoding(936).GetBytes(value);
                var utf8 = Encoding.UTF8.GetString(gbkBytes);
                return !utf8.Contains('�') && MojibakeScore(utf8) < MojibakeScore(value)
                    ? utf8
                    : value;
            }
            catch
            {
                return value;
            }
        }

        private static int MojibakeScore(string value)
        {
            ReadOnlySpan<char> markers = ['锟', '�', '€', '绋', '浠', '叆', '闂', '鍏', '鍚', '鐨', '鐢', '缇', '兢', '涓', '娲', '瑙', '戠', '瀹', '呭', '枃'];
            var score = 0;
            foreach (var ch in value)
            {
                if (markers.Contains(ch))
                    score++;
            }

            return score;
        }
    }
}

public sealed record PCQQInfoGroup(uint GroupId, uint GroupCode, string GroupName);

public sealed record PCQQInfoContact(uint Uin, string? Nickname, string? RemarkName)
{
    public string? DisplayName => !string.IsNullOrWhiteSpace(RemarkName)
        ? RemarkName
        : !string.IsNullOrWhiteSpace(Nickname)
            ? Nickname
            : null;
}

public sealed record PCQQTxDataObject(int Offset, IReadOnlyDictionary<string, PCQQTxDataField> Fields)
{
    public bool TryGetString(string name, out string value)
    {
        value = string.Empty;
        if (!Fields.TryGetValue(name, out var field) || field.Value is not string stringValue)
            return false;

        value = stringValue;
        return true;
    }

    public bool TryGetUInt32(string name, out uint value)
    {
        value = 0;
        if (!Fields.TryGetValue(name, out var field))
            return false;

        switch (field.Value)
        {
            case uint uintValue:
                value = uintValue;
                return true;
            case int intValue:
                value = unchecked((uint)intValue);
                return true;
            case ushort ushortValue:
                value = ushortValue;
                return true;
            case short shortValue:
                value = unchecked((uint)shortValue);
                return true;
            case byte byteValue:
                value = byteValue;
                return true;
            default:
                return false;
        }
    }
}

public sealed record PCQQTxDataField(int Offset, byte Type, object? Value, byte[] Raw);
