using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace QQPinyinLetter;

/// <summary>
/// Registers a QQNT-compatible FTS5 tokenizer named <c>pinyin_letter</c> into an existing SQLite connection.
///
/// Runtime dependency: only the SQLite native library that owns the supplied sqlite3* handle.
/// No wrapper.node, no pinyin_data.bin sidecar, no load_extension DLL.
/// </summary>
public static unsafe partial class QQPinyinLetterTokenizer
{
    private const int SQLITE_OK = 0;
    private const int SQLITE_ROW = 100;
    private const int SQLITE_UTF8 = 1;

    private const int FTS5_TOKEN_COLOCATED = 0x0001;
    private const int FTS5_TOKENIZE_DOCUMENT = 0x0004;
    private const int FTS5_TOKENIZE_AUX = 0x0008;

    private static readonly PinyinTable s_table = PinyinTable.LoadEmbedded();
    private static readonly List<GCHandle> s_roots = new();
    private static readonly object s_gate = new();

    /// <summary>
    /// Register tokenizer on a raw sqlite3* handle.
    /// </summary>
    /// <param name="sqlite3Handle">Native sqlite3* connection handle.</param>
    /// <param name="sqliteLibraryName">SQLite native library name/path used by DllImport resolution. Default: sqlite3.</param>
    public static void Register(nint sqlite3Handle, string sqliteLibraryName = "sqlite3")
    {
        if (sqlite3Handle == 0)
            throw new ArgumentNullException(nameof(sqlite3Handle));

        NativeLibraryResolver.Ensure(sqliteLibraryName);

        fts5_api* api = GetFts5Api((sqlite3*)sqlite3Handle);
        if (api == null)
            throw new InvalidOperationException("SQLite FTS5 API is unavailable. Ensure SQLite is built with FTS5 enabled.");

        var state = new RegistrationState();
        state.Tokenizer.xCreate = (delegate* unmanaged[Cdecl]<void*, byte**, int, Fts5Tokenizer**, int>)&XCreate;
        state.Tokenizer.xDelete = (delegate* unmanaged[Cdecl]<Fts5Tokenizer*, void>)&XDelete;
        state.Tokenizer.xTokenize = (delegate* unmanaged[Cdecl]<Fts5Tokenizer*, void*, int, byte*, int, delegate* unmanaged[Cdecl]<void*, int, byte*, int, int, int, int>, int>)&XTokenize;

        fixed (byte* name = "pinyin_letter"u8)
        fixed (fts5_tokenizer* tok = &state.Tokenizer)
        {
            int rc = api->xCreateTokenizer(api, name, null, tok, null);
            if (rc != SQLITE_OK)
                throw new SqliteNativeException(rc, "fts5_api.xCreateTokenizer(pinyin_letter) failed");
        }

        lock (s_gate)
        {
            s_roots.Add(GCHandle.Alloc(state, GCHandleType.Normal));
        }
    }

    /// <summary>
    /// Convenience overload for providers exposing a Handle property returning sqlite3*.
    /// For Microsoft.Data.Sqlite, pass connection.Handle after opening the connection.
    /// </summary>
    public static void Register(object sqliteConnection, string sqliteLibraryName = "sqlite3")
    {
        if (sqliteConnection is null) throw new ArgumentNullException(nameof(sqliteConnection));
        var prop = sqliteConnection.GetType().GetProperty("Handle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop is null)
            throw new ArgumentException("Connection object does not expose a Handle property. Use Register(nint sqlite3Handle).", nameof(sqliteConnection));
        object? value = prop.GetValue(sqliteConnection);
        nint handle = value switch
        {
            IntPtr p => p,
            _ => throw new ArgumentException("Handle property is not IntPtr/nint.", nameof(sqliteConnection))
        };
        Register(handle, sqliteLibraryName);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int XCreate(void* pCtx, byte** azArg, int nArg, Fts5Tokenizer** ppOut)
    {
        try
        {
            byte state = 0;
            for (int i = 0; i < nArg; i++)
            {
                byte* arg = azArg[i];
                if (arg == null) continue;
                if ((arg[0] == (byte)'0' || arg[0] == (byte)'1') && arg[1] == 0)
                {
                    state = arg[0] == (byte)'1' ? (byte)1 : (byte)0;
                    break;
                }
            }
            byte* p = (byte*)Marshal.AllocHGlobal(1);
            *p = state;
            *ppOut = (Fts5Tokenizer*)p;
            return SQLITE_OK;
        }
        catch
        {
            return 7; // SQLITE_NOMEM / generic safe error for creation failure
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void XDelete(Fts5Tokenizer* tok)
    {
        if (tok != null)
            Marshal.FreeHGlobal((nint)tok);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int XTokenize(
        Fts5Tokenizer* tok,
        void* ctx,
        int flags,
        byte* text,
        int textLen,
        delegate* unmanaged[Cdecl]<void*, int, byte*, int, int, int, int> xToken)
    {
        try
        {
            bool docOrAux = (flags & (FTS5_TOKENIZE_DOCUMENT | FTS5_TOKENIZE_AUX)) != 0;
            bool colocated = docOrAux;

            int i = 0;
            int charPos = 0;
            Span<byte> lowerStack = stackalloc byte[512];

            while (i < textLen)
            {
                byte b = text[i];
                int cls = CharClass(b);

                if (cls == 4)
                {
                    int blen = Utf8LeadLen(b);
                    if (blen == 0 || i + blen > textLen)
                    {
                        int rcInvalid = xToken(ctx, 0, text + i, 1, charPos, charPos + 1);
                        if (rcInvalid != SQLITE_OK) return rcInvalid;
                        i++;
                        charPos++;
                        continue;
                    }

                    int start = charPos;
                    int end = charPos + 1;
                    int rc = xToken(ctx, 0, text + i, blen, start, end);
                    if (rc != SQLITE_OK) return rc;

                    if (colocated && TryDecodeUtf8(text + i, blen, out int cp) && s_table.TryGet(cp, out PinyinEntry entry))
                    {
                        foreach (ReadOnlyMemory<byte> pinyin in entry.Pinyin)
                        {
                            if (pinyin.Length == 1) continue;
                            fixed (byte* p = pinyin.Span)
                            {
                                rc = xToken(ctx, FTS5_TOKEN_COLOCATED, p, pinyin.Length, start, end);
                                if (rc != SQLITE_OK) return rc;
                            }
                        }
                        foreach (byte first in entry.FirstLetters)
                        {
                            byte tmp = first;
                            rc = xToken(ctx, FTS5_TOKEN_COLOCATED, &tmp, 1, start, end);
                            if (rc != SQLITE_OK) return rc;
                        }
                    }

                    i += blen;
                    charPos++;
                    continue;
                }

                if (cls == 0 || cls == 3)
                {
                    int j = i + 1;
                    int count = 1;
                    while (j < textLen && CharClass(text[j]) == cls)
                    {
                        j++;
                        count++;
                    }
                    byte sp = (byte)' ';
                    int rc = xToken(ctx, 0, &sp, 1, charPos, charPos + count);
                    if (rc != SQLITE_OK) return rc;
                    i = j;
                    charPos += count;
                    continue;
                }

                if (cls == 1)
                {
                    int j = i + 1;
                    while (j < textLen && CharClass(text[j]) == 1) j++;
                    int run = j - i;
                    int rc = TokenizeAsciiRun(ctx, text + i, run, charPos, docOrAux, xToken, lowerStack);
                    if (rc != SQLITE_OK) return rc;
                    i = j;
                    charPos += run;
                    continue;
                }

                // cls == 2 digit run
                {
                    int j = i + 1;
                    while (j < textLen && CharClass(text[j]) == 2) j++;
                    int run = j - i;
                    int rc = TokenizeDigitRun(ctx, text + i, run, charPos, docOrAux, xToken);
                    if (rc != SQLITE_OK) return rc;
                    i = j;
                    charPos += run;
                }
            }
            return SQLITE_OK;
        }
        catch
        {
            return 1; // SQLITE_ERROR
        }
    }

    private static int TokenizeAsciiRun(
        void* ctx,
        byte* text,
        int run,
        int charPos,
        bool docOrAux,
        delegate* unmanaged[Cdecl]<void*, int, byte*, int, int, int, int> xToken,
        Span<byte> lowerStack)
    {
        byte[]? heap = null;
        Span<byte> lower = run <= lowerStack.Length ? lowerStack[..run] : (heap = new byte[run]);
        for (int k = 0; k < run; k++)
            lower[k] = ToLowerAscii(text[k]);

        fixed (byte* pLower = lower)
        {
            if (!docOrAux && s_table.TrySegmentPinyin(lower, out var pinyinSegments))
            {
                var pos = charPos;
                foreach (var segment in pinyinSegments)
                {
                    fixed (byte* pSegment = segment.Span)
                    {
                        int rc = xToken(ctx, 0, pSegment, segment.Length, pos, pos + segment.Length);
                        if (rc != SQLITE_OK) return rc;
                    }

                    pos += segment.Length;
                }

                GC.KeepAlive(heap);
                return SQLITE_OK;
            }

            if (docOrAux)
            {
                int wholeRc = xToken(ctx, 0, pLower, run, charPos, charPos + run);
                if (wholeRc != SQLITE_OK) return wholeRc;

                if (s_table.TrySegmentPinyin(lower, out var documentSegments))
                {
                    var pos = charPos;
                    foreach (var segment in documentSegments)
                    {
                        fixed (byte* pSegment = segment.Span)
                        {
                            int rc = xToken(ctx, 0, pSegment, segment.Length, pos, pos + segment.Length);
                            if (rc != SQLITE_OK) return rc;
                        }

                        pos += segment.Length;
                    }
                }
            }

            for (int k = 0; k < run; k++)
            {
                int rc = xToken(ctx, 0, pLower + k, 1, charPos + k, charPos + k + 1);
                if (rc != SQLITE_OK) return rc;
            }
        }

        GC.KeepAlive(heap);
        return SQLITE_OK;
    }

    private static int TokenizeDigitRun(
        void* ctx,
        byte* text,
        int run,
        int charPos,
        bool docOrAux,
        delegate* unmanaged[Cdecl]<void*, int, byte*, int, int, int, int> xToken)
    {
        if (docOrAux)
        {
            int wholeRc = xToken(ctx, 0, text, run, charPos, charPos + run);
            if (wholeRc != SQLITE_OK) return wholeRc;
        }

        for (int k = 0; k < run; k++)
        {
            int rc = xToken(ctx, 0, text + k, 1, charPos + k, charPos + k + 1);
            if (rc != SQLITE_OK) return rc;
        }

        return SQLITE_OK;
    }

    private static fts5_api* GetFts5Api(sqlite3* db)
    {
        sqlite3_stmt* stmt = null;
        fts5_api* api = null;
        byte[] sql = Encoding.UTF8.GetBytes("SELECT fts5(?1)\0");
        fixed (byte* pSql = sql)
        fixed (byte* type = "fts5_api_ptr"u8)
        {
            int rc = Native.sqlite3_prepare_v2(db, pSql, -1, &stmt, null);
            if (rc != SQLITE_OK) throw new SqliteNativeException(rc, "sqlite3_prepare_v2(SELECT fts5(?1)) failed");
            try
            {
                rc = Native.sqlite3_bind_pointer(stmt, 1, &api, type, null);
                if (rc != SQLITE_OK) throw new SqliteNativeException(rc, "sqlite3_bind_pointer(fts5_api_ptr) failed");
                rc = Native.sqlite3_step(stmt);
                if (rc != SQLITE_ROW) throw new SqliteNativeException(rc, "sqlite3_step(SELECT fts5(?1)) failed");
            }
            finally
            {
                Native.sqlite3_finalize(stmt);
            }
        }
        return api;
    }

    private static int CharClass(byte c)
    {
        if (c >= (byte)'0' && c <= (byte)'9') return 2;
        if (c >= 0x80) return 4;
        if (c is 9 or 10 or 11 or 12 or 13 or 32) return 0;
        if ((c >= (byte)'A' && c <= (byte)'Z') || (c >= (byte)'a' && c <= (byte)'z')) return 1;
        if (c < 32 || c == 127) return 3;
        return 4;
    }

    private static byte ToLowerAscii(byte c) => c >= (byte)'A' && c <= (byte)'Z' ? (byte)(c + 32) : c;

    private static int Utf8LeadLen(byte b)
    {
        if (b < 0x80) return 1;
        if ((b & 0xE0) == 0xC0) return 2;
        if ((b & 0xF0) == 0xE0) return 3;
        if ((b & 0xF8) == 0xF0) return 4;
        return 0;
    }

    private static bool TryDecodeUtf8(byte* p, int len, out int cp)
    {
        cp = 0;
        switch (len)
        {
            case 1:
                cp = p[0];
                return true;
            case 2:
                if ((p[1] & 0xC0) != 0x80) return false;
                cp = ((p[0] & 0x1F) << 6) | (p[1] & 0x3F);
                return true;
            case 3:
                if ((p[1] & 0xC0) != 0x80 || (p[2] & 0xC0) != 0x80) return false;
                cp = ((p[0] & 0x0F) << 12) | ((p[1] & 0x3F) << 6) | (p[2] & 0x3F);
                return true;
            case 4:
                if ((p[1] & 0xC0) != 0x80 || (p[2] & 0xC0) != 0x80 || (p[3] & 0xC0) != 0x80) return false;
                cp = ((p[0] & 0x07) << 18) | ((p[1] & 0x3F) << 12) | ((p[2] & 0x3F) << 6) | (p[3] & 0x3F);
                return true;
            default:
                return false;
        }
    }

    private sealed class RegistrationState
    {
        public fts5_tokenizer Tokenizer;
    }

    private readonly struct PinyinEntry
    {
        public PinyinEntry(ReadOnlyMemory<byte>[] pinyin, byte[] firstLetters)
        {
            Pinyin = pinyin;
            FirstLetters = firstLetters;
        }
        public readonly ReadOnlyMemory<byte>[] Pinyin;
        public readonly byte[] FirstLetters;
    }

    private sealed class PinyinTable
    {
        private readonly int[] _codepoints;
        private readonly PinyinEntry[] _entries;
        private readonly ReadOnlyMemory<byte>[] _syllablesByLengthDesc;

        private PinyinTable(int[] codepoints, PinyinEntry[] entries, ReadOnlyMemory<byte>[] syllablesByLengthDesc)
        {
            _codepoints = codepoints;
            _entries = entries;
            _syllablesByLengthDesc = syllablesByLengthDesc;
        }

        public bool TryGet(int codepoint, out PinyinEntry entry)
        {
            int idx = Array.BinarySearch(_codepoints, codepoint);
            if (idx >= 0)
            {
                entry = _entries[idx];
                return true;
            }
            entry = default;
            return false;
        }

        public bool TrySegmentPinyin(ReadOnlySpan<byte> ascii, out List<ReadOnlyMemory<byte>> segments)
        {
            segments = new List<ReadOnlyMemory<byte>>();
            if (ascii.Length == 0)
                return false;

            var offset = 0;
            while (offset < ascii.Length)
            {
                ReadOnlyMemory<byte>? best = null;
                foreach (var syllable in _syllablesByLengthDesc)
                {
                    if (syllable.Length > ascii.Length - offset)
                        continue;

                    if (ascii.Slice(offset, syllable.Length).SequenceEqual(syllable.Span))
                    {
                        best = syllable;
                        break;
                    }
                }

                if (best is null)
                {
                    segments.Clear();
                    return false;
                }

                segments.Add(best.Value);
                offset += best.Value.Length;
            }

            return true;
        }

        public static PinyinTable LoadEmbedded()
        {
            string? resourceName = typeof(QQPinyinLetterTokenizer).Assembly
                .GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("pinyin_data.bin", StringComparison.OrdinalIgnoreCase));
            if (resourceName is null)
                throw new InvalidOperationException("Embedded pinyin_data.bin resource not found.");

            using Stream stream = typeof(QQPinyinLetterTokenizer).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException("Embedded pinyin_data.bin resource cannot be opened.");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            byte[] blob = ms.ToArray();
            ReadOnlySpan<byte> data = blob;

            int off = 0;
            uint magic = ReadU32(data, ref off);
            if (magic != 0x314C5950u) throw new InvalidDataException("Invalid pinyin_data.bin magic.");

            uint sylCount = ReadU32(data, ref off);
            byte[][] syllables = new byte[sylCount][];
            for (int i = 0; i < syllables.Length; i++)
            {
                int len = data[off++];
                syllables[i] = data.Slice(off, len).ToArray();
                off += len;
            }

            uint entryCountU = ReadU32(data, ref off);
            int entryCount = checked((int)entryCountU);
            int[] cps = new int[entryCount];
            PinyinEntry[] entries = new PinyinEntry[entryCount];

            for (int i = 0; i < entryCount; i++)
            {
                cps[i] = checked((int)ReadU32(data, ref off));
                int pc = data[off++];
                var pys = new ReadOnlyMemory<byte>[pc];
                for (int k = 0; k < pc; k++)
                {
                    int idx = ReadU16(data, ref off);
                    pys[k] = syllables[idx];
                }
                int fc = data[off++];
                byte[] fl = data.Slice(off, fc).ToArray();
                off += fc;
                entries[i] = new PinyinEntry(pys, fl);
            }
            var syllablesByLengthDesc = syllables
                .Select(static syllable => (ReadOnlyMemory<byte>)syllable)
                .OrderByDescending(static syllable => syllable.Length)
                .ThenBy(static syllable => Encoding.ASCII.GetString(syllable.Span))
                .ToArray();

            return new PinyinTable(cps, entries, syllablesByLengthDesc);
        }

        private static ushort ReadU16(ReadOnlySpan<byte> data, ref int off)
        {
            ushort v = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(off, 2));
            off += 2;
            return v;
        }

        private static uint ReadU32(ReadOnlySpan<byte> data, ref int off)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off, 4));
            off += 4;
            return v;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct sqlite3 { }

    [StructLayout(LayoutKind.Sequential)]
    private struct sqlite3_stmt { }

    [StructLayout(LayoutKind.Sequential)]
    private struct Fts5Tokenizer { }

    [StructLayout(LayoutKind.Sequential)]
    private struct fts5_tokenizer
    {
        public delegate* unmanaged[Cdecl]<void*, byte**, int, Fts5Tokenizer**, int> xCreate;
        public delegate* unmanaged[Cdecl]<Fts5Tokenizer*, void> xDelete;
        public delegate* unmanaged[Cdecl]<Fts5Tokenizer*, void*, int, byte*, int, delegate* unmanaged[Cdecl]<void*, int, byte*, int, int, int, int>, int> xTokenize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct fts5_api
    {
        public int iVersion;
        public delegate* unmanaged[Cdecl]<fts5_api*, byte*, void*, fts5_tokenizer*, delegate* unmanaged[Cdecl]<void*, void>, int> xCreateTokenizer;
        public nint xFindTokenizer;
        public nint xCreateFunction;
    }

    public sealed class SqliteNativeException : Exception
    {
        public SqliteNativeException(int code, string message) : base($"{message} (SQLite rc={code})") => Code = code;
        public int Code { get; }
    }

    private static class NativeLibraryResolver
    {
        private static string s_libraryName = "sqlite3";
        private static bool s_configured;
        private static readonly object s_lock = new();

        public static void Ensure(string libraryName)
        {
            if (string.IsNullOrWhiteSpace(libraryName)) libraryName = "sqlite3";
            lock (s_lock)
            {
                s_libraryName = libraryName;
                if (!s_configured)
                {
                    NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
                    s_configured = true;
                }
            }
        }

        private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName == "sqlite3_dyn")
            {
                if (NativeLibrary.TryLoad(s_libraryName, assembly, searchPath, out nint handle))
                    return handle;
            }
            return 0;
        }
    }

    private static unsafe partial class Native
    {
        [LibraryImport("sqlite3_dyn")]
        public static partial int sqlite3_prepare_v2(sqlite3* db, byte* zSql, int nByte, sqlite3_stmt** ppStmt, byte** pzTail);

        [LibraryImport("sqlite3_dyn")]
        public static partial int sqlite3_bind_pointer(sqlite3_stmt* stmt, int index, void* pointer, byte* type, delegate* unmanaged[Cdecl]<void*, void> destroy);

        [LibraryImport("sqlite3_dyn")]
        public static partial int sqlite3_step(sqlite3_stmt* stmt);

        [LibraryImport("sqlite3_dyn")]
        public static partial int sqlite3_finalize(sqlite3_stmt* stmt);
    }
}
