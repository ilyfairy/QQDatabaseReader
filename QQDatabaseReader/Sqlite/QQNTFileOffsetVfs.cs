using System.Runtime.InteropServices;
using SQLitePCL;
using System.Runtime.CompilerServices;

namespace QQDatabaseReader.Sqlite;

public static class QQNTFileOffsetVfs
{
    public const string VfsName = "qqnt-vfs";
    public static ReadOnlySpan<byte> VfsNameUtf8 => "qqnt-vfs"u8;

    // 固定的偏移量：1024 字节（QQNT 头部大小）
    private const long FixedOffset = 1024;

    private static unsafe readonly sqlite3_io_methods _streamIoMethods = new sqlite3_io_methods()
    {
        iVersion = 3,
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
        // Version 2
        xShmMap = &xShmMap,
        xShmLock = &xShmLock,
        xShmBarrier = &xShmBarrier,
        xShmUnmap = &xShmUnmap,
        // Version 3
        xFetch = &xFetch,
        xUnfetch = &xUnfetch,
    };
    private static unsafe sqlite3_io_methods* _streamIoMethodsPtr => (sqlite3_io_methods*)Unsafe.AsPointer(ref Unsafe.AsRef(in _streamIoMethods));

    private static unsafe sqlite3_vfs _streamVfs = new()
    {
        iVersion = 1,
        szOsFile = sizeof(sqlite3_file) + sizeof(long), // Base + Offset
        mxPathname = 260,
        zName = (byte*)Unsafe.AsPointer(in VfsNameUtf8.GetPinnableReference()),
        pAppData = null, // 将在Register()中设置为原始VFS指针
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

    public static unsafe void Register()
    {
        nint defaultVfsPtr = raw.sqlite3_vfs_find(null);
        if (defaultVfsPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to find default VFS.");
        }

        sqlite3_vfs* origVfs = (sqlite3_vfs*)defaultVfsPtr;
        _streamVfs.pAppData = (void*)defaultVfsPtr;
        _streamVfs.szOsFile += origVfs->szOsFile;

        int result = raw.sqlite3_vfs_register((nint)Unsafe.AsPointer(ref _streamVfs), 0);
        if (result != raw.SQLITE_OK)
        {
            throw new InvalidOperationException("Failed to register custom VFS.");
        }
    }

    // 获取指向原始文件结构的指针
    private static unsafe sqlite3_file* GetOrigFile(sqlite3_file* pFile)
    {
        // 原始文件紧跟在所有字段之后
        // 布局: [Base(sqlite3_file)][Offset(long)][OrigFile(...)]
        var streamFile = (StreamSqlite3File*)pFile;
        byte* basePtr = (byte*)streamFile;
        int offsetToOrigFile = sizeof(sqlite3_file) + sizeof(long);
        return (sqlite3_file*)(basePtr + offsetToOrigFile);
    }

    private static unsafe long GetOffset(sqlite3_file* pFile)
    {
        var streamFile = (StreamSqlite3File*)pFile;
        return streamFile->Offset;
    }

    private static unsafe void SetOffset(sqlite3_file* pFile, long offset)
    {
        var streamFile = (StreamSqlite3File*)pFile;
        streamFile->Offset = offset;
    }


    [UnmanagedCallersOnly]
    public static unsafe int xOpen(sqlite3_vfs* pVfs, SqliteUtf8String zName, sqlite3_file* pFile, int flags, int* pOutFlags)
    {
        try
        {
            sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
            bool isMainDb = (flags & raw.SQLITE_OPEN_MAIN_DB) != 0;
            bool isTempDb = (flags & (0x00000008 | 0x00000010)) != 0; // SQLITE_OPEN_TEMP_DB | SQLITE_OPEN_TEMP_JOURNAL

            // 只对主数据库应用偏移量，其他文件（临时文件、journal等）不应用偏移
            if (!isMainDb || isTempDb)
            {
                // 非主库或临时文件：直接传递给原始 VFS，不应用偏移
                return origVfs->xOpen(origVfs, zName, pFile, flags, pOutFlags);
            }

            // 主数据库文件：使用包装模式
            sqlite3_file* origFile = GetOrigFile(pFile);
            new Span<byte>(origFile, origVfs->szOsFile).Clear();

            // 调用原始VFS打开真实文件
            int rc = origVfs->xOpen(origVfs, zName, origFile, flags, pOutFlags);
            if (rc != raw.SQLITE_OK)
            {
                return rc;
            }

            // 设置我们的IO methods和offset
            pFile->pMethods = _streamIoMethodsPtr;
            SetOffset(pFile, FixedOffset);

            return raw.SQLITE_OK;
        }
        catch
        {
            return raw.SQLITE_IOERR;
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe int xClose(sqlite3_file* pFile)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        return origFile->pMethods->xClose(origFile);
    }

    [UnmanagedCallersOnly]
    private static unsafe int xRead(sqlite3_file* pFile, void* zBuf, int iAmt, long iOfst)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        long offset = GetOffset(pFile);
        return origFile->pMethods->xRead(origFile, zBuf, iAmt, iOfst + offset);
    }

    [UnmanagedCallersOnly]
    private static unsafe int xWrite(sqlite3_file* pFile, void* zBuf, int iAmt, long iOfst)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        long offset = GetOffset(pFile);
        return origFile->pMethods->xWrite(origFile, zBuf, iAmt, iOfst + offset);
    }

    [UnmanagedCallersOnly]
    private static unsafe int xTruncate(sqlite3_file* pFile, long size)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        long offset = GetOffset(pFile);
        return origFile->pMethods->xTruncate(origFile, size + offset);
    }

    [UnmanagedCallersOnly]
    private static unsafe int xSync(sqlite3_file* pFile, int flags)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        return origFile->pMethods->xSync(origFile, flags);
    }

    [UnmanagedCallersOnly]
    private static unsafe int xFileSize(sqlite3_file* pFile, long* pSize)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        long offset = GetOffset(pFile);

        int rc = origFile->pMethods->xFileSize(origFile, pSize);
        if (rc == raw.SQLITE_OK)
        {
            *pSize -= offset;
        }
        return rc;
    }

    [UnmanagedCallersOnly]
    private static unsafe int xLock(sqlite3_file* pFile, int lockLevel)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        return origFile->pMethods->xLock(origFile, lockLevel);
    }

    [UnmanagedCallersOnly]
    private static unsafe int xUnlock(sqlite3_file* pFile, int lockLevel)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        return origFile->pMethods->xUnlock(origFile, lockLevel);
    }

    [UnmanagedCallersOnly]
    private static unsafe int xCheckReservedLock(sqlite3_file* pFile, int* pResOut)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        return origFile->pMethods->xCheckReservedLock(origFile, pResOut);
    }

    [UnmanagedCallersOnly]
    private static unsafe int xFileControl(sqlite3_file* pFile, int op, void* pArg)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);

        if (op == 5 && pArg != null) // SQLITE_FCNTL_SIZE_HINT
        {
            long* pSizeHint = (long*)pArg;
            *pSizeHint += GetOffset(pFile);
        }

        return origFile->pMethods->xFileControl(origFile, op, pArg);
    }

    [UnmanagedCallersOnly]
    private static unsafe int xSectorSize(sqlite3_file* pFile)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        return origFile->pMethods->xSectorSize(origFile);
    }

    [UnmanagedCallersOnly]
    private static unsafe int xDeviceCharacteristics(sqlite3_file* pFile)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        return origFile->pMethods->xDeviceCharacteristics(origFile);
    }

    // ==================== Version 2 methods (WAL mode support) ====================

    [UnmanagedCallersOnly]
    private static unsafe int xShmMap(sqlite3_file* pFile, int iPg, int pgsz, int bExtend, void** pp)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        return origFile->pMethods->xShmMap(origFile, iPg, pgsz, bExtend, pp);
    }

    [UnmanagedCallersOnly]
    private static unsafe int xShmLock(sqlite3_file* pFile, int offset, int n, int flags)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        return origFile->pMethods->xShmLock(origFile, offset, n, flags);
    }

    [UnmanagedCallersOnly]
    private static unsafe void xShmBarrier(sqlite3_file* pFile)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        origFile->pMethods->xShmBarrier(origFile);
    }

    [UnmanagedCallersOnly]
    private static unsafe int xShmUnmap(sqlite3_file* pFile, int deleteFlag)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        return origFile->pMethods->xShmUnmap(origFile, deleteFlag);
    }

    // ==================== Version 3 methods ====================

    [UnmanagedCallersOnly]
    private static unsafe int xFetch(sqlite3_file* pFile, long iOfst, int iAmt, void** pp)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        long offset = GetOffset(pFile);
        return origFile->pMethods->xFetch(origFile, iOfst + offset, iAmt, pp);
    }

    [UnmanagedCallersOnly]
    private static unsafe int xUnfetch(sqlite3_file* pFile, long iOfst, void* p)
    {
        sqlite3_file* origFile = GetOrigFile(pFile);
        long offset = GetOffset(pFile);
        return origFile->pMethods->xUnfetch(origFile, iOfst + offset, p);
    }



    [UnmanagedCallersOnly]
    private static unsafe int xDelete(sqlite3_vfs* pVfs, SqliteUtf8String zName, int syncDir)
    {
        sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
        return origVfs->xDelete(origVfs, zName, syncDir);
    }

    [UnmanagedCallersOnly]
    private static unsafe int xAccess(sqlite3_vfs* pVfs, SqliteUtf8String zName, int flags, int* pResOut)
    {
        sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
        return origVfs->xAccess(origVfs, zName, flags, pResOut);
    }

    [UnmanagedCallersOnly]
    private static unsafe int xFullPathname(sqlite3_vfs* pVfs, SqliteUtf8String zName, int nOut, byte* zOut)
    {
        sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
        return origVfs->xFullPathname(origVfs, zName, nOut, zOut);
    }

    [UnmanagedCallersOnly]
    private static unsafe void* xDlOpen(sqlite3_vfs* pVfs, SqliteUtf8String zFilename)
    {
        sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
        return origVfs->xDlOpen(origVfs, zFilename);
    }

    [UnmanagedCallersOnly]
    private static unsafe void xDlError(sqlite3_vfs* pVfs, int nByte, SqliteUtf8String zErrMsg)
    {
        sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
        origVfs->xDlError(origVfs, nByte, zErrMsg);
    }

    [UnmanagedCallersOnly]
    private static unsafe delegate* unmanaged<void> xDlSym(sqlite3_vfs* pVfs, void* pHandle, SqliteUtf8String zSymbol)
    {
        sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
        return origVfs->xDlSym(origVfs, pHandle, zSymbol);
    }

    [UnmanagedCallersOnly]
    private static unsafe void xDlClose(sqlite3_vfs* pVfs, void* pHandle)
    {
        sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
        origVfs->xDlClose(origVfs, pHandle);
    }

    [UnmanagedCallersOnly]
    private static unsafe int xRandomness(sqlite3_vfs* pVfs, int nByte, char* zOut)
    {
        sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
        return origVfs->xRandomness(origVfs, nByte, zOut);
    }

    [UnmanagedCallersOnly]
    private static unsafe int xSleep(sqlite3_vfs* pVfs, int microseconds)
    {
        sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
        return origVfs->xSleep(origVfs, microseconds);
    }

    [UnmanagedCallersOnly]
    private static unsafe int xCurrentTime(sqlite3_vfs* pVfs, double* pTime)
    {
        sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
        return origVfs->xCurrentTime(origVfs, pTime);
    }

    [UnmanagedCallersOnly]
    private static unsafe int xGetLastError(sqlite3_vfs* pVfs, int nByte, char* zErrMsg)
    {
        sqlite3_vfs* origVfs = (sqlite3_vfs*)pVfs->pAppData;
        return origVfs->xGetLastError(origVfs, nByte, zErrMsg);
    }

}
