using System.Runtime.InteropServices;

namespace QQDatabaseReader.Sqlite;


[StructLayout(LayoutKind.Sequential)]
public unsafe struct sqlite3_file(sqlite3_io_methods* pMethods)
{
    public sqlite3_io_methods* pMethods = pMethods;

    public static implicit operator sqlite3_file(sqlite3_io_methods* pMethods) => new(pMethods);
}


[StructLayout(LayoutKind.Sequential)]
public unsafe struct StreamSqlite3File
{
    public sqlite3_file Base;   // 基础文件结构
    public long Offset;          // 文件偏移量
    // 后面是原始VFS的文件结构（可变长度）
}
