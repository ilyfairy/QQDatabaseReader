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

    public const int HeaderSize = 1024;
    public const int PageSize = 8192;

    private const uint Delta = 0x9E3779B9;
    private const int SQLITE_READONLY = 8;
    private const int SQLITE_IOERR = 10;
    private const int SQLITE_IOERR_SHORT_READ = SQLITE_IOERR | (2 << 8);

    private static readonly object RegistrationGate = new();
    private static readonly Dictionary<string, VfsRegistration> ActiveRegistrationsByKey = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, VfsRegistration> RegistrationsByName = new(StringComparer.Ordinal);
    private static readonly Dictionary<nint, VfsRegistration> RegistrationsByState = new();
    private static int _registrationId;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct PCQQSqlite3File
    {
        public sqlite3_file Base;
        public uint K0;
        public uint K1;
        public uint K2;
        public uint K3;
        public VfsRegistrationState* RegistrationState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VfsRegistrationState
    {
        public sqlite3_vfs* OriginalVfs;
        public uint K0;
        public uint K1;
        public uint K2;
        public uint K3;
    }

    private sealed class VfsRegistration(string keyId, string name, nint vfs, nint state, nint nameUtf8)
    {
        public string KeyId { get; } = keyId;
        public string Name { get; } = name;
        public nint Vfs { get; } = vfs;
        public nint State { get; } = state;
        public nint NameUtf8 { get; } = nameUtf8;
        public int ClientReferenceCount { get; set; } = 1;
        public int OpenFileCount { get; set; }
        public bool IsUnregistered { get; set; }
    }

    public static string Register(string key) => Register(PcqqDatabaseDecryptor.ParseKey(key));

    public static string Register(ReadOnlySpan<byte> key)
    {
        if (key.Length != 16)
            throw new ArgumentException("PCQQ VFS key must be exactly 16 bytes.", nameof(key));

        var keyId = Convert.ToHexString(key);
        lock (RegistrationGate)
        {
            if (ActiveRegistrationsByKey.TryGetValue(keyId, out var existing))
            {
                existing.ClientReferenceCount++;
                return existing.Name;
            }

            nint defaultVfsPtr = raw.sqlite3_vfs_find(null);
            if (defaultVfsPtr == IntPtr.Zero)
                throw new InvalidOperationException("Failed to find default VFS.");

            var name = VfsName + "-" + (++_registrationId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var nameUtf8 = Marshal.StringToCoTaskMemUTF8(name);
            var state = (VfsRegistrationState*)NativeMemory.AllocZeroed((nuint)sizeof(VfsRegistrationState));
            var vfs = (sqlite3_vfs*)NativeMemory.AllocZeroed((nuint)sizeof(sqlite3_vfs));
            var registered = false;
            try
            {
                var originalVfs = (sqlite3_vfs*)defaultVfsPtr;
                state->OriginalVfs = originalVfs;
                state->K0 = BinaryPrimitives.ReadUInt32LittleEndian(key[0..4]);
                state->K1 = BinaryPrimitives.ReadUInt32LittleEndian(key[4..8]);
                state->K2 = BinaryPrimitives.ReadUInt32LittleEndian(key[8..12]);
                state->K3 = BinaryPrimitives.ReadUInt32LittleEndian(key[12..16]);

                *vfs = new sqlite3_vfs
                {
                    iVersion = 1,
                    szOsFile = sizeof(PCQQSqlite3File) + originalVfs->szOsFile,
                    mxPathname = originalVfs->mxPathname,
                    zName = (byte*)nameUtf8,
                    pAppData = state,
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

                int rc = raw.sqlite3_vfs_register((nint)vfs, 0);
                if (rc != raw.SQLITE_OK)
                    throw new InvalidOperationException($"Failed to register {name}: sqlite rc={rc}.");
                registered = true;

                var registration = new VfsRegistration(keyId, name, (nint)vfs, (nint)state, nameUtf8);
                ActiveRegistrationsByKey.Add(keyId, registration);
                RegistrationsByName.Add(name, registration);
                RegistrationsByState.Add((nint)state, registration);
                return name;
            }
            catch
            {
                ActiveRegistrationsByKey.Remove(keyId);
                RegistrationsByName.Remove(name);
                RegistrationsByState.Remove((nint)state);
                if (registered)
                    raw.sqlite3_vfs_unregister((nint)vfs);
                NativeMemory.Free(vfs);
                NativeMemory.Free(state);
                Marshal.FreeCoTaskMem(nameUtf8);
                throw;
            }
        }
    }

    public static void Release(string? vfsName)
    {
        if (string.IsNullOrWhiteSpace(vfsName))
            return;

        lock (RegistrationGate)
        {
            if (!RegistrationsByName.TryGetValue(vfsName, out var registration) ||
                registration.ClientReferenceCount <= 0)
                return;

            registration.ClientReferenceCount--;
            if (registration.ClientReferenceCount > 0)
                return;

            var rc = raw.sqlite3_vfs_unregister(registration.Vfs);
            if (rc != raw.SQLITE_OK)
                return;

            registration.IsUnregistered = true;
            if (ActiveRegistrationsByKey.TryGetValue(registration.KeyId, out var active) &&
                ReferenceEquals(active, registration))
            {
                ActiveRegistrationsByKey.Remove(registration.KeyId);
            }

            TryFreeRegistration(registration);
        }
    }

    private static bool TryAddOpenFileReference(VfsRegistrationState* state)
    {
        lock (RegistrationGate)
        {
            if (!RegistrationsByState.TryGetValue((nint)state, out var registration))
                return false;

            registration.OpenFileCount++;
            return true;
        }
    }

    private static void ReleaseOpenFileReference(VfsRegistrationState* state)
    {
        lock (RegistrationGate)
        {
            if (!RegistrationsByState.TryGetValue((nint)state, out var registration))
                return;

            if (registration.OpenFileCount > 0)
                registration.OpenFileCount--;
            TryFreeRegistration(registration);
        }
    }

    private static void TryFreeRegistration(VfsRegistration registration)
    {
        if (!registration.IsUnregistered ||
            registration.ClientReferenceCount != 0 ||
            registration.OpenFileCount != 0)
        {
            return;
        }

        RegistrationsByName.Remove(registration.Name);
        RegistrationsByState.Remove(registration.State);
        if (ActiveRegistrationsByKey.TryGetValue(registration.KeyId, out var active) &&
            ReferenceEquals(active, registration))
        {
            ActiveRegistrationsByKey.Remove(registration.KeyId);
        }
        NativeMemory.Free((void*)registration.Vfs);
        NativeMemory.Free((void*)registration.State);
        Marshal.FreeCoTaskMem(registration.NameUtf8);
    }

    private static sqlite3_file* GetOrigFile(sqlite3_file* pFile)
    {
        return (sqlite3_file*)((byte*)pFile + sizeof(PCQQSqlite3File));
    }

    private static PCQQSqlite3File* GetPCQQFile(sqlite3_file* pFile)
    {
        return (PCQQSqlite3File*)pFile;
    }

    [UnmanagedCallersOnly]
    private static int xOpen(sqlite3_vfs* pVfs, SqliteUtf8String zName, sqlite3_file* pFile, int flags, int* pOutFlags)
    {
        var state = (VfsRegistrationState*)pVfs->pAppData;
        var openFileReferenceAdded = false;
        try
        {
            sqlite3_vfs* origVfs = state->OriginalVfs;
            bool isMainDb = (flags & raw.SQLITE_OPEN_MAIN_DB) != 0;
            bool isTempDb = (flags & (0x00000008 | 0x00000010)) != 0;

            if (!isMainDb || isTempDb)
                return origVfs->xOpen(origVfs, zName, pFile, flags, pOutFlags);

            if (!TryAddOpenFileReference(state))
                return SQLITE_IOERR;
            openFileReferenceAdded = true;

            sqlite3_file* origFile = GetOrigFile(pFile);
            new Span<byte>(origFile, origVfs->szOsFile).Clear();

            int rc = origVfs->xOpen(origVfs, zName, origFile, flags, pOutFlags);
            if (rc != raw.SQLITE_OK)
            {
                ReleaseOpenFileReference(state);
                openFileReferenceAdded = false;
                return rc;
            }

            var pcqqFile = GetPCQQFile(pFile);
            pcqqFile->K0 = state->K0;
            pcqqFile->K1 = state->K1;
            pcqqFile->K2 = state->K2;
            pcqqFile->K3 = state->K3;
            pcqqFile->RegistrationState = state;
            pFile->pMethods = IoMethodsPtr;
            openFileReferenceAdded = false;
            return raw.SQLITE_OK;
        }
        catch
        {
            if (openFileReferenceAdded)
                ReleaseOpenFileReference(state);
            return SQLITE_IOERR;
        }
    }

    [UnmanagedCallersOnly]
    private static int xClose(sqlite3_file* pFile)
    {
        var state = GetPCQQFile(pFile)->RegistrationState;
        sqlite3_file* origFile = GetOrigFile(pFile);
        try
        {
            return origFile->pMethods->xClose(origFile);
        }
        finally
        {
            ReleaseOpenFileReference(state);
        }
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

                    var pcqqFile = GetPCQQFile(pFile);
                    DecryptPageInPlace(
                        encryptedPage.AsSpan(0, PageSize),
                        words.AsSpan(0, PageSize / 4),
                        pcqqFile->K0,
                        pcqqFile->K1,
                        pcqqFile->K2,
                        pcqqFile->K3);
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
        sqlite3_vfs* origVfs = ((VfsRegistrationState*)pVfs->pAppData)->OriginalVfs;
        return origVfs->xDelete(origVfs, zName, syncDir);
    }

    [UnmanagedCallersOnly]
    private static int xAccess(sqlite3_vfs* pVfs, SqliteUtf8String zName, int flags, int* pResOut)
    {
        sqlite3_vfs* origVfs = ((VfsRegistrationState*)pVfs->pAppData)->OriginalVfs;
        return origVfs->xAccess(origVfs, zName, flags, pResOut);
    }

    [UnmanagedCallersOnly]
    private static int xFullPathname(sqlite3_vfs* pVfs, SqliteUtf8String zName, int nOut, byte* zOut)
    {
        sqlite3_vfs* origVfs = ((VfsRegistrationState*)pVfs->pAppData)->OriginalVfs;
        return origVfs->xFullPathname(origVfs, zName, nOut, zOut);
    }

    [UnmanagedCallersOnly]
    private static void* xDlOpen(sqlite3_vfs* pVfs, SqliteUtf8String zFilename)
    {
        sqlite3_vfs* origVfs = ((VfsRegistrationState*)pVfs->pAppData)->OriginalVfs;
        return origVfs->xDlOpen(origVfs, zFilename);
    }

    [UnmanagedCallersOnly]
    private static void xDlError(sqlite3_vfs* pVfs, int nByte, SqliteUtf8String zErrMsg)
    {
        sqlite3_vfs* origVfs = ((VfsRegistrationState*)pVfs->pAppData)->OriginalVfs;
        origVfs->xDlError(origVfs, nByte, zErrMsg);
    }

    [UnmanagedCallersOnly]
    private static delegate* unmanaged<void> xDlSym(sqlite3_vfs* pVfs, void* pHandle, SqliteUtf8String zSymbol)
    {
        sqlite3_vfs* origVfs = ((VfsRegistrationState*)pVfs->pAppData)->OriginalVfs;
        return origVfs->xDlSym(origVfs, pHandle, zSymbol);
    }

    [UnmanagedCallersOnly]
    private static void xDlClose(sqlite3_vfs* pVfs, void* pHandle)
    {
        sqlite3_vfs* origVfs = ((VfsRegistrationState*)pVfs->pAppData)->OriginalVfs;
        origVfs->xDlClose(origVfs, pHandle);
    }

    [UnmanagedCallersOnly]
    private static int xRandomness(sqlite3_vfs* pVfs, int nByte, char* zOut)
    {
        sqlite3_vfs* origVfs = ((VfsRegistrationState*)pVfs->pAppData)->OriginalVfs;
        return origVfs->xRandomness(origVfs, nByte, zOut);
    }

    [UnmanagedCallersOnly]
    private static int xSleep(sqlite3_vfs* pVfs, int microseconds)
    {
        sqlite3_vfs* origVfs = ((VfsRegistrationState*)pVfs->pAppData)->OriginalVfs;
        return origVfs->xSleep(origVfs, microseconds);
    }

    [UnmanagedCallersOnly]
    private static int xCurrentTime(sqlite3_vfs* pVfs, double* pTime)
    {
        sqlite3_vfs* origVfs = ((VfsRegistrationState*)pVfs->pAppData)->OriginalVfs;
        return origVfs->xCurrentTime(origVfs, pTime);
    }

    [UnmanagedCallersOnly]
    private static int xGetLastError(sqlite3_vfs* pVfs, int nByte, char* zErrMsg)
    {
        sqlite3_vfs* origVfs = ((VfsRegistrationState*)pVfs->pAppData)->OriginalVfs;
        return origVfs->xGetLastError(origVfs, nByte, zErrMsg);
    }

    private static void DecryptPageInPlace(
        Span<byte> page,
        Span<uint> words,
        uint k0,
        uint k1,
        uint k2,
        uint k3)
    {
        for (int i = 0; i < PageSize / 4; i++)
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(page[(i * 4)..]);

        Span<uint> key = stackalloc uint[4] { k0, k1, k2, k3 };
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
