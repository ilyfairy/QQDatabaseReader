using System.Runtime.InteropServices;

namespace QQDatabaseReader.Sqlite;

/// <summary>
/// https://sqlite.org/c3ref/io_methods.html
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct sqlite3_io_methods
{
    public int iVersion;
    public delegate* unmanaged<sqlite3_file*, int> xClose; // int (*xClose)(sqlite3_file*);
    public delegate* unmanaged<sqlite3_file*, void*, int, long, int> xRead; // int (*xRead)(sqlite3_file*, void*, int iAmt, sqlite3_int64 iOfst);
    public delegate* unmanaged<sqlite3_file*, void*, int, long, int> xWrite; // int (*xWrite)(sqlite3_file*, const void*, int iAmt, sqlite3_int64 iOfst);
    public delegate* unmanaged<sqlite3_file*, long, int> xTruncate; // int (*xTruncate)(sqlite3_file*, sqlite3_int64 size);
    public delegate* unmanaged<sqlite3_file*, int, int> xSync; // int (*xSync)(sqlite3_file*, int flags);
    public delegate* unmanaged<sqlite3_file*, long*, int> xFileSize; // int (*xFileSize)(sqlite3_file*, sqlite3_int64 *pSize);
    public delegate* unmanaged<sqlite3_file*, int, int> xLock; // int (*xLock)(sqlite3_file*, int);
    public delegate* unmanaged<sqlite3_file*, int, int> xUnlock; // int (*xUnlock)(sqlite3_file*, int);
    public delegate* unmanaged<sqlite3_file*, int*, int> xCheckReservedLock; // int (*xCheckReservedLock)(sqlite3_file*, int *pResOut);
    public delegate* unmanaged<sqlite3_file*, int, void*, int> xFileControl; // int (*xFileControl)(sqlite3_file*, int op, void *pArg);
    public delegate* unmanaged<sqlite3_file*, int> xSectorSize; // int (*xSectorSize)(sqlite3_file*);
    public delegate* unmanaged<sqlite3_file*, int> xDeviceCharacteristics; // int (*xDeviceCharacteristics)(sqlite3_file*);
    /* Methods above are valid for version 1 */
    public delegate* unmanaged<sqlite3_file*, int, int, int, void**, int> xShmMap; // int (*xShmMap)(sqlite3_file*, int iPg, int pgsz, int, void volatile**);
    public delegate* unmanaged<sqlite3_file*, int, int, int, int> xShmLock; // int (*xShmLock)(sqlite3_file*, int offset, int n, int flags);
    public delegate* unmanaged<sqlite3_file*, void> xShmBarrier; // void (*xShmBarrier)(sqlite3_file*);
    public delegate* unmanaged<sqlite3_file*, int, int> xShmUnmap; // int (*xShmUnmap)(sqlite3_file*, int deleteFlag);
    /* Methods above are valid for version 2 */
    public delegate* unmanaged<sqlite3_file*, long, int, void**, int> xFetch; // int (*xFetch)(sqlite3_file*, sqlite3_int64 iOfst, int iAmt, void **pp);
    public delegate* unmanaged<sqlite3_file*, long, void*, int> xUnfetch; // int (*xUnfetch)(sqlite3_file*, sqlite3_int64 iOfst, void *p);
    /* Methods above are valid for version 3 */
}
