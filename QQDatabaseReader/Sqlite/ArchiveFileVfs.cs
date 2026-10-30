using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using QQDatabaseReader.Archives;
using SQLitePCL;

namespace QQDatabaseReader.Sqlite;

/// <summary>
/// 只读 VFS: 让 SQLite 直接读 tar / zip 里仅存储的数据库文件, 例如 D:\backup\data.tar\databases\nt_db\nt_qq_xxx\nt_msg.db.
/// 不是归档路径的文件 (临时文件等) 交给默认 VFS.
/// 主库开头是 QQNT 的 "SQLite header 3" 时跳过 1024 字节头; -wal 等附属文件也从归档里读, 归档里没有就当作空文件.
/// 没有共享内存方法 (iVersion 1), 打开后要先设 PRAGMA locking_mode = EXCLUSIVE, WAL 索引放进进程内存
/// </summary>
public static unsafe class ArchiveFileVfs
{
    public const string VfsName = "qqdbr-archive";

    private const int QQNtHeaderSize = 1024;
    private const int SQLITE_IOERR_SHORT_READ = 522;
    private const int SQLITE_NOTFOUND = 12;
    private const int SQLITE_OPEN_CREATE = 0x00000004;
    private const int SQLITE_ACCESS_EXISTS = 0;
    private const int SQLITE_ACCESS_READ = 2;

    private static sqlite3_vfs* _vfs;
    private static sqlite3_io_methods* _methods;

    [StructLayout(LayoutKind.Sequential)]
    private struct ArchiveSqlite3File
    {
        public sqlite3_file Base;
        public nint State;
    }

    private sealed class FileState(SafeFileHandle? handle, long start, long length)
    {
        public SafeFileHandle? Handle { get; } = handle;
        public long Start { get; } = start;
        public long Length { get; } = length;
    }

    public static void Register()
    {
        if (_vfs != null)
            return;

        var defaultVfs = (sqlite3_vfs*)raw.sqlite3_vfs_find(null);
        if (defaultVfs == null)
            throw new InvalidOperationException("Failed to find default VFS.");

        var methods = (sqlite3_io_methods*)NativeMemory.AllocZeroed((nuint)sizeof(sqlite3_io_methods));
        methods->iVersion = 1;
        methods->xClose = &xClose;
        methods->xRead = &xRead;
        methods->xWrite = &xWrite;
        methods->xTruncate = &xTruncate;
        methods->xSync = &xSync;
        methods->xFileSize = &xFileSize;
        methods->xLock = &xLock;
        methods->xUnlock = &xLock;
        methods->xCheckReservedLock = &xCheckReservedLock;
        methods->xFileControl = &xFileControl;
        methods->xSectorSize = &xSectorSize;
        methods->xDeviceCharacteristics = &xDeviceCharacteristics;

        var name = Encoding.UTF8.GetBytes(VfsName + "\0");
        var namePtr = (byte*)NativeMemory.Alloc((nuint)name.Length);
        name.CopyTo(new Span<byte>(namePtr, name.Length));

        var vfs = (sqlite3_vfs*)NativeMemory.AllocZeroed((nuint)sizeof(sqlite3_vfs));
        vfs->iVersion = 1;
        vfs->szOsFile = Math.Max(sizeof(ArchiveSqlite3File), defaultVfs->szOsFile);
        vfs->mxPathname = Math.Max(defaultVfs->mxPathname, 4096);
        vfs->zName = namePtr;
        vfs->pAppData = defaultVfs;
        vfs->xOpen = &xOpen;
        vfs->xDelete = &xDelete;
        vfs->xAccess = &xAccess;
        vfs->xFullPathname = &xFullPathname;
        vfs->xDlOpen = &xDlOpen;
        vfs->xDlError = &xDlError;
        vfs->xDlSym = &xDlSym;
        vfs->xDlClose = &xDlClose;
        vfs->xRandomness = &xRandomness;
        vfs->xSleep = &xSleep;
        vfs->xCurrentTime = &xCurrentTime;
        vfs->xGetLastError = &xGetLastError;

        if (raw.sqlite3_vfs_register((nint)vfs, 0) != raw.SQLITE_OK)
            throw new InvalidOperationException("Failed to register archive VFS.");

        _methods = methods;
        _vfs = vfs;
    }

    private static sqlite3_vfs* Default(sqlite3_vfs* pVfs) => (sqlite3_vfs*)pVfs->pAppData;

    private static FileState State(sqlite3_file* pFile) =>
        (FileState)GCHandle.FromIntPtr(((ArchiveSqlite3File*)pFile)->State).Target!;

    private static string? ArchiveName(SqliteUtf8String zName)
    {
        if (zName.ptr == null)
            return null;
        var name = zName.ToUtf8String();
        return ArchiveFile.IsArchivePath(name) ? name : null;
    }

    [UnmanagedCallersOnly]
    private static int xOpen(sqlite3_vfs* pVfs, SqliteUtf8String zName, sqlite3_file* pFile, int flags, int* pOutFlags)
    {
        try
        {
            var path = zName.ptr == null ? null : zName.ToUtf8String();
            if (path is null || !ArchiveFile.TryResolve(path, out var archive, out var entryName))
                return Default(pVfs)->xOpen(Default(pVfs), zName, pFile, flags, pOutFlags);

            pFile->pMethods = null;
            var isMainDb = (flags & raw.SQLITE_OPEN_MAIN_DB) != 0;
            FileState state;
            if (archive.TryFind(entryName, out var entry))
            {
                if (entry.Storage != ArchiveEntryStorage.Stored)
                    return raw.SQLITE_CANTOPEN;

                var handle = File.OpenHandle(archive.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.RandomAccess);
                try
                {
                    var skip = isMainDb && HasQQNtHeader(handle, entry) ? QQNtHeaderSize : 0;
                    state = new FileState(handle, entry.DataOffset + skip, entry.Length - skip);
                }
                catch
                {
                    // 读头失败 (例如移动硬盘断开): 立即关闭, 不要等终结器, 否则 Windows 上归档一直被占用
                    handle.Dispose();
                    throw;
                }
            }
            else if (isMainDb)
            {
                return raw.SQLITE_CANTOPEN;
            }
            else
            {
                // 归档里没有的 -wal / -journal: 当作空文件, SQLite 会认为没有未合并的内容
                state = new FileState(null, 0, 0);
            }

            ((ArchiveSqlite3File*)pFile)->State = GCHandle.ToIntPtr(GCHandle.Alloc(state));
            pFile->pMethods = _methods;
            if (pOutFlags != null)
                *pOutFlags = (flags & ~(raw.SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE)) | raw.SQLITE_OPEN_READONLY;
            return raw.SQLITE_OK;
        }
        catch
        {
            pFile->pMethods = null;
            return raw.SQLITE_CANTOPEN;
        }
    }

    private static bool HasQQNtHeader(SafeFileHandle handle, ArchiveEntry entry)
    {
        if (entry.Length < QQNtHeaderSize)
            return false;
        Span<byte> magic = stackalloc byte[15];
        return RandomAccess.Read(handle, magic, entry.DataOffset) == magic.Length && magic.SequenceEqual("SQLite header 3"u8);
    }

    [UnmanagedCallersOnly]
    private static int xClose(sqlite3_file* pFile)
    {
        try
        {
            var file = (ArchiveSqlite3File*)pFile;
            if (file->State != 0)
            {
                var handle = GCHandle.FromIntPtr(file->State);
                ((FileState)handle.Target!).Handle?.Dispose();
                handle.Free();
                file->State = 0;
            }

            return raw.SQLITE_OK;
        }
        catch
        {
            return raw.SQLITE_IOERR;
        }
    }

    // 读的范围限制在这个文件内, 不会读到归档里的下一个文件; 不足部分补零并返回短读
    [UnmanagedCallersOnly]
    private static int xRead(sqlite3_file* pFile, void* zBuf, int iAmt, long iOfst)
    {
        try
        {
            var state = State(pFile);
            var buffer = new Span<byte>(zBuf, iAmt);
            var available = (int)Math.Clamp(state.Length - iOfst, 0, iAmt);
            var total = 0;
            while (total < available)
            {
                var read = RandomAccess.Read(state.Handle!, buffer[total..available], state.Start + iOfst + total);
                if (read <= 0)
                    break;
                total += read;
            }

            if (total == iAmt)
                return raw.SQLITE_OK;

            buffer[total..].Clear();
            return SQLITE_IOERR_SHORT_READ;
        }
        catch
        {
            return raw.SQLITE_IOERR;
        }
    }

    [UnmanagedCallersOnly]
    private static int xWrite(sqlite3_file* pFile, void* zBuf, int iAmt, long iOfst) => raw.SQLITE_READONLY;

    [UnmanagedCallersOnly]
    private static int xTruncate(sqlite3_file* pFile, long size) => raw.SQLITE_READONLY;

    [UnmanagedCallersOnly]
    private static int xSync(sqlite3_file* pFile, int flags) => raw.SQLITE_OK;

    [UnmanagedCallersOnly]
    private static int xFileSize(sqlite3_file* pFile, long* pSize)
    {
        try
        {
            *pSize = State(pFile).Length;
            return raw.SQLITE_OK;
        }
        catch
        {
            return raw.SQLITE_IOERR;
        }
    }

    // 只读且独占使用, 不需要真正加锁
    [UnmanagedCallersOnly]
    private static int xLock(sqlite3_file* pFile, int lockLevel) => raw.SQLITE_OK;

    [UnmanagedCallersOnly]
    private static int xCheckReservedLock(sqlite3_file* pFile, int* pResOut)
    {
        *pResOut = 0;
        return raw.SQLITE_OK;
    }

    [UnmanagedCallersOnly]
    private static int xFileControl(sqlite3_file* pFile, int op, void* pArg) => SQLITE_NOTFOUND;

    [UnmanagedCallersOnly]
    private static int xSectorSize(sqlite3_file* pFile) => 4096;

    [UnmanagedCallersOnly]
    private static int xDeviceCharacteristics(sqlite3_file* pFile) => 0;

    [UnmanagedCallersOnly]
    private static int xDelete(sqlite3_vfs* pVfs, SqliteUtf8String zName, int syncDir)
    {
        try
        {
            return ArchiveName(zName) is null ? Default(pVfs)->xDelete(Default(pVfs), zName, syncDir) : raw.SQLITE_OK;
        }
        catch
        {
            return raw.SQLITE_IOERR;
        }
    }

    [UnmanagedCallersOnly]
    private static int xAccess(sqlite3_vfs* pVfs, SqliteUtf8String zName, int flags, int* pResOut)
    {
        try
        {
            var path = ArchiveName(zName);
            if (path is null)
                return Default(pVfs)->xAccess(Default(pVfs), zName, flags, pResOut);

            // 和 unix VFS 一致: 长度为 0 的文件按不存在处理; 归档内一律不可写
            var exists = ArchiveFile.TryGetEntry(path, out _, out var entry);
            *pResOut = flags switch
            {
                SQLITE_ACCESS_EXISTS => exists && entry.Length > 0 ? 1 : 0,
                SQLITE_ACCESS_READ => exists ? 1 : 0,
                _ => 0,
            };
            return raw.SQLITE_OK;
        }
        catch
        {
            return raw.SQLITE_IOERR;
        }
    }

    // 默认 VFS 会逐级检查路径 (Linux 上遇到 "data.tar/xxx" 会报错), 归档路径自己规范化
    [UnmanagedCallersOnly]
    private static int xFullPathname(sqlite3_vfs* pVfs, SqliteUtf8String zName, int nOut, byte* zOut)
    {
        try
        {
            var path = ArchiveName(zName);
            if (path is null)
                return Default(pVfs)->xFullPathname(Default(pVfs), zName, nOut, zOut);

            var bytes = Encoding.UTF8.GetBytes(Path.GetFullPath(path));
            if (bytes.Length + 1 > nOut)
                return raw.SQLITE_CANTOPEN;
            bytes.CopyTo(new Span<byte>(zOut, nOut));
            zOut[bytes.Length] = 0;
            return raw.SQLITE_OK;
        }
        catch
        {
            return raw.SQLITE_CANTOPEN;
        }
    }

    [UnmanagedCallersOnly]
    private static void* xDlOpen(sqlite3_vfs* pVfs, SqliteUtf8String zFilename) => Default(pVfs)->xDlOpen(Default(pVfs), zFilename);

    [UnmanagedCallersOnly]
    private static void xDlError(sqlite3_vfs* pVfs, int nByte, SqliteUtf8String zErrMsg) => Default(pVfs)->xDlError(Default(pVfs), nByte, zErrMsg);

    [UnmanagedCallersOnly]
    private static delegate* unmanaged<void> xDlSym(sqlite3_vfs* pVfs, void* pHandle, SqliteUtf8String zSymbol) =>
        Default(pVfs)->xDlSym(Default(pVfs), pHandle, zSymbol);

    [UnmanagedCallersOnly]
    private static void xDlClose(sqlite3_vfs* pVfs, void* pHandle) => Default(pVfs)->xDlClose(Default(pVfs), pHandle);

    [UnmanagedCallersOnly]
    private static int xRandomness(sqlite3_vfs* pVfs, int nByte, char* zOut) => Default(pVfs)->xRandomness(Default(pVfs), nByte, zOut);

    [UnmanagedCallersOnly]
    private static int xSleep(sqlite3_vfs* pVfs, int microseconds) => Default(pVfs)->xSleep(Default(pVfs), microseconds);

    [UnmanagedCallersOnly]
    private static int xCurrentTime(sqlite3_vfs* pVfs, double* pTime) => Default(pVfs)->xCurrentTime(Default(pVfs), pTime);

    [UnmanagedCallersOnly]
    private static int xGetLastError(sqlite3_vfs* pVfs, int nByte, char* zErrMsg) => Default(pVfs)->xGetLastError(Default(pVfs), nByte, zErrMsg);
}
