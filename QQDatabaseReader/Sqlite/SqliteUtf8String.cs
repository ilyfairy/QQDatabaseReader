using System.Runtime.InteropServices;
using SQLitePCL;

namespace QQDatabaseReader.Sqlite;

[StructLayout(LayoutKind.Sequential)]
public unsafe readonly struct SqliteUtf8String(byte* ptr)
{
    public readonly byte* ptr = ptr;

    public static implicit operator SqliteUtf8String(byte* p) => new(p);
    public utf8z ToUtf8z() => utf8z.FromPtr(ptr);
    public string ToUtf8String() => ToUtf8z().utf8_to_string();
}
