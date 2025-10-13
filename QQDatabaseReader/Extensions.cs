using System.Runtime.InteropServices;
public static unsafe partial class Extensions
{
    extension(SQLitePCL.raw)
    {
        [DllImport("e_sqlcipher", EntryPoint = "sqlite3_vfs_register")]
        public static extern int sqlite3_vfs_register(nint sqlite3_vfs, int makeDflt);

        [DllImport("e_sqlcipher", EntryPoint = "sqlite3_vfs_unregister")]
        public static extern int sqlite3_vfs_unregister(nint sqlite3_vfs);

        public static nint sqlite3_vfs_find(string? zVfsName) => sqlite3_vfs_find_internal(zVfsName);
    }


    [LibraryImport("e_sqlcipher", EntryPoint = "sqlite3_vfs_find")]
    private static partial nint sqlite3_vfs_find_internal([MarshalAs(UnmanagedType.LPUTF8Str)] string? zVfsName);

    extension(string str)
    {
        public static string? operator |(string? left, string? right)
        {
            if (string.IsNullOrEmpty(left))
                return right;
            return left;
        }
    }
}
