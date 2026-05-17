using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SQLitePCL;

namespace QQDatabaseReader.Sqlite;

/// <summary>
/// PCQQ Msg3.0.db VFS.
///
/// File layout:
///   [1024-byte PCQQ ext header][8192-byte XXTEA encrypted SQLite page 1][page 2]...
///
/// This VFS exposes the file to SQLite as a normal plaintext SQLite database by decrypting pages in xRead.
/// It is intentionally read-only for the main database file; temp/journal files are delegated to the original VFS.
/// </summary>
public static unsafe class PCQQPageDecryptVfs
{
    public const string VfsName = "pcqq-vfs";
    public static ReadOnlySpan<byte> VfsNameUtf8 => "pcqq-vfs"u8;

    public const int HeaderSize = 1024;
    public const int PageSize = 8192;

    private const uint Delta = 0x9E3779B9;
    private const int SQLITE_READONLY = 8;
    private const int SQLITE_IOERR = 10;
    private const int SQLITE_IOERR_SHORT_READ = SQLITE_IOERR | (2 << 8);

    private static bool _registered;
    private static uint _k0;
    private static uint _k1;
    private static uint _k2;
    private static uint _k3;

    private static readonly sqlite3_io_methods _ioMethods = new()
    {
        iVersion = 2,
        xClose = &xClose,
        xRead = &xRead,
        xWrite = &xWrite,
        xTruncate = &xTruncate,
        xSync = &xSync,
        xFileSize = &xFileSize,
        xLock = &xLock,
        xUnlock = &xUnlock,
        xCheckReservedLock = &xCheckReservedLock,
        xFileControl = &xFileControl,
        xSectorSize = &xSectorSize,
        xDeviceCharacteristics = &xDeviceCharacteristics,
        xShmMap = &xShmMap,
        xShmLock = &xShmLock,
        xShmBarrier = &xShmBarrier,
        xShmUnmap = &xShmUnmap,
    };

    private static sqlite3_io_methods* IoMethodsPtr =>
        (sqlite3_io_methods*)Unsafe.AsPointer(ref Unsafe.AsRef(in _ioMethods));

    private static sqlite3_vfs _vfs = new()
    {
        iVersion = 1,
        szOsFile = sizeof(sqlite3_file),
        mxPathname = 260,
        zName = (byte*)Unsafe.AsPointer(in VfsNameUtf8.GetPinnableReference()),
        pAppData = null,
        xOpen = &xOpen,
        xDelete = &xDelete,
        xAccess = &xAccess,
        xFullPathname = &xFullPathname,
        xDlOpen = &xDlOpen,
        xDlError = &xDlError,
        xDlSym = &xDlSym,
        xDlClose = &xDlClose,
        xRandomness = &xRandomness,
        xSleep = &xSleep,
        xCurrentTime = &xCurrentTime,
        xGetLastError = &xGetLastError,
    };

    public static void Register(string key) => Register(PcqqDatabaseDecryptor.ParseKey(key));

    public static void Register(ReadOnlySpan<byte> key)
    {
        if (key.Length != 16)
            throw new ArgumentException("PCQQ VFS key must be exactly 16 bytes.", nameof(key));

        _k0 = BinaryPrimitives.ReadUInt32LittleEndian(key[0..4]);
        _k1 = BinaryPrimitives.ReadUInt32LittleEndian(key[4..8]);
        _k2 = BinaryPrimitives.ReadUInt32LittleEndian(key[8..12]);
        _k3 = BinaryPrimitives.ReadUInt32LittleEndian(key[12..16]);

        if (_registered)
            return;

        nint defaultVfsPtr = raw.sqlite3_vfs_find(null);
        if (defaultVfsPtr == IntPtr.Zero)
            throw new InvalidOperationException("Failed to find default VFS.");

        sqlite3_vfs* origVfs = (sqlite3_vfs*)defaultVfsPtr;
        _vfs.pAppData = (void*)defaultVfsPtr;
        _vfs.szOsFile = sizeof(sqlite3_file) + origVfs->szOsFile;

        int rc = raw.sqlite3_vfs_register((nint)Unsafe.AsPointer(ref _vfs), 0);
        if (rc != raw.SQLITE_OK)
            throw new InvalidOperationException($"Failed to register {VfsName}: sqlite rc={rc}.");

        _registered = true;
    }

    private static sqlite3_file* GetOrigFile(sqlite3_file* pFile)
    {
        byte* basePtr = (byte*)pFile;
        return (sqlite3_file*)(basePtr + sizeof(sqlite3_file));
    }

    [UnmanagedCallersOnly]
    private static int xOpen(sqlite3_vfs* pVfs, SqliteUtf8String zName, sqlite3_file* pFile, int flags, int* pOutFlags)
    {
        try
        {
            sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
            bool isMainDb = (flags & raw.SQLITE_OPEN_MAIN_DB) != 0;
            bool isTempDb = (flags & (0x00000008 | 0x00000010)) != 0;

            if (!isMainDb || isTempDb)
                return origVfs->xOpen(origVfs, zName, pFile, flags, pOutFlags);

            sqlite3_file* origFile = GetOrigFile(pFile);
            new Span<byte>(origFile, origVfs->szOsFile).Clear();

            int rc = origVfs->xOpen(origVfs, zName, origFile, flags, pOutFlags);
            if (rc != raw.SQLITE_OK)
                return rc;

            pFile->pMethods = IoMethodsPtr;
            return raw.SQLITE_OK;
        }
        catch
        {
            return SQLITE_IOERR;
        }
    }

    [UnmanagedCallersOnly]
    private static int xClose(sqlite3_file* pFile)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        return origFile->pMethods->xClose(origFile);
    }

    [UnmanagedCallersOnly]
    private static int xRead(sqlite3_file* pFile, void* zBuf, int iAmt, long iOfst)
    {
        try
        {
            if (iAmt < 0 || iOfst < 0)
                return SQLITE_IOERR;

            sqlite3_file* origFile = GetOrigFile(pFile);
            Span<byte> output = new(zBuf, iAmt);
            output.Clear();

            long plainFileSize = 0;
            int rc = origFile->pMethods->xFileSize(origFile, &plainFileSize);
            if (rc != raw.SQLITE_OK)
                return rc;

            plainFileSize = Math.Max(0, plainFileSize - HeaderSize);
            if (iOfst >= plainFileSize)
                return SQLITE_IOERR_SHORT_READ;

            int requested = iAmt;
            if (iOfst + requested > plainFileSize)
                requested = checked((int)(plainFileSize - iOfst));

            byte[] encryptedPage = ArrayPool<byte>.Shared.Rent(PageSize);
            uint[] words = ArrayPool<uint>.Shared.Rent(PageSize / 4);
            try
            {
                int copied = 0;
                while (copied < requested)
                {
                    long logicalOffset = iOfst + copied;
                    long pageIndex = logicalOffset / PageSize;
                    int pageOffset = (int)(logicalOffset % PageSize);
                    int take = Math.Min(requested - copied, PageSize - pageOffset);
                    long physicalOffset = HeaderSize + pageIndex * PageSize;

                    fixed (byte* encryptedPagePtr = encryptedPage)
                        rc = origFile->pMethods->xRead(origFile, encryptedPagePtr, PageSize, physicalOffset);

                    if (rc != raw.SQLITE_OK)
                        return rc;

                    DecryptPageInPlace(encryptedPage.AsSpan(0, PageSize), words.AsSpan(0, PageSize / 4));
                    encryptedPage.AsSpan(pageOffset, take).CopyTo(output[copied..]);
                    copied += take;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(encryptedPage);
                ArrayPool<uint>.Shared.Return(words);
            }

            return requested == iAmt ? raw.SQLITE_OK : SQLITE_IOERR_SHORT_READ;
        }
        catch
        {
            return SQLITE_IOERR;
        }
    }

    [UnmanagedCallersOnly]
    private static int xWrite(sqlite3_file* pFile, void* zBuf, int iAmt, long iOfst) => SQLITE_READONLY;

    [UnmanagedCallersOnly]
    private static int xTruncate(sqlite3_file* pFile, long size) => SQLITE_READONLY;

    [UnmanagedCallersOnly]
    private static int xSync(sqlite3_file* pFile, int flags)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        return origFile->pMethods->xSync(origFile, flags);
    }

    [UnmanagedCallersOnly]
    private static int xFileSize(sqlite3_file* pFile, long* pSize)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        int rc = origFile->pMethods->xFileSize(origFile, pSize);
        if (rc == raw.SQLITE_OK)
            *pSize = Math.Max(0, *pSize - HeaderSize);
        return rc;
    }

    [UnmanagedCallersOnly]
    private static int xLock(sqlite3_file* pFile, int lockLevel)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        return origFile->pMethods->xLock(origFile, lockLevel);
    }

    [UnmanagedCallersOnly]
    private static int xUnlock(sqlite3_file* pFile, int lockLevel)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        return origFile->pMethods->xUnlock(origFile, lockLevel);
    }

    [UnmanagedCallersOnly]
    private static int xCheckReservedLock(sqlite3_file* pFile, int* pResOut)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        return origFile->pMethods->xCheckReservedLock(origFile, pResOut);
    }

    [UnmanagedCallersOnly]
    private static int xFileControl(sqlite3_file* pFile, int op, void* pArg)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        if (op == 5 && pArg != null)
        {
            long* pSizeHint = (long*)pArg;
            *pSizeHint += HeaderSize;
        }
        return origFile->pMethods->xFileControl(origFile, op, pArg);
    }

    [UnmanagedCallersOnly]
    private static int xSectorSize(sqlite3_file* pFile)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        return origFile->pMethods->xSectorSize(origFile);
    }

    [UnmanagedCallersOnly]
    private static int xDeviceCharacteristics(sqlite3_file* pFile)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        return origFile->pMethods->xDeviceCharacteristics(origFile);
    }

    [UnmanagedCallersOnly]
    private static int xShmMap(sqlite3_file* pFile, int iPg, int pgsz, int bExtend, void** pp)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        return origFile->pMethods->xShmMap(origFile, iPg, pgsz, bExtend, pp);
    }

    [UnmanagedCallersOnly]
    private static int xShmLock(sqlite3_file* pFile, int offset, int n, int flags)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        return origFile->pMethods->xShmLock(origFile, offset, n, flags);
    }

    [UnmanagedCallersOnly]
    private static void xShmBarrier(sqlite3_file* pFile)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        origFile->pMethods->xShmBarrier(origFile);
    }

    [UnmanagedCallersOnly]
    private static int xShmUnmap(sqlite3_file* pFile, int deleteFlag)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        return origFile->pMethods->xShmUnmap(origFile, deleteFlag);
    }

    [UnmanagedCallersOnly]
    private static int xDelete(sqlite3_vfs* pVfs, SqliteUtf8String zName, int syncDir)
    {
        sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
        return origVfs->xDelete(origVfs, zName, syncDir);
    }

    [UnmanagedCallersOnly]
    private static int xAccess(sqlite3_vfs* pVfs, SqliteUtf8String zName, int flags, int* pResOut)
    {
        sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
        return origVfs->xAccess(origVfs, zName, flags, pResOut);
    }

    [UnmanagedCallersOnly]
    private static int xFullPathname(sqlite3_vfs* pVfs, SqliteUtf8String zName, int nOut, byte* zOut)
    {
        sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
        return origVfs->xFullPathname(origVfs, zName, nOut, zOut);
    }

    [UnmanagedCallersOnly]
    private static void* xDlOpen(sqlite3_vfs* pVfs, SqliteUtf8String zFilename)
    {
        sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
        return origVfs->xDlOpen(origVfs, zFilename);
    }

    [UnmanagedCallersOnly]
    private static void xDlError(sqlite3_vfs* pVfs, int nByte, SqliteUtf8String zErrMsg)
    {
        sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
        origVfs->xDlError(origVfs, nByte, zErrMsg);
    }

    [UnmanagedCallersOnly]
    private static delegate* unmanaged<void> xDlSym(sqlite3_vfs* pVfs, void* pHandle, SqliteUtf8String zSymbol)
    {
        sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
        return origVfs->xDlSym(origVfs, pHandle, zSymbol);
    }

    [UnmanagedCallersOnly]
    private static void xDlClose(sqlite3_vfs* pVfs, void* pHandle)
    {
        sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
        origVfs->xDlClose(origVfs, pHandle);
    }

    [UnmanagedCallersOnly]
    private static int xRandomness(sqlite3_vfs* pVfs, int nByte, char* zOut)
    {
        sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
        return origVfs->xRandomness(origVfs, nByte, zOut);
    }

    [UnmanagedCallersOnly]
    private static int xSleep(sqlite3_vfs* pVfs, int microseconds)
    {
        sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
        return origVfs->xSleep(origVfs, microseconds);
    }

    [UnmanagedCallersOnly]
    private static int xCurrentTime(sqlite3_vfs* pVfs, double* pTime)
    {
        sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
        return origVfs->xCurrentTime(origVfs, pTime);
    }

    [UnmanagedCallersOnly]
    private static int xGetLastError(sqlite3_vfs* pVfs, int nByte, char* zErrMsg)
    {
        sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
        return origVfs->xGetLastError(origVfs, nByte, zErrMsg);
    }

    private static void DecryptPageInPlace(Span<byte> page, Span<uint> words)
    {
        for (int i = 0; i < PageSize / 4; i++)
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(page[(i * 4)..]);

        Span<uint> key = stackalloc uint[4] { _k0, _k1, _k2, _k3 };
        Btea(words[..(PageSize / 4)], key, -(PageSize / 4));

        for (int i = 0; i < PageSize / 4; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(page[(i * 4)..], words[i]);
    }

    private static void Btea(Span<uint> v, ReadOnlySpan<uint> key, int n)
    {
        if (n >= -1)
            return;

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

    private static uint Mx(uint sum, uint y, uint z, int p, uint e, ReadOnlySpan<uint> key) =>
        (((z >> 5) ^ (y << 2)) + ((y >> 3) ^ (z << 4))) ^ ((sum ^ y) + (key[(p & 3) ^ (int)e] ^ z));
}
