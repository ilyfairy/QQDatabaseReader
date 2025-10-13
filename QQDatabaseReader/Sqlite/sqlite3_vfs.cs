using System.Runtime.InteropServices;

namespace QQDatabaseReader.Sqlite;

/// <summary>
/// https://sqlite.org/c3ref/vfs.html
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct sqlite3_vfs
{
    public int iVersion;
    public int szOsFile;
    public int mxPathname;
    public sqlite3_vfs* pNext;
    public SqliteUtf8String zName; // const char*
    public void* pAppData;
    public delegate* unmanaged<sqlite3_vfs*, SqliteUtf8String, sqlite3_file*, int, int*, int> xOpen; // int (*xOpen)(sqlite3_vfs*, sqlite3_filename zName, sqlite3_file*, int flags, int* pOutFlags);
    public delegate* unmanaged<sqlite3_vfs*, SqliteUtf8String, int, int> xDelete; // int (*xDelete)(sqlite3_vfs*, const char *zName, int syncDir);
    public delegate* unmanaged<sqlite3_vfs*, SqliteUtf8String, int, int*, int> xAccess; // int (*xAccess)(sqlite3_vfs*, const char *zName, int flags, int *pResOut);
    public delegate* unmanaged<sqlite3_vfs*, SqliteUtf8String, int, byte*, int> xFullPathname; // int (*xFullPathname)(sqlite3_vfs*, const char *zName, int nOut, char *zOut);
    public delegate* unmanaged<sqlite3_vfs*, SqliteUtf8String, void*> xDlOpen; // void *(*xDlOpen)(sqlite3_vfs*, const char *zFilename);
    public delegate* unmanaged<sqlite3_vfs*, int, SqliteUtf8String, void> xDlError; // void (*xDlError)(sqlite3_vfs*, int nByte, char *zErrMsg);
    public delegate* unmanaged<sqlite3_vfs*, void*, SqliteUtf8String, delegate* unmanaged<void>> xDlSym; // void (*(*xDlSym)(sqlite3_vfs*,void*, const char *zSymbol))(void);
    public delegate* unmanaged<sqlite3_vfs*, void*, void> xDlClose; // void (*xDlClose)(sqlite3_vfs*, void*);
    public delegate* unmanaged<sqlite3_vfs*, int, char*, int> xRandomness; // int (*xRandomness)(sqlite3_vfs*, int nByte, char *zOut);
    public delegate* unmanaged<sqlite3_vfs*, int, int> xSleep; // int (*xSleep)(sqlite3_vfs*, int microseconds);
    public delegate* unmanaged<sqlite3_vfs*, double*, int> xCurrentTime; // int (*xCurrentTime)(sqlite3_vfs*, double*);
    public delegate* unmanaged<sqlite3_vfs*, int, char*, int> xGetLastError; // int (*xGetLastError)(sqlite3_vfs*, int, char *);
    /*
    ** The methods above are in version 1 of the sqlite_vfs object
    ** definition.  Those that follow are added in version 2 or later
    */
    public delegate* unmanaged<sqlite3_vfs*, long*, int> xCurrentTimeInt64; // int (*xCurrentTimeInt64)(sqlite3_vfs*, sqlite3_int64*);
    /*
    ** The methods above are in versions 1 and 2 of the sqlite_vfs object.
    ** Those below are for version 3 and greater.
    */
    public delegate* unmanaged<sqlite3_vfs*, SqliteUtf8String, delegate* unmanaged<void>, int> xSetSystemCall; // int (*xSetSystemCall)(sqlite3_vfs*, const char *zName, sqlite3_syscall_ptr);
    public delegate* unmanaged<sqlite3_vfs*, SqliteUtf8String, delegate* unmanaged<void>> xGetSystemCall; // sqlite3_syscall_ptr (*xGetSystemCall)(sqlite3_vfs*, const char *zName);
    public delegate* unmanaged<sqlite3_vfs*, SqliteUtf8String, SqliteUtf8String> xNextSystemCall; // const char *(*xNextSystemCall)(sqlite3_vfs*, const char *zName);
    /*
    ** The methods above are in versions 1 through 3 of the sqlite_vfs object.
    ** New fields may be appended in future versions.  The iVersion
    ** value will increment whenever this happens.
    */
}
