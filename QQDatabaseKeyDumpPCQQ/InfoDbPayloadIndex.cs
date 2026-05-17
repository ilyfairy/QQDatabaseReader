using System.Buffers.Binary;
using System.Text;

namespace QQDatabaseKeyDumpPCQQ;

public sealed class InfoDbPayloadIndex
{
    private readonly Dictionary<string, List<InfoDbPayloadMatch>> _byLengthAndHead = new(StringComparer.OrdinalIgnoreCase);

    public int InfoDbCount { get; private set; }

    public int StreamCount { get; private set; }

    public int EsRecordCount { get; private set; }

    public static InfoDbPayloadIndex BuildDefault(Action<string>? errorLog = null)
    {
        var index = new InfoDbPayloadIndex();
        foreach (string path in EnumerateDefaultInfoDbPaths())
        {
            try
            {
                index.AddInfoDb(path);
            }
            catch (Exception ex)
            {
                (errorLog ?? Console.Error.WriteLine)($"[Info.db] skip {path}: {ex.Message}");
            }
        }

        return index;
    }

    public IReadOnlyList<InfoDbPayloadMatch> Match(int payloadLength, byte[] head)
    {
        string key = MakeKey(payloadLength, head);
        return _byLengthAndHead.TryGetValue(key, out var list)
            ? list
            : Array.Empty<InfoDbPayloadMatch>();
    }

    private void AddInfoDb(string path)
    {
        InfoDbCount++;
        byte[] file = File.ReadAllBytes(path);
        foreach (var stream in CfbReader.ReadStreams(file))
        {
            StreamCount++;
            byte[] data = stream.Data;
            if (data.Length < 16 || data[0] != (byte)'E' || data[1] != (byte)'S')
                continue;

            int pos = 8;
            while (pos + 8 <= data.Length)
            {
                uint tag = ReadBigEndianUInt32(data, pos);
                int len = checked((int)ReadBigEndianUInt32(data, pos + 4));
                pos += 8;
                if (len < 0 || pos + len > data.Length)
                    break;

                byte[] head = data.AsSpan(pos, Math.Min(64, len)).ToArray();
                var match = new InfoDbPayloadMatch(path, stream.Name, tag, len, Convert.ToHexString(head));
                string key = MakeKey(len, head);
                if (!_byLengthAndHead.TryGetValue(key, out var list))
                {
                    list = new List<InfoDbPayloadMatch>();
                    _byLengthAndHead[key] = list;
                }

                list.Add(match);
                EsRecordCount++;
                pos += len;
            }
        }
    }

    private static IEnumerable<string> EnumerateDefaultInfoDbPaths()
    {
        var roots = new List<string>();
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(documents))
            roots.Add(Path.Combine(documents, "Tencent Files"));

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
            roots.Add(Path.Combine(userProfile, "Documents", "Tencent Files"));

        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
                continue;

            foreach (string file in Directory.EnumerateFiles(root, "Info.db", SearchOption.AllDirectories))
                yield return file;
        }
    }

    private static string MakeKey(int length, byte[] head) => length + ":" + Convert.ToHexString(head);

    private static uint ReadBigEndianUInt32(byte[] bytes, int offset) =>
        ((uint)bytes[offset] << 24) |
        ((uint)bytes[offset + 1] << 16) |
        ((uint)bytes[offset + 2] << 8) |
        bytes[offset + 3];

    private sealed record CfbStream(string Name, byte[] Data);

    private static class CfbReader
    {
        public static IEnumerable<CfbStream> ReadStreams(byte[] file)
        {
            byte[] cfbMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
            if (file.Length < 512 ||
                !file.AsSpan(0, 8).SequenceEqual(cfbMagic))
            {
                yield break;
            }

            ushort sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x1E, 2));
            ushort miniSectorShift = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x20, 2));
            int sectorSize = 1 << sectorShift;
            int miniSectorSize = 1 << miniSectorShift;
            int firstDirSector = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x30, 4));
            int miniStreamCutoff = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x38, 4));
            int firstMiniFatSector = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x3C, 4));
            int miniFatSectorCount = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x40, 4));
            int firstDifatSector = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x44, 4));
            int difatSectorCount = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x48, 4));

            byte[] ReadSector(int sector)
            {
                int offset = (sector + 1) * sectorSize;
                if (sector < 0 || offset < 0 || offset >= file.Length)
                    throw new ArgumentOutOfRangeException(nameof(sector), $"sector={sector}");

                byte[] output = new byte[sectorSize];
                int available = Math.Min(sectorSize, file.Length - offset);
                Buffer.BlockCopy(file, offset, output, 0, available);
                return output;
            }

            var fatSectors = new List<int>();
            for (int i = 0; i < 109; i++)
            {
                int sector = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x4C + i * 4, 4));
                if (sector >= 0)
                    fatSectors.Add(sector);
            }

            int difat = firstDifatSector;
            for (int i = 0; i < difatSectorCount && difat >= 0; i++)
            {
                byte[] sector = ReadSector(difat);
                int difatEntries = sectorSize / 4 - 1;
                for (int j = 0; j < difatEntries; j++)
                {
                    int fatSector = BinaryPrimitives.ReadInt32LittleEndian(sector.AsSpan(j * 4, 4));
                    if (fatSector >= 0)
                        fatSectors.Add(fatSector);
                }

                difat = BinaryPrimitives.ReadInt32LittleEndian(sector.AsSpan(difatEntries * 4, 4));
            }

            int[] fat = fatSectors
                .SelectMany(sector => ReadSector(sector).Chunk(4).Select(chunk => BinaryPrimitives.ReadInt32LittleEndian(chunk)))
                .ToArray();

            IEnumerable<int> Chain(int start, int[] table)
            {
                const int EndOfChain = unchecked((int)0xFFFFFFFE);
                var seen = new HashSet<int>();
                for (int current = start;
                     current >= 0 && current < table.Length && current != EndOfChain && seen.Add(current);
                     current = table[current])
                {
                    yield return current;
                }
            }

            byte[] ReadChain(int start, int[] table) => Chain(start, table).SelectMany(ReadSector).ToArray();

            int[] miniFat = Chain(firstMiniFatSector, fat)
                .Take(miniFatSectorCount)
                .SelectMany(sector => ReadSector(sector).Chunk(4).Select(chunk => BinaryPrimitives.ReadInt32LittleEndian(chunk)))
                .ToArray();

            byte[] dirBytes = ReadChain(firstDirSector, fat);
            var entries = new List<DirEntry>();
            for (int offset = 0, index = 0; offset + 128 <= dirBytes.Length; offset += 128, index++)
            {
                var span = dirBytes.AsSpan(offset, 128);
                ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(64, 2));
                byte type = span[66];
                if (type == 0 || nameLength < 2 || nameLength > 64)
                    continue;

                string name = Encoding.Unicode.GetString(span.Slice(0, nameLength - 2));
                int start = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(116, 4));
                long size = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(120, 8));
                entries.Add(new DirEntry(index, name, type, start, size));
            }

            byte[] rootMiniStream = Array.Empty<byte>();
            var root = entries.FirstOrDefault(entry => entry.Type == 5);
            if (root is not null && root.Start >= 0 && root.Size > 0)
            {
                rootMiniStream = ReadChain(root.Start, fat)
                    .AsSpan(0, checked((int)Math.Min(root.Size, int.MaxValue)))
                    .ToArray();
            }

            foreach (var entry in entries.Where(entry => entry.Type == 2))
            {
                byte[] data = ReadStream(entry);
                yield return new CfbStream(entry.Name, data);
            }

            byte[] ReadStream(DirEntry entry)
            {
                if (entry.Size <= 0)
                    return Array.Empty<byte>();

                byte[] output = new byte[checked((int)entry.Size)];
                if (entry.Size < miniStreamCutoff && rootMiniStream.Length > 0)
                {
                    int copied = 0;
                    foreach (int miniSector in Chain(entry.Start, miniFat))
                    {
                        int source = miniSector * miniSectorSize;
                        int take = Math.Min(miniSectorSize, output.Length - copied);
                        if (source < 0 || source + take > rootMiniStream.Length)
                            break;

                        Buffer.BlockCopy(rootMiniStream, source, output, copied, take);
                        copied += take;
                        if (copied >= output.Length)
                            break;
                    }

                    return output;
                }

                int copiedLarge = 0;
                foreach (int sector in Chain(entry.Start, fat))
                {
                    byte[] sectorBytes = ReadSector(sector);
                    int take = Math.Min(sectorSize, output.Length - copiedLarge);
                    Buffer.BlockCopy(sectorBytes, 0, output, copiedLarge, take);
                    copiedLarge += take;
                    if (copiedLarge >= output.Length)
                        break;
                }

                return output;
            }
        }

        private sealed record DirEntry(int Index, string Name, byte Type, int Start, long Size);
    }
}

public sealed record InfoDbPayloadMatch(string InfoDbPath, string StreamName, uint Tag, int Length, string HeadHex);
