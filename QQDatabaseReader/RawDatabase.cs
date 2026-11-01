using System.Text;
using SQLitePCL;
using System.Security.Cryptography;
using QQDatabaseReader.Archives;
using QQDatabaseReader.Sqlite;
using System.Linq;

namespace QQDatabaseReader;

public class RawDatabase : IDisposable
{
    public string DatabaseFilePath { get; }
    public bool UseVFS { get; }
    public bool UsePCQQVFS { get; }
    /// <summary>
    /// 以 immutable 方式只读打开 (mode=ro&amp;immutable=1, 要在 <see cref="Initialize"/> 之前设置): 不加锁, 不打开也不创建 -wal / -shm.
    /// WAL 模式的库在只读目录里打开时要新建 -wal, 没有现成的 -wal 就会报 SQLITE_CANTOPEN; 这种情况用它.
    /// 库旁边 -wal 里还没写回主库的内容会被忽略, 所以只用于没有 -wal 的静止的库 (例如 QQ 正常退出后复制的备份)
    /// </summary>
    public bool UseImmutableReadOnly { get; set; }
    public byte[]? PCQQKey { get; }
    public string? CipherPassword { get; }
    public int? CipherPageSize { get; }
    public int? CipherKdfIter { get; }
    public HashAlgorithmName? CipherKdfAlgorithm { get; }
    public HashAlgorithmName? CipherHmacAlgorithm { get; }

    /// <summary>数据库在 tar / zip 归档里 (路径形如 data.tar\databases\...\nt_msg.db), 通过 ArchiveFileVfs 只读访问</summary>
    public bool IsInArchive => !UsePCQQVFS && ArchiveFile.IsArchivePath(DatabaseFilePath);

    /// <summary>
    /// 用独占锁模式打开, 不加文件锁 (要在 <see cref="Initialize"/> 之前设置): WAL 索引放在进程内存里, 不在库旁边创建 -shm,
    /// 适合只读挂载的备份目录和网络共享. 不加锁, 所以库不能同时被 QQ 写入. 只支持 QQNT 库 (UseVFS); 归档内的库总是这样打开
    /// </summary>
    public bool ExclusiveLocking { get; set; }

    private sqlite3? _db;
    private string? _pcqqVfsName;

    public sqlite3 Database => _db!;

    public IntPtr NativeHandle => _db?.DangerousGetHandle() ?? IntPtr.Zero;

    static RawDatabase()
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());
        QQNTFileOffsetVfs.Register();
        ArchiveFileVfs.Register();
    }

    public RawDatabase(string dbFilePath, bool useVFS = false, bool useImmutableReadOnly = false)
    {
        if (!ArchiveFile.Exists(dbFilePath))
        {
            throw new Exception($"Error: Database file not found: {DatabaseFilePath}");
        }

        DatabaseFilePath = dbFilePath;
        UseVFS = useVFS;
        UseImmutableReadOnly = useImmutableReadOnly;
    }

    private RawDatabase(string databaseFilePath, byte[] pcqqKey)
    {
        if (!File.Exists(databaseFilePath))
        {
            throw new Exception($"Error: Database file not found: {databaseFilePath}");
        }

        if (pcqqKey.Length != 16)
        {
            throw new ArgumentException("PCQQ database key must be exactly 16 bytes.", nameof(pcqqKey));
        }

        DatabaseFilePath = databaseFilePath;
        UsePCQQVFS = true;
        PCQQKey = pcqqKey.ToArray();
    }

    public static RawDatabase OpenPCQQ(string databaseFilePath, string key) =>
        new(databaseFilePath, PcqqDatabaseDecryptor.ParseKey(key));

    public RawDatabase(string databaseFilePath, string cipherPassword, HashAlgorithmName? cipherKdfAlgorithm = null, HashAlgorithmName? cipherHmacAlgorithm = null, int cipherPageSize = 4096, int cipherKdfIter = 4000, bool useVFS = true)
    {
        if (!ArchiveFile.Exists(databaseFilePath))
        {
            throw new Exception($"Error: Database file not found: {DatabaseFilePath}");
        }

        DatabaseFilePath = databaseFilePath;
        CipherPassword = cipherPassword;
        CipherPageSize = cipherPageSize;
        CipherKdfIter = cipherKdfIter;
        UseVFS = useVFS;
        CipherKdfAlgorithm = cipherKdfAlgorithm ?? HashAlgorithmName.SHA512;
        CipherHmacAlgorithm = cipherHmacAlgorithm ?? HashAlgorithmName.SHA1;
    }

    public void Initialize()
    {
        if (_db is not null)
            throw new InvalidOperationException("Database is already initialized.");

        string? vfsName = null;
        sqlite3? openedDatabase = null;
        try
        {
            if (UsePCQQVFS)
            {
                _pcqqVfsName = PCQQPageDecryptVfs.Register(PCQQKey!);
                vfsName = _pcqqVfsName;
            }
            else if (IsInArchive)
            {
                // 归档内的库: 自动识别 QQNT 头, 不需要 QQNTFileOffsetVfs; 只有原样存放的文件能随机读
                if (ArchiveFile.TryGetEntry(DatabaseFilePath, out _, out var entry) && entry.Storage != ArchiveEntryStorage.Stored)
                    throw new NotSupportedException($"归档内的数据库是压缩存储的, 不能直接读取, 请用仅存储 (不压缩) 方式打包: {DatabaseFilePath}");
                vfsName = ArchiveFileVfs.VfsName;
            }
            else if (UseVFS)
            {
                // 独占模式要拿主库的排他锁, 只读打开的文件拿不到 (SQLITE_IOERR_LOCK), 所以用不加锁的版本
                vfsName = ExclusiveLocking ? QQNTFileOffsetVfs.NoLockVfsName : QQNTFileOffsetVfs.VfsName;
            }
            else if (ExclusiveLocking)
            {
                throw new NotSupportedException("ExclusiveLocking 只支持带 QQNT 文件头的库 (UseVFS)");
            }

            var openFilePath = UseImmutableReadOnly && !IsInArchive
                ? CreateImmutableReadOnlyUri(DatabaseFilePath)
                : DatabaseFilePath;
            var openFlags = raw.SQLITE_OPEN_READONLY |
                            (UseImmutableReadOnly && !IsInArchive ? raw.SQLITE_OPEN_URI : 0);
            int rc = raw.sqlite3_open_v2(openFilePath, out openedDatabase, openFlags, vfsName);
            if (rc != raw.SQLITE_OK)
            {
                var errorMsg = raw.sqlite3_errmsg(openedDatabase).utf8_to_string();
                throw new Exception($"Failed to open database: {errorMsg}");
            }

            _db = openedDatabase;
            // 归档里不能建 -shm; 独占模式下 SQLite 把 WAL 索引放在进程内存里, 要在第一次读库之前设置
            if (IsInArchive || ExclusiveLocking)
                ExecuteSqlOrThrow(Database, "PRAGMA locking_mode = EXCLUSIVE;", "设置独占锁模式失败");

            // 如果启用了解密，使用 PRAGMA key 方法（SQLCipher）
            if (!string.IsNullOrEmpty(CipherPassword))
            {
                var escapedPassword = CipherPassword.Replace("'", "''", StringComparison.Ordinal);
                ExecuteSqlOrThrow(Database, $"PRAGMA key = '{escapedPassword}';", "设置 SQLCipher 密钥失败");
                ExecuteSqlOrThrow(Database, $"PRAGMA cipher_page_size = {CipherPageSize};", "设置 SQLCipher page size 失败");
                ExecuteSqlOrThrow(Database, $"PRAGMA kdf_iter = {CipherKdfIter};", "设置 SQLCipher KDF 轮数失败");
                ExecuteSqlOrThrow(Database, $"PRAGMA cipher_hmac_algorithm = HMAC_{CipherHmacAlgorithm!.Value.Name};", "设置 SQLCipher HMAC 算法失败");
                ExecuteSqlOrThrow(Database, $"PRAGMA cipher_kdf_algorithm = PBKDF2_HMAC_{CipherKdfAlgorithm!.Value.Name};", "设置 SQLCipher KDF 算法失败");
                VerifyDecrypted();
            }
        }
        catch
        {
            _db = null;
            openedDatabase?.Dispose();
            PCQQPageDecryptVfs.Release(_pcqqVfsName);
            _pcqqVfsName = null;
            throw;
        }
    }


    public static string GetQQPathHash(string qquid)
    {
        var tmp = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(qquid)));
        var result = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(tmp + "nt_kernel")));
        return result;
    }

    public static string GetQQKey(string ntuid, string rand)
    {
        var hash = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(ntuid)));
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(hash + rand)));
    }

    /// <summary>
    /// 读数据库文件开头 1024 字节 (QQNT 头). 文件不存在, 不足 1024 字节, 或在归档里但不是原样存放 (读它要整个解压) 时返回 false
    /// </summary>
    private static bool TryReadQQNtHeader(string databaseFilePath, Span<byte> buffer)
    {
        try
        {
            if (ArchiveFile.TryGetEntry(databaseFilePath, out _, out var entry) && entry.Storage != ArchiveEntryStorage.Stored)
                return false;
            if (!ArchiveFile.Exists(databaseFilePath))
                return false;

            using var fs = ArchiveFile.OpenRead(databaseFilePath);
            return fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return false;
        }
    }

    public static string? GetRand(string databaseFilePath)
    {
        var header = "SQLite header 3"u8;
        Span<byte> buffer = stackalloc byte[1024];
        if (!TryReadQQNtHeader(databaseFilePath, buffer))
            return null;
        if (!buffer[..header.Length].SequenceEqual(header))
            return null;

        var qqntdbHeader = "QQ_NT DB"u8;
        var qqntIndex = buffer.IndexOf(qqntdbHeader);
        if (qqntIndex < 0)
            return null;
        var headerPayload = buffer[(qqntIndex + qqntdbHeader.Length)..];
        return TryReadQQNTHeaderRand(headerPayload) ?? ScanRandToken(headerPayload);
    }

    /// <summary>
    /// 从 QQNT 数据库头部检测 SQLCipher 的 HMAC 算法. 新版 QQNT 在头部写明 (如 HMAC_SHA1);
    /// 旧版 PC QQNT 头部没有这一项, 用的是 SQLCipher 默认的 HMAC_SHA512.
    /// Android 头部没有这一项时按 HMAC_SHA1 处理. 文件不存在或不是 QQNT 数据库时返回 null
    /// </summary>
    public static HashAlgorithmName? DetectQQNtHmacAlgorithm(string databaseFilePath, bool android = false)
    {
        Span<byte> buffer = stackalloc byte[1024];
        if (!TryReadQQNtHeader(databaseFilePath, buffer))
            return null;

        if (!buffer.StartsWith("SQLite header 3"u8) || buffer.IndexOf("QQ_NT DB"u8) < 0)
            return null;

        // HMAC_SHA1 是 HMAC_SHA1xx 的前缀, 先匹配长的
        if (buffer.IndexOf("HMAC_SHA512"u8) >= 0)
            return HashAlgorithmName.SHA512;
        if (buffer.IndexOf("HMAC_SHA256"u8) >= 0)
            return HashAlgorithmName.SHA256;
        if (buffer.IndexOf("HMAC_SHA1"u8) >= 0)
            return HashAlgorithmName.SHA1;
        return android ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA512;
    }

    private static string CreateImmutableReadOnlyUri(string databaseFilePath)
    {
        var uri = new Uri(Path.GetFullPath(databaseFilePath)).AbsoluteUri;
        return uri + (uri.Contains('?') ? "&" : "?") + "mode=ro&immutable=1";
    }

    private static string? TryReadQQNTHeaderRand(ReadOnlySpan<byte> headerPayload)
    {
        if (headerPayload.Length >= 4)
        {
            var messageLength =
                headerPayload[0] |
                headerPayload[1] << 8 |
                headerPayload[2] << 16 |
                headerPayload[3] << 24;

            if (messageLength > 0 && messageLength <= headerPayload.Length - 4)
            {
                var value = TryReadLengthDelimitedField(headerPayload.Slice(4, messageLength), 2);
                if (value is not null)
                    return value;
            }
        }

        var scanLength = Math.Min(headerPayload.Length, 128);
        for (var offset = 0; offset < scanLength; offset++)
        {
            var index = offset;
            if (!TryReadVarint(headerPayload[..scanLength], ref index, out var tag) ||
                tag != ((2ul << 3) | 2))
            {
                continue;
            }

            if (!TryReadVarint(headerPayload[..scanLength], ref index, out var length) ||
                length > (ulong)(scanLength - index))
            {
                continue;
            }

            var value = DecodeRand(headerPayload.Slice(index, (int)length));
            if (value is not null)
                return value;
        }

        return null;
    }

    private static string? TryReadLengthDelimitedField(ReadOnlySpan<byte> payload, int fieldNumber)
    {
        var index = 0;
        while (index < payload.Length)
        {
            if (!TryReadVarint(payload, ref index, out var tag))
                return null;

            var currentFieldNumber = (int)(tag >> 3);
            var wireType = (int)(tag & 7);
            switch (wireType)
            {
                case 0:
                    if (!TryReadVarint(payload, ref index, out _))
                        return null;
                    break;
                case 1:
                    index += 8;
                    break;
                case 2:
                    if (!TryReadVarint(payload, ref index, out var length) ||
                        length > (ulong)(payload.Length - index))
                    {
                        return null;
                    }

                    var value = payload.Slice(index, (int)length);
                    if (currentFieldNumber == fieldNumber)
                        return DecodeRand(value);

                    index += (int)length;
                    break;
                case 5:
                    index += 4;
                    break;
                default:
                    return null;
            }

            if (index > payload.Length)
                return null;
        }

        return null;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int index, out ulong value)
    {
        value = 0;
        var shift = 0;

        while (index < data.Length && shift < 64)
        {
            var item = data[index++];
            value |= (ulong)(item & 0x7F) << shift;
            if ((item & 0x80) == 0)
                return true;

            shift += 7;
        }

        return false;
    }

    private static string? ScanRandToken(ReadOnlySpan<byte> headerPayload)
    {
        StringBuilder builder = new(8);
        foreach (var item in headerPayload)
        {
            var isChar = IsRandByte(item);
            if (builder.Length > 0 && !isChar)
                break;

            if (isChar)
                builder.Append((char)item);
        }

        return builder.Length > 0 ? builder.ToString() : null;
    }

    private static string? DecodeRand(ReadOnlySpan<byte> value)
    {
        if (value.Length < 4 || value.Length > 64)
            return null;

        foreach (var item in value)
        {
            if (!IsRandByte(item))
                return null;
        }

        return Encoding.UTF8.GetString(value);
    }

    private static bool IsRandByte(byte value)
    {
        return value >= (byte)'0' && value <= (byte)'9' ||
               value >= (byte)'A' && value <= (byte)'Z' ||
               value >= (byte)'a' && value <= (byte)'z' ||
               value == (byte)'_' ||
               value == (byte)'-';
    }


    private static string QuoteIdent(string name)
    {
        return "\"" + name.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// 校验时报数据库损坏, 已开启 writable_schema 宽松打开. 最常见的是 QQ 运行中复制的备份末尾少了几页 (库头页数比文件多),
    /// 此时按文件实际大小读, 缺的页里的数据读不到. 也可能是其它损坏, 部分表或数据读不到
    /// </summary>
    public bool OpenedInRecoveryMode { get; private set; }

    private void VerifyDecrypted()
    {
        const string sql = "SELECT count(*) FROM sqlite_master;";
        var rc = raw.sqlite3_exec(Database, sql, null, IntPtr.Zero, out _);
        if (rc == raw.SQLITE_CORRUPT)
        {
            // 页数检查失败时 SQLite 报 malformed; writable_schema 下改按文件大小计页. 连接是只读的, 不会写入.
            // 这个开关打开后要一直开着: 每个读事务都会重新检查页数
            ExecuteSqlOrThrow(Database, "PRAGMA writable_schema = ON;", "设置 writable_schema 失败");
            rc = raw.sqlite3_exec(Database, sql, null, IntPtr.Zero, out _);
            if (rc == raw.SQLITE_OK)
            {
                OpenedInRecoveryMode = true;
                return;
            }

            ExecuteSqlOrThrow(Database, "PRAGMA writable_schema = OFF;", "恢复 writable_schema 失败");
        }

        if (rc != raw.SQLITE_OK)
            throw new InvalidOperationException($"解密失败: {raw.sqlite3_errmsg(Database).utf8_to_string()} (code {rc})");
    }

    private static void ExecuteSqlOrThrow(sqlite3 database, string sql, string context)
    {
        var rc = raw.sqlite3_exec(database, sql, null, IntPtr.Zero, out _);
        if (rc == raw.SQLITE_OK)
            return;

        var error = raw.sqlite3_errmsg(database).utf8_to_string();
        throw new InvalidOperationException($"{context}: {error} (code {rc})");
    }

    public string DiagnoseDatabase()
    {
        // 快速检查
        string sql = "PRAGMA quick_check;";
        raw.sqlite3_prepare_v2(Database, sql, out var stmt);

        while (raw.sqlite3_step(stmt) == raw.SQLITE_ROW)
        {
            var result = raw.sqlite3_column_text(stmt, 0).utf8_to_string();
            return result;
        }
        raw.sqlite3_finalize(stmt);
        return "bad";
    }

    /// <summary>
    /// 检查表是否包含指定列
    /// </summary>
    private bool HasColumn(string tableName, string columnName)
    {
        string sql = $"PRAGMA table_info('{tableName}');";
        raw.sqlite3_prepare_v2(Database, sql, out var stmt);

        while (raw.sqlite3_step(stmt) == raw.SQLITE_ROW)
        {
            var colName = raw.sqlite3_column_text(stmt, 1).utf8_to_string();
            if (string.Equals(colName, columnName, StringComparison.OrdinalIgnoreCase))
            {
                raw.sqlite3_finalize(stmt);
                return true;
            }
        }
        raw.sqlite3_finalize(stmt);
        return false;
    }

    /// <summary>
    /// 检查表是否在指定列上有索引
    /// </summary>
    private bool HasIndexOnColumn(string tableName, string columnName)
    {
        string sql = $"PRAGMA index_list('{tableName}');";
        raw.sqlite3_prepare_v2(Database, sql, out var stmt);

        var indexNames = new List<string>();
        while (raw.sqlite3_step(stmt) == raw.SQLITE_ROW)
        {
            var indexName = raw.sqlite3_column_text(stmt, 1).utf8_to_string();
            indexNames.Add(indexName);
        }
        raw.sqlite3_finalize(stmt);

        foreach (var indexName in indexNames)
        {
            string indexInfoSQL = $"PRAGMA index_info('{indexName}');";
            raw.sqlite3_prepare_v2(Database, indexInfoSQL, out var indexInfoStmt);

            while (raw.sqlite3_step(indexInfoStmt) == raw.SQLITE_ROW)
            {
                var colName = raw.sqlite3_column_text(indexInfoStmt, 2).utf8_to_string();
                if (string.Equals(colName, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    raw.sqlite3_finalize(indexInfoStmt);
                    return true;
                }
            }
            raw.sqlite3_finalize(indexInfoStmt);
        }

        return false;
    }

    /// <summary>
    /// 检查表是否在指定列组合上有索引
    /// </summary>
    private bool HasIndexOnColumns(string tableName, string[] columnNames)
    {
        string sql = $"PRAGMA index_list('{tableName}');";
        raw.sqlite3_prepare_v2(Database, sql, out var stmt);

        var indexNames = new List<string>();
        while (raw.sqlite3_step(stmt) == raw.SQLITE_ROW)
        {
            var indexName = raw.sqlite3_column_text(stmt, 1).utf8_to_string();
            indexNames.Add(indexName);
        }
        raw.sqlite3_finalize(stmt);

        foreach (var indexName in indexNames)
        {
            string indexInfoSQL = $"PRAGMA index_info('{indexName}');";
            raw.sqlite3_prepare_v2(Database, indexInfoSQL, out var indexInfoStmt);

            var indexColumns = new List<string>();
            while (raw.sqlite3_step(indexInfoStmt) == raw.SQLITE_ROW)
            {
                var colName = raw.sqlite3_column_text(indexInfoStmt, 2).utf8_to_string();
                indexColumns.Add(colName);
            }
            raw.sqlite3_finalize(indexInfoStmt);

            // 检查是否包含所有指定的列（顺序必须匹配或者是前缀）
            if (indexColumns.Count >= columnNames.Length)
            {
                bool matches = true;
                for (int i = 0; i < columnNames.Length; i++)
                {
                    if (!string.Equals(indexColumns[i], columnNames[i], StringComparison.OrdinalIgnoreCase))
                    {
                        matches = false;
                        break;
                    }
                }
                if (matches) return true;
            }
        }

        return false;
    }



    /// <summary>
    /// 将所有用户表（排除 sqlite_*）从当前连接导出到新的未加密数据库。
    /// 对每张表优先使用 rowid 驱动复制；无法访问隐藏 rowid 时按表自身顺序完整复制。
    /// 任意读取、写入、建表、索引或触发器错误都会终止导出，临时数据库不会替换目标文件。
    /// 可选进度：reportTotalRows 回调总行数；progress 每成功插入一行时 Report 当前累计行数。
    /// </summary>
    public void ExportToNewDatabase(string destPath, int batchSize = 2048, int commitEvery = 20000, Action<long>? reportTotalRows = null, IProgress<long>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(destPath))
            throw new ArgumentException("destPath 不能为空");
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "batchSize 必须大于 0");
        if (commitEvery <= 0)
            throw new ArgumentOutOfRangeException(nameof(commitEvery), "commitEvery 必须大于 0");

        var destinationPath = Path.GetFullPath(destPath);
        var sourcePath = Path.GetFullPath(DatabaseFilePath);
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(destinationPath, sourcePath, pathComparison))
            throw new ArgumentException("目标路径不能与源数据库相同", nameof(destPath));

        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("无法确定导出目录");
        Directory.CreateDirectory(destinationDirectory);
        var tempPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        sqlite3? newDb = null;

        try
        {
            int rc = raw.sqlite3_open_v2(
                tempPath,
                out newDb,
                raw.SQLITE_OPEN_READWRITE | raw.SQLITE_OPEN_CREATE,
                null);
            if (rc != raw.SQLITE_OK)
            {
                var msg = raw.sqlite3_errmsg(newDb).utf8_to_string();
                throw new InvalidOperationException($"无法创建目标数据库: {msg} (code {rc})");
            }

            ExportToOpenedDatabase(newDb, batchSize, commitEvery, reportTotalRows, progress);
            VerifyDatabaseIntegrity(newDb);

            rc = raw.sqlite3_close_v2(newDb);
            if (rc != raw.SQLITE_OK)
                throw new InvalidOperationException($"关闭导出数据库失败 (code {rc})");
            newDb = null;

            if (File.Exists(destinationPath))
                File.Replace(tempPath, destinationPath, null, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, destinationPath);
        }
        catch
        {
            if (newDb is not null)
            {
                raw.sqlite3_close_v2(newDb);
            }

            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }

            throw;
        }
    }

    private void ExportToOpenedDatabase(
        sqlite3 newDb,
        int batchSize,
        int commitEvery,
        Action<long>? reportTotalRows,
        IProgress<long>? progress)
    {
        // 获取所有用户表及其 CREATE TABLE 语句
        List<(string name, string sql)> tables = new();
        var rc = raw.sqlite3_prepare_v2(_db, "SELECT name, sql FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND sql NOT NULL ORDER BY name", out var tblStmt);
        if (rc != raw.SQLITE_OK)
            throw new InvalidOperationException($"读取源数据库表结构失败: {raw.sqlite3_errmsg(_db).utf8_to_string()} (code {rc})");
        try
        {
            while ((rc = raw.sqlite3_step(tblStmt)) == raw.SQLITE_ROW)
            {
                string name = raw.sqlite3_column_text(tblStmt, 0).utf8_to_string();
                string sql = raw.sqlite3_column_text(tblStmt, 1).utf8_to_string();
                tables.Add((name, sql));
            }

            if (rc != raw.SQLITE_DONE)
                throw new InvalidOperationException($"读取源数据库表结构失败: {raw.sqlite3_errmsg(_db).utf8_to_string()} (code {rc})");
        }
        finally
        {
            raw.sqlite3_finalize(tblStmt);
        }

        var views = ReadViewDefinitions(_db!);

        // 分离普通表和FTS表
        var ftsTables = tables.Where(t => IsFts5VirtualTable(t.sql)).ToList();
        var ftsTableNames = ftsTables.Select(static table => table.name).ToArray();
        var normalTables = tables
            .Where(t => !IsFts5VirtualTable(t.sql) && !IsFtsShadowTable(t.name, ftsTableNames))
            .ToList();
        var exportTables = normalTables.Concat(ftsTables).ToArray();

        // 先创建普通表
        foreach (var (name, sql) in normalTables)
        {
            ExecuteSqlOrThrow(newDb, sql, $"创建普通表 {name} 失败");
        }

        // 后创建FTS表（处理tokenizer兼容性）
        foreach (var (name, sql) in ftsTables)
        {
            // 处理不支持的FTS5 tokenizer
            string createSql = RewriteUnsupportedFtsTokenizer(sql, name);
            ExecuteSqlOrThrow(newDb, createSql, $"创建 FTS 表 {name} 失败");
        }

        // 计算总行数（进度总量）
        long totalRows = 0;
        foreach (var (name, _) in exportTables)
        {
            rc = raw.sqlite3_prepare_v2(_db, $"SELECT COUNT(*) FROM {QuoteIdent(name)}", out var cntStmt);
            if (rc != raw.SQLITE_OK)
                throw new InvalidOperationException($"统计表 {name} 失败: {raw.sqlite3_errmsg(_db).utf8_to_string()} (code {rc})");
            try
            {
                rc = raw.sqlite3_step(cntStmt);
                if (rc != raw.SQLITE_ROW)
                    throw new InvalidOperationException($"统计表 {name} 失败: {raw.sqlite3_errmsg(_db).utf8_to_string()} (code {rc})");
                totalRows += raw.sqlite3_column_int64(cntStmt, 0);
            }
            finally
            {
                raw.sqlite3_finalize(cntStmt);
            }
        }
        reportTotalRows?.Invoke(totalRows);

        // 复制前性能优化参数（目标库）
        ExecuteSqlOrThrow(newDb, "PRAGMA synchronous=OFF", "设置目标数据库 synchronous 失败");
        ExecuteSqlOrThrow(newDb, "PRAGMA journal_mode=OFF", "设置目标数据库 journal_mode 失败");
        ExecuteSqlOrThrow(newDb, "PRAGMA temp_store=MEMORY", "设置目标数据库 temp_store 失败");
        ExecuteSqlOrThrow(newDb, "PRAGMA foreign_keys=OFF", "设置目标数据库 foreign_keys 失败");

        long copiedTotal = 0;
        // 每张表独立事务复制，避免单表失败导致全部数据回滚
        foreach (var (table, _) in exportTables)
        {
            // 开启表级事务
            ExecuteSqlOrThrow(newDb, "BEGIN IMMEDIATE", $"开始复制表 {table} 的事务失败");
            try
            {
                CopySingleTableData(_db!, newDb, table, batchSize, commitEvery, ref copiedTotal, progress);
                progress?.Report(copiedTotal);
                ExecuteSqlOrThrow(newDb, "COMMIT", $"提交表 {table} 失败");
            }
            catch (Exception ex)
            {
                raw.sqlite3_exec(newDb, "ROLLBACK", null, IntPtr.Zero, out _);
                throw new InvalidOperationException($"复制表 {table} 失败", ex);
            }
        }

        CopySqliteSequence(_db!, newDb);

        foreach (var (name, sql) in views)
            ExecuteSqlOrThrow(newDb, sql, $"重建视图 {name} 失败");

        // 重建所有索引/触发器
        rc = raw.sqlite3_prepare_v2(_db, "SELECT name, sql FROM sqlite_master WHERE (type='index' OR type='trigger') AND name NOT LIKE 'sqlite_%' AND sql NOT NULL ORDER BY name", out var itStmt);
        if (rc != raw.SQLITE_OK)
            throw new InvalidOperationException($"读取索引/触发器结构失败: {raw.sqlite3_errmsg(_db).utf8_to_string()} (code {rc})");
        try
        {
            while ((rc = raw.sqlite3_step(itStmt)) == raw.SQLITE_ROW)
            {
                var name = raw.sqlite3_column_text(itStmt, 0).utf8_to_string();
                var ddl = raw.sqlite3_column_text(itStmt, 1).utf8_to_string();
                ExecuteSqlOrThrow(newDb, ddl, $"重建索引/触发器 {name} 失败");
            }

            if (rc != raw.SQLITE_DONE)
                throw new InvalidOperationException($"读取索引/触发器结构失败: {raw.sqlite3_errmsg(_db).utf8_to_string()} (code {rc})");
        }
        finally
        {
            raw.sqlite3_finalize(itStmt);
        }
    }

    private static List<(string name, string sql)> ReadViewDefinitions(sqlite3 database)
    {
        var views = new List<(string name, string sql)>();
        var rc = raw.sqlite3_prepare_v2(
            database,
            "SELECT name, sql FROM sqlite_master WHERE type='view' AND name NOT LIKE 'sqlite_%' AND sql NOT NULL ORDER BY name",
            out var statement);
        if (rc != raw.SQLITE_OK)
            throw new InvalidOperationException($"读取视图结构失败: {raw.sqlite3_errmsg(database).utf8_to_string()} (code {rc})");

        try
        {
            while ((rc = raw.sqlite3_step(statement)) == raw.SQLITE_ROW)
            {
                views.Add((
                    raw.sqlite3_column_text(statement, 0).utf8_to_string(),
                    raw.sqlite3_column_text(statement, 1).utf8_to_string()));
            }

            if (rc != raw.SQLITE_DONE)
                throw new InvalidOperationException($"读取视图结构失败: {raw.sqlite3_errmsg(database).utf8_to_string()} (code {rc})");
        }
        finally
        {
            raw.sqlite3_finalize(statement);
        }

        return views;
    }

    private static void CopySqliteSequence(sqlite3 sourceDatabase, sqlite3 destinationDatabase)
    {
        if (!HasSchemaTable(sourceDatabase, "sqlite_sequence"))
            return;

        var sequences = new List<(string name, long? sequence)>();
        var rc = raw.sqlite3_prepare_v2(
            sourceDatabase,
            "SELECT name, seq FROM sqlite_sequence ORDER BY name",
            out var selectStatement);
        if (rc != raw.SQLITE_OK)
            throw new InvalidOperationException($"读取 AUTOINCREMENT 序列失败: {raw.sqlite3_errmsg(sourceDatabase).utf8_to_string()} (code {rc})");

        try
        {
            while ((rc = raw.sqlite3_step(selectStatement)) == raw.SQLITE_ROW)
            {
                var sequence = raw.sqlite3_column_type(selectStatement, 1) == raw.SQLITE_NULL
                    ? (long?)null
                    : raw.sqlite3_column_int64(selectStatement, 1);
                sequences.Add((raw.sqlite3_column_text(selectStatement, 0).utf8_to_string(), sequence));
            }

            if (rc != raw.SQLITE_DONE)
                throw new InvalidOperationException($"读取 AUTOINCREMENT 序列失败: {raw.sqlite3_errmsg(sourceDatabase).utf8_to_string()} (code {rc})");
        }
        finally
        {
            raw.sqlite3_finalize(selectStatement);
        }

        if (sequences.Count == 0)
            return;
        if (!HasSchemaTable(destinationDatabase, "sqlite_sequence"))
            throw new InvalidOperationException("目标数据库缺少 sqlite_sequence，无法恢复 AUTOINCREMENT 序列");

        ExecuteSqlOrThrow(destinationDatabase, "BEGIN IMMEDIATE", "开始恢复 AUTOINCREMENT 序列失败");
        sqlite3_stmt? insertStatement = null;
        try
        {
            ExecuteSqlOrThrow(destinationDatabase, "DELETE FROM sqlite_sequence", "清理目标 AUTOINCREMENT 序列失败");
            rc = raw.sqlite3_prepare_v2(
                destinationDatabase,
                "INSERT INTO sqlite_sequence(name, seq) VALUES(?1, ?2)",
                out insertStatement);
            if (rc != raw.SQLITE_OK)
                throw new InvalidOperationException($"准备恢复 AUTOINCREMENT 序列失败: {raw.sqlite3_errmsg(destinationDatabase).utf8_to_string()} (code {rc})");

            foreach (var (name, sequence) in sequences)
            {
                raw.sqlite3_reset(insertStatement);
                raw.sqlite3_clear_bindings(insertStatement);
                raw.sqlite3_bind_text(insertStatement, 1, name);
                if (sequence is { } value)
                    raw.sqlite3_bind_int64(insertStatement, 2, value);
                else
                    raw.sqlite3_bind_null(insertStatement, 2);

                rc = raw.sqlite3_step(insertStatement);
                if (rc != raw.SQLITE_DONE)
                    throw new InvalidOperationException($"恢复 AUTOINCREMENT 序列 {name} 失败: {raw.sqlite3_errmsg(destinationDatabase).utf8_to_string()} (code {rc})");
            }

            raw.sqlite3_finalize(insertStatement);
            insertStatement = null;
            ExecuteSqlOrThrow(destinationDatabase, "COMMIT", "提交 AUTOINCREMENT 序列失败");
        }
        catch
        {
            if (insertStatement is not null)
                raw.sqlite3_finalize(insertStatement);
            raw.sqlite3_exec(destinationDatabase, "ROLLBACK", null, IntPtr.Zero, out _);
            throw;
        }
    }

    private static bool HasSchemaTable(sqlite3 database, string tableName)
    {
        var rc = raw.sqlite3_prepare_v2(
            database,
            "SELECT 1 FROM sqlite_master WHERE type='table' AND name=?1 LIMIT 1",
            out var statement);
        if (rc != raw.SQLITE_OK)
            throw new InvalidOperationException($"检查表 {tableName} 失败: {raw.sqlite3_errmsg(database).utf8_to_string()} (code {rc})");

        try
        {
            raw.sqlite3_bind_text(statement, 1, tableName);
            rc = raw.sqlite3_step(statement);
            if (rc == raw.SQLITE_ROW)
                return true;
            if (rc == raw.SQLITE_DONE)
                return false;
            throw new InvalidOperationException($"检查表 {tableName} 失败: {raw.sqlite3_errmsg(database).utf8_to_string()} (code {rc})");
        }
        finally
        {
            raw.sqlite3_finalize(statement);
        }
    }

    private static bool IsFtsShadowTable(string tableName, IReadOnlyList<string> ftsTableNames)
    {
        foreach (var ftsTableName in ftsTableNames)
        {
            if (tableName == ftsTableName + "_data" ||
                tableName == ftsTableName + "_idx" ||
                tableName == ftsTableName + "_content" ||
                tableName == ftsTableName + "_docsize" ||
                tableName == ftsTableName + "_config")
            {
                return true;
            }
        }

        return false;
    }

    private static void VerifyDatabaseIntegrity(sqlite3 database)
    {
        var rc = raw.sqlite3_prepare_v2(database, "PRAGMA quick_check", out var stmt);
        if (rc != raw.SQLITE_OK)
            throw new InvalidOperationException($"准备导出数据库完整性检查失败: {raw.sqlite3_errmsg(database).utf8_to_string()} (code {rc})");

        try
        {
            rc = raw.sqlite3_step(stmt);
            if (rc != raw.SQLITE_ROW)
                throw new InvalidOperationException($"导出数据库完整性检查失败: {raw.sqlite3_errmsg(database).utf8_to_string()} (code {rc})");

            var result = raw.sqlite3_column_text(stmt, 0).utf8_to_string();
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"导出数据库完整性检查失败: {result}");
        }
        finally
        {
            raw.sqlite3_finalize(stmt);
        }
    }

    private void CopySingleTableData(sqlite3 srcDb, sqlite3 destDb, string table, int batchSize, int commitEvery, ref long copiedTotal, IProgress<long>? progress)
    {
        // 列清单
        List<(int cid, string name)> cols = new();
        var rc = raw.sqlite3_prepare_v2(srcDb, $"PRAGMA table_info({QuoteIdent(table)})", out var infoStmt);
        if (rc != raw.SQLITE_OK)
            throw new InvalidOperationException($"读取表 {table} 的列结构失败: {raw.sqlite3_errmsg(srcDb).utf8_to_string()} (code {rc})");
        try
        {
            while ((rc = raw.sqlite3_step(infoStmt)) == raw.SQLITE_ROW)
            {
                int cid = raw.sqlite3_column_int(infoStmt, 0);
                string name = raw.sqlite3_column_text(infoStmt, 1).utf8_to_string();
                cols.Add((cid, name));
            }

            if (rc != raw.SQLITE_DONE)
                throw new InvalidOperationException($"读取表 {table} 的列结构失败: {raw.sqlite3_errmsg(srcDb).utf8_to_string()} (code {rc})");
        }
        finally
        {
            raw.sqlite3_finalize(infoStmt);
        }
        if (cols.Count == 0)
            return;

        var orderedCols = cols.OrderBy(c => c.cid).ToList();
        string quotedCols = string.Join(",", orderedCols.Select(c => QuoteIdent(c.name)));

        // 判定隐藏 rowid。声明列可能遮蔽 rowid 别名。
        bool hasRowId = false;
        string? rowIdAlias = new[] { "rowid", "_rowid_", "oid" }
            .FirstOrDefault(alias => orderedCols.All(column =>
                !string.Equals(column.name, alias, StringComparison.OrdinalIgnoreCase)));
        long minKey = 0, maxKey = 0;
        if (rowIdAlias is not null &&
            raw.sqlite3_prepare_v2(srcDb, $"SELECT MIN({rowIdAlias}), MAX({rowIdAlias}) FROM {QuoteIdent(table)}", out var mmStmt) == raw.SQLITE_OK)
        {
            try
            {
                rc = raw.sqlite3_step(mmStmt);
                if (rc != raw.SQLITE_ROW)
                    throw new InvalidOperationException($"读取表 {table} 的 rowid 范围失败: {raw.sqlite3_errmsg(srcDb).utf8_to_string()} (code {rc})");
                minKey = raw.sqlite3_column_int64(mmStmt, 0);
                maxKey = raw.sqlite3_column_int64(mmStmt, 1);
                hasRowId = true;
            }
            finally
            {
                raw.sqlite3_finalize(mmStmt);
            }
        }

        // 准备 INSERT 语句
        var insertColumnCount = orderedCols.Count + (hasRowId ? 1 : 0);
        string placeholders = string.Join(",", Enumerable.Range(1, insertColumnCount).Select(i => $"?{i}"));
        string insertColumns = hasRowId ? $"{rowIdAlias},{quotedCols}" : quotedCols;
        string insertSql = $"INSERT INTO {QuoteIdent(table)}({insertColumns}) VALUES({placeholders})";
        if (raw.sqlite3_prepare_v2(destDb, insertSql, out var insertStmt) != raw.SQLITE_OK)
        {
            var em = raw.sqlite3_errmsg(destDb).utf8_to_string();
            throw new Exception($"准备目标插入失败({table}): {em}");
        }

        int currentBatch = Math.Max(1, batchSize);
        try
        {
            if (hasRowId)
            {
                string keyExpr = rowIdAlias!;
                string selectCols = $"{keyExpr},{quotedCols}";

                string selectSql = $"SELECT {selectCols} FROM {QuoteIdent(table)} WHERE {keyExpr} >= ?1 ORDER BY {keyExpr} LIMIT ?2";
                if (raw.sqlite3_prepare_v2(srcDb, selectSql, out var selectStmt) != raw.SQLITE_OK)
                {
                    var em = raw.sqlite3_errmsg(srcDb).utf8_to_string();
                    throw new Exception($"准备源查询失败({table}): {em}");
                }

                try
                {
                    long nextKey = minKey;
                    while (nextKey <= maxKey)
                    {
                        raw.sqlite3_reset(selectStmt);
                        raw.sqlite3_clear_bindings(selectStmt);
                        raw.sqlite3_bind_int64(selectStmt, 1, nextKey);
                        raw.sqlite3_bind_int(selectStmt, 2, currentBatch);

                        int stepRc;
                        bool anyRow = false;
                        long lastSuccess = nextKey;

                        while ((stepRc = raw.sqlite3_step(selectStmt)) == raw.SQLITE_ROW)
                        {
                            anyRow = true;
                            long keyVal = raw.sqlite3_column_int64(selectStmt, 0);

                            raw.sqlite3_reset(insertStmt);
                            raw.sqlite3_clear_bindings(insertStmt);
                            if (hasRowId)
                                raw.sqlite3_bind_int64(insertStmt, 1, keyVal);

                            // 绑定列
                            int srcIdx = 1; // select 第 0 列是 key
                            for (int i = 0; i < orderedCols.Count; i++)
                            {
                                var parameterIndex = i + 2;

                                int type = raw.sqlite3_column_type(selectStmt, srcIdx);
                                if (type == raw.SQLITE_NULL)
                                {
                                    raw.sqlite3_bind_null(insertStmt, parameterIndex);
                                }
                                else if (type == raw.SQLITE_INTEGER)
                                {
                                    long v = raw.sqlite3_column_int64(selectStmt, srcIdx);
                                    raw.sqlite3_bind_int64(insertStmt, parameterIndex, v);
                                }
                                else if (type == raw.SQLITE_FLOAT)
                                {
                                    double v = raw.sqlite3_column_double(selectStmt, srcIdx);
                                    raw.sqlite3_bind_double(insertStmt, parameterIndex, v);
                                }
                                else if (type == raw.SQLITE_TEXT)
                                {
                                    string v = raw.sqlite3_column_text(selectStmt, srcIdx).utf8_to_string();
                                    raw.sqlite3_bind_text(insertStmt, parameterIndex, v);
                                }
                                else if (type == raw.SQLITE_BLOB)
                                {
                                    var span = raw.sqlite3_column_blob(selectStmt, srcIdx);
                                    if (span.Length == 0)
                                        raw.sqlite3_bind_zeroblob(insertStmt, parameterIndex, 0);
                                    else
                                        raw.sqlite3_bind_blob(insertStmt, parameterIndex, span.ToArray());
                                }

                                srcIdx++;
                            }

                            int irc = raw.sqlite3_step(insertStmt);
                            if (irc != raw.SQLITE_DONE)
                            {
                                var em = raw.sqlite3_errmsg(destDb).utf8_to_string();
                                throw new Exception($"插入失败({table}): {em} (code {irc})");
                            }

                            copiedTotal++;
                            lastSuccess = keyVal;

                            if (copiedTotal % commitEvery == 0)
                            {
                                ExecuteSqlOrThrow(destDb, "COMMIT", $"分批提交表 {table} 失败");
                                ExecuteSqlOrThrow(destDb, "BEGIN IMMEDIATE", $"继续复制表 {table} 失败");
                                progress?.Report(copiedTotal);
                            }
                        }

                        if (stepRc != raw.SQLITE_DONE)
                        {
                            var em = raw.sqlite3_errmsg(srcDb).utf8_to_string();
                            throw new Exception($"读取失败({table}): {em} (code {stepRc})");
                        }

                        if (!anyRow)
                            break;
                        if (lastSuccess == maxKey)
                            break;
                        nextKey = lastSuccess + 1;
                    }
                }
                finally
                {
                    raw.sqlite3_finalize(selectStmt);
                }
            }
            else
            {
                // 顺序扫描（无法精确跳过损坏）
                string selectSql = $"SELECT {quotedCols} FROM {QuoteIdent(table)}";
                if (raw.sqlite3_prepare_v2(srcDb, selectSql, out var selectStmt) != raw.SQLITE_OK)
                {
                    var em = raw.sqlite3_errmsg(srcDb).utf8_to_string();
                    throw new Exception($"准备源查询失败({table}): {em}");
                }

                try
                {
                    int stepRc;
                    while ((stepRc = raw.sqlite3_step(selectStmt)) == raw.SQLITE_ROW)
                    {
                        raw.sqlite3_reset(insertStmt);
                        raw.sqlite3_clear_bindings(insertStmt);
                        for (int i = 0; i < orderedCols.Count; i++)
                        {
                            int type = raw.sqlite3_column_type(selectStmt, i);
                            if (type == raw.SQLITE_NULL)
                                raw.sqlite3_bind_null(insertStmt, i + 1);
                            else if (type == raw.SQLITE_INTEGER)
                                raw.sqlite3_bind_int64(insertStmt, i + 1, raw.sqlite3_column_int64(selectStmt, i));
                            else if (type == raw.SQLITE_FLOAT)
                                raw.sqlite3_bind_double(insertStmt, i + 1, raw.sqlite3_column_double(selectStmt, i));
                            else if (type == raw.SQLITE_TEXT)
                                raw.sqlite3_bind_text(insertStmt, i + 1, raw.sqlite3_column_text(selectStmt, i).utf8_to_string());
                            else if (type == raw.SQLITE_BLOB)
                            {
                                var span = raw.sqlite3_column_blob(selectStmt, i);
                                if (span.Length == 0)
                                    raw.sqlite3_bind_zeroblob(insertStmt, i + 1, 0);
                                else
                                    raw.sqlite3_bind_blob(insertStmt, i + 1, span.ToArray());
                            }
                        }

                        int irc = raw.sqlite3_step(insertStmt);
                        if (irc != raw.SQLITE_DONE)
                        {
                            var em = raw.sqlite3_errmsg(destDb).utf8_to_string();
                            throw new Exception($"插入失败({table}): {em} (code {irc})");
                        }

                        copiedTotal++;
                        if (copiedTotal % commitEvery == 0)
                        {
                            ExecuteSqlOrThrow(destDb, "COMMIT", $"分批提交表 {table} 失败");
                            ExecuteSqlOrThrow(destDb, "BEGIN IMMEDIATE", $"继续复制表 {table} 失败");
                            progress?.Report(copiedTotal);
                        }
                    }

                    if (stepRc != raw.SQLITE_DONE)
                        throw new Exception($"读取失败({table}): {raw.sqlite3_errmsg(srcDb).utf8_to_string()} (code {stepRc})");
                }
                finally
                {
                    raw.sqlite3_finalize(selectStmt);
                }
            }
        }
        finally
        {
            raw.sqlite3_finalize(insertStmt);
        }

    }
    /// <summary>
    /// 重写不支持的FTS5 tokenizer，提高兼容性
    /// </summary>
    private static string RewriteUnsupportedFtsTokenizer(string originalSql, string tableName)
    {
        if (string.IsNullOrWhiteSpace(originalSql))
            return originalSql;

        // 检查是否为FTS5虚拟表
        if (!IsFts5VirtualTable(originalSql))
            return originalSql;

        try
        {
            return RewriteFts5TokenizerSafe(originalSql, tableName);
        }
        catch
        {
            //Console.WriteLine($"⚠️ 表 {tableName} FTS tokenizer重写失败: {ex.Message}，使用原始SQL");
            return originalSql;
        }
    }

    /// <summary>
    /// 检查是否为FTS5虚拟表
    /// </summary>
    private static bool IsFts5VirtualTable(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return false;

        // 转换为小写便于匹配，但保留原始字符串用于重写
        string lowerSql = sql.ToLowerInvariant();

        return lowerSql.Contains("create") &&
               lowerSql.Contains("virtual") &&
               lowerSql.Contains("table") &&
               lowerSql.Contains("using") &&
               lowerSql.Contains("fts5");
    }

    /// <summary>
    /// 安全地重写FTS5 tokenizer
    /// </summary>
    private static string RewriteFts5TokenizerSafe(string sql, string tableName)
    {
        // 已知的不支持tokenizer及其替代方案
        var unsupportedTokenizers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "pinyin_letter", "unicode61" },
            { "pinyin", "unicode61" },
            { "jieba", "unicode61" },
            { "simple", "unicode61" },
            { "icu", "unicode61" }
        };

        string result = sql;
        bool wasModified = false;

        foreach (var (unsupported, replacement) in unsupportedTokenizers)
        {
            // 查找 tokenize 参数的多种可能格式
            var patterns = new[]
            {
                $"tokenize = '{unsupported}'",
                $"tokenize = \"{unsupported}\"",
                $"tokenize = '{unsupported} ",     // 带参数的情况，如 'pinyin_letter 1'
                $"tokenize = \"{unsupported} ",
                $"tokenize='{unsupported}'",
                $"tokenize=\"{unsupported}\"",
                $"tokenize='{unsupported} ",
                $"tokenize=\"{unsupported} "
            };

            foreach (string pattern in patterns)
            {
                int index = result.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    // 找到匹配的模式，进行替换
                    string replacement_full = ReplaceTokenizerInPattern(result, index, pattern, unsupported, replacement);
                    if (replacement_full != result)
                    {
                        result = replacement_full;
                        wasModified = true;
                        //Console.WriteLine($"🔧 表 {tableName}: 替换tokenizer '{unsupported}' -> '{replacement}'");
                        break; // 替换成功，跳出内层循环
                    }
                }
            }

            if (wasModified)
                break; // 已经替换过，跳出外层循环
        }

        return result;
    }

    /// <summary>
    /// 在特定模式中替换tokenizer
    /// </summary>
    private static string ReplaceTokenizerInPattern(string sql, int patternIndex, string pattern, string oldTokenizer, string newTokenizer)
    {
        try
        {
            // 找到引号的开始和结束位置
            int equalsIndex = sql.IndexOf('=', patternIndex);
            if (equalsIndex < 0)
                return sql;

            // 找到引号开始位置
            int quoteStart = -1;
            char quoteChar = '\0';
            for (int i = equalsIndex + 1; i < sql.Length; i++)
            {
                if (sql[i] == '\'' || sql[i] == '"')
                {
                    quoteStart = i;
                    quoteChar = sql[i];
                    break;
                }
            }

            if (quoteStart < 0)
                return sql;

            // 找到匹配的结束引号
            int quoteEnd = -1;
            for (int i = quoteStart + 1; i < sql.Length; i++)
            {
                if (sql[i] == quoteChar)
                {
                    quoteEnd = i;
                    break;
                }
            }

            if (quoteEnd < 0)
                return sql;

            // 提取引号内的内容
            string tokenizeValue = sql.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);

            // 检查是否包含旧的tokenizer
            if (tokenizeValue.IndexOf(oldTokenizer, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // 替换为新的tokenizer（保持简单，直接用新tokenizer替换整个值）
                string before = sql.Substring(0, quoteStart + 1);
                string after = sql.Substring(quoteEnd);
                return before + newTokenizer + after;
            }
        }
        catch
        {
            // 出现任何解析错误都返回原始SQL
        }

        return sql;
    }

    public void Dispose()
    {
        var database = _db;
        _db = null;
        try
        {
            database?.Dispose();
        }
        finally
        {
            PCQQPageDecryptVfs.Release(_pcqqVfsName);
            _pcqqVfsName = null;
        }
    }

}
