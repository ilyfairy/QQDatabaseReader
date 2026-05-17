using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace QQDatabaseReader;

public static class PcqqDatabaseDecryptor
{
    public const int DefaultHeaderSize = 1024;
    public const int DefaultPageSize = 8192;

    private const uint Delta = 0x9E3779B9;
    private static readonly byte[] SQLiteHeader = "SQLite format 3\0"u8.ToArray();

    public static byte[] ParseKey(string input)
    {
        string s = input.Trim();
        if (s.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
            return Convert.FromHexString(s[4..]);

        if (s.Length == 32 && s.All(static c =>
                c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            return Convert.FromHexString(s);

        return Encoding.ASCII.GetBytes(s);
    }

    public static void DecryptToFile(
        string encryptedDatabasePath,
        string outputSqlitePath,
        ReadOnlySpan<byte> key,
        int headerSize = DefaultHeaderSize,
        int pageSize = DefaultPageSize)
    {
        if (key.Length != 16)
            throw new ArgumentException("PCQQ database key must be exactly 16 bytes.", nameof(key));
        if (headerSize < 0)
            throw new ArgumentOutOfRangeException(nameof(headerSize));
        if (pageSize <= 0 || (pageSize & 3) != 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be positive and divisible by 4.");

        using var input = new FileStream(encryptedDatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (input.Length <= headerSize)
            throw new InvalidDataException("Database is smaller than the PCQQ extended header.");

        long encryptedPayloadSize = input.Length - headerSize;
        if (encryptedPayloadSize % pageSize != 0)
            throw new InvalidDataException($"Encrypted payload size {encryptedPayloadSize} is not aligned to page size {pageSize}.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputSqlitePath))!);
        using var output = new FileStream(outputSqlitePath, FileMode.Create, FileAccess.Write, FileShare.Read);

        byte[] page = ArrayPool<byte>.Shared.Rent(pageSize);
        uint[] words = ArrayPool<uint>.Shared.Rent(pageSize / 4);
        Span<uint> keyWords = stackalloc uint[4];
        for (int i = 0; i < 4; i++)
            keyWords[i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(i * 4)..]);

        try
        {
            input.Position = headerSize;
            bool firstPage = true;
            while (input.Position < input.Length)
            {
                input.ReadExactly(page.AsSpan(0, pageSize));
                XxteaInPlace(page.AsSpan(0, pageSize), words.AsSpan(0, pageSize / 4), keyWords, decrypt: true);

                if (firstPage && !page.AsSpan(0, SQLiteHeader.Length).SequenceEqual(SQLiteHeader))
                {
                    string preview = Convert.ToHexString(page.AsSpan(0, Math.Min(16, pageSize)));
                    throw new InvalidDataException($"Key or codec mismatch: decrypted first page does not start with SQLite header. Preview={preview}");
                }

                output.Write(page.AsSpan(0, pageSize));
                firstPage = false;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(page);
            ArrayPool<uint>.Shared.Return(words);
        }
    }

    private static void XxteaInPlace(Span<byte> page, Span<uint> words, ReadOnlySpan<uint> key, bool decrypt)
    {
        if ((page.Length & 3) != 0)
            throw new ArgumentException("XXTEA buffer length must be divisible by 4.", nameof(page));

        int wordCount = page.Length / 4;
        for (int i = 0; i < wordCount; i++)
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(page[(i * 4)..]);

        Btea(words[..wordCount], key, decrypt ? -wordCount : wordCount);

        for (int i = 0; i < wordCount; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(page[(i * 4)..], words[i]);
    }

    private static void Btea(Span<uint> v, ReadOnlySpan<uint> key, int n)
    {
        if (n > 1)
        {
            uint z = v[n - 1];
            uint sum = 0;
            uint q = (uint)(6 + 52 / n);
            while (q-- > 0)
            {
                sum += Delta;
                uint e = (sum >> 2) & 3;
                int p;
                for (p = 0; p < n - 1; p++)
                {
                    uint y = v[p + 1];
                    z = v[p] += Mx(sum, y, z, p, e, key);
                }
                z = v[n - 1] += Mx(sum, v[0], z, p, e, key);
            }
        }
        else if (n < -1)
        {
            n = -n;
            uint y = v[0];
            uint sum = (uint)((6 + 52 / n) * Delta);
            while (sum != 0)
            {
                uint e = (sum >> 2) & 3;
                int p;
                for (p = n - 1; p > 0; p--)
                {
                    uint z = v[p - 1];
                    y = v[p] -= Mx(sum, y, z, p, e, key);
                }
                y = v[0] -= Mx(sum, y, v[n - 1], 0, e, key);
                sum -= Delta;
            }
        }
    }

    private static uint Mx(uint sum, uint y, uint z, int p, uint e, ReadOnlySpan<uint> key) =>
        (((z >> 5) ^ (y << 2)) + ((y >> 3) ^ (z << 4))) ^ ((sum ^ y) + (key[(p & 3) ^ (int)e] ^ z));
}
