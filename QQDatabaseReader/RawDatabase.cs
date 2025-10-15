using System.Text;
using SQLitePCL;
using System.Security.Cryptography;
using QQDatabaseReader.Sqlite;
using System.Linq;

namespace QQDatabaseReader;

public class RawDatabase : IDisposable
{
    public string DatabaseFilePath { get; }
    public bool UseVFS { get; }
    public string? CipherPassword { get; }
    public int? CipherPageSize { get; }
    public int? CipherKdfIter { get; }
    public HashAlgorithmName? CipherKdfAlgorithm { get; }
    public HashAlgorithmName? CipherHmacAlgorithm { get; }

    private sqlite3? _db;

    public sqlite3 Database => _db!;

    static RawDatabase()
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());
        QQNTFileOffsetVfs.Register();
    }

    public RawDatabase(string dbFilePath, bool useVFS = false)
    {
        if (!File.Exists(dbFilePath))
        {
            throw new Exception($"Error: Database file not found: {DatabaseFilePath}");
        }

        DatabaseFilePath = dbFilePath;
        UseVFS = useVFS;
    }

    public RawDatabase(string databaseFilePath, string cipherPassword, HashAlgorithmName? cipherKdfAlgorithm = null, HashAlgorithmName? cipherHmacAlgorithm = null, int cipherPageSize = 4096, int cipherKdfIter = 4000, bool useVFS = true)
    {
        if (!File.Exists(databaseFilePath))
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
        int rc = raw.sqlite3_open_v2(DatabaseFilePath, out _db, raw.SQLITE_OPEN_READONLY, UseVFS ? QQNTFileOffsetVfs.VfsName : null);
        if (rc != raw.SQLITE_OK)
        {
            var errorMsg = raw.sqlite3_errmsg(Database).utf8_to_string();
            throw new Exception($"Failed to open database: {errorMsg}");
        }

        // 如果启用了解密，使用 PRAGMA key 方法（SQLCipher）
        if (!string.IsNullOrEmpty(CipherPassword))
        {
            rc += raw.sqlite3_exec(Database, $"PRAGMA key = '{CipherPassword}';", null, IntPtr.Zero, out var keyErr);
            rc += raw.sqlite3_exec(Database, $"PRAGMA cipher_page_size = {CipherPageSize};", null, IntPtr.Zero, out _);
            rc += raw.sqlite3_exec(Database, $"PRAGMA kdf_iter = {CipherKdfIter};", null, IntPtr.Zero, out _);
            rc += raw.sqlite3_exec(Database, $"PRAGMA cipher_hmac_algorithm = HMAC_{CipherHmacAlgorithm!.Value.Name};", null, IntPtr.Zero, out _);
            rc += raw.sqlite3_exec(Database, $"PRAGMA cipher_default_kdf_algorithm = PBKDF2_HMAC_{CipherKdfAlgorithm!.Value.Name};", null, IntPtr.Zero, out _);
            rc += raw.sqlite3_exec(Database, "SELECT count(*) FROM sqlite_master;", null, IntPtr.Zero, out _);
            if (rc != raw.SQLITE_OK)
            {
                throw new Exception("解密失败");
            }
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

    public static string? GetRand(string databaseFilePath)
    {
        if (!File.Exists(databaseFilePath))
            return null;

        using var fs = new FileStream(databaseFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length < 1024)
            return null;

        var header = "SQLite header 3"u8;
        Span<byte> buffer = stackalloc byte[1024];
        fs.ReadExactly(buffer);
        if (!buffer[..header.Length].SequenceEqual(header))
            return null;

        var qqntdbHeader = "QQ_NT DB"u8;
        var qqntIndex = buffer.IndexOf(qqntdbHeader);
        if (qqntIndex < 0)
            return null;
        bool isRand = false;
        StringBuilder s = new(8);
        foreach (var item in buffer[(qqntIndex + qqntdbHeader.Length)..])
        {
            var isChar = char.IsLetter((char)item);
            if (isRand && !isChar)
            {
                break;
            }
            if (isChar)
            {
                isRand = isChar;
                s.Append((char)item);
            }

        }

        return s.ToString();
    }


    private static string QuoteIdent(string name)
    {
        return "\"" + name.Replace("\"", "\"\"") + "\"";
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
    /// 对每张表优先使用 rowid 驱动复制；若无 rowid 则尝试 INTEGER PRIMARY KEY；否则回退为全表顺序扫描。
    /// 遇到损坏页时：rowid/整数主键路径使用指数跳过；顺序扫描无法精确跳过，仅记录错误后继续下一表。
    /// 可选进度：reportTotalRows 回调总行数；progress 每成功插入一行时 Report 当前累计行数。
    /// </summary>
    public void ExportToNewDatabase(string destPath, int batchSize = 2048, int commitEvery = 20000, Action<long>? reportTotalRows = null, IProgress<long>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(destPath))
            throw new ArgumentException("destPath 不能为空");
        if (File.Exists(destPath))
            File.Delete(destPath);

        int rc = raw.sqlite3_open_v2(destPath, out var newDb, raw.SQLITE_OPEN_READWRITE | raw.SQLITE_OPEN_CREATE, null);
        if (rc != raw.SQLITE_OK)
        {
            var msg = raw.sqlite3_errmsg(newDb).utf8_to_string();
            throw new Exception($"无法创建目标数据库: {msg}");
        }

        try
        {
            // 获取所有用户表及其 CREATE TABLE 语句
            List<(string name, string sql, string type)> tables = new();
            raw.sqlite3_prepare_v2(_db, "SELECT name, sql, type FROM sqlite_master WHERE type IN ('table') AND name NOT LIKE 'sqlite_%' AND sql NOT NULL ORDER BY name", out var tblStmt);
            while (raw.sqlite3_step(tblStmt) == raw.SQLITE_ROW)
            {
                string name = raw.sqlite3_column_text(tblStmt, 0).utf8_to_string();
                string sql = raw.sqlite3_column_text(tblStmt, 1).utf8_to_string();
                string type = raw.sqlite3_column_text(tblStmt, 2).utf8_to_string();
                tables.Add((name, sql, type));
            }
            raw.sqlite3_finalize(tblStmt);

            // 分离普通表和FTS表
            var normalTables = tables.Where(t => !IsFts5VirtualTable(t.sql)).ToList();
            var ftsTables = tables.Where(t => IsFts5VirtualTable(t.sql)).ToList();

            // 先创建普通表
            foreach (var (name, sql, type) in normalTables)
            {
                rc = raw.sqlite3_exec(newDb, sql, null, IntPtr.Zero, out _);
                if (rc != raw.SQLITE_OK)
                {
                    var msg2 = raw.sqlite3_errmsg(newDb).utf8_to_string();
                    throw new Exception($"创建普通表 {name} 失败: {msg2}");
                }
            }

            // 后创建FTS表（处理tokenizer兼容性）
            foreach (var (name, sql, type) in ftsTables)
            {
                // 先清理可能存在的FTS内部表
                CleanupExistingFtsTable(newDb, name);
                
                // 处理不支持的FTS5 tokenizer
                string createSql = RewriteUnsupportedFtsTokenizer(sql, name);
                
                rc = raw.sqlite3_exec(newDb, createSql, null, IntPtr.Zero, out _);
                if (rc != raw.SQLITE_OK)
                {
                    var msg2 = raw.sqlite3_errmsg(newDb).utf8_to_string();
                    
                    // 如果是FTS相关错误，尝试跳过该表
                    if (msg2.Contains("no such tokenizer") || msg2.Contains("already exists") || msg2.Contains("fts"))
                    {
                        continue;
                    }
                    
                    throw new Exception($"创建FTS表 {name} 失败: {msg2}");
                }
            }

            // 计算总行数（进度总量）
            long totalRows = 0;
            foreach (var (name, _, _) in tables)
            {
                if (raw.sqlite3_prepare_v2(_db, $"SELECT COUNT(*) FROM {QuoteIdent(name)}", out var cntStmt) == raw.SQLITE_OK)
                {
                    if (raw.sqlite3_step(cntStmt) == raw.SQLITE_ROW)
                    {
                        totalRows += raw.sqlite3_column_int64(cntStmt, 0);
                    }
                    raw.sqlite3_finalize(cntStmt);
                }
            }
            reportTotalRows?.Invoke(totalRows);

            // 复制前性能优化参数（目标库）
            raw.sqlite3_exec(newDb, "PRAGMA synchronous=OFF", null, IntPtr.Zero, out _);
            raw.sqlite3_exec(newDb, "PRAGMA journal_mode=OFF", null, IntPtr.Zero, out _);
            raw.sqlite3_exec(newDb, "PRAGMA temp_store=MEMORY", null, IntPtr.Zero, out _);
            raw.sqlite3_exec(newDb, "PRAGMA foreign_keys=OFF", null, IntPtr.Zero, out _);

            long copiedTotal = 0;
            // 每张表独立事务复制，避免单表失败导致全部数据回滚
            foreach (var (table, _, _) in tables)
            {
                // 开启表级事务
                raw.sqlite3_exec(newDb, "BEGIN IMMEDIATE", null, IntPtr.Zero, out _);
                try
                {
                    CopySingleTableData(_db!, newDb, table, batchSize, commitEvery, ref copiedTotal, progress);
                    progress?.Report(copiedTotal);
                    raw.sqlite3_exec(newDb, "COMMIT", null, IntPtr.Zero, out _);
                }
                catch (Exception)
                {
                    // 回滚当前表，继续下一张，避免整库失败
                    raw.sqlite3_exec(newDb, "ROLLBACK", null, IntPtr.Zero, out _);
                    // 可选：输出错误以便诊断
                    // Console.WriteLine($"[WARN] 表 {table} 复制失败");
                }
            }

            // 重建所有索引/触发器
            raw.sqlite3_prepare_v2(_db, "SELECT sql FROM sqlite_master WHERE (type='index' OR type='trigger') AND name NOT LIKE 'sqlite_%' AND sql NOT NULL", out var itStmt);
            while (raw.sqlite3_step(itStmt) == raw.SQLITE_ROW)
            {
                var ddl = raw.sqlite3_column_text(itStmt, 0).utf8_to_string();
                try
                { raw.sqlite3_exec(newDb, ddl, null, IntPtr.Zero, out _); }
                catch { }
            }
            raw.sqlite3_finalize(itStmt);
        }
        finally
        {
            raw.sqlite3_close_v2(newDb);
        }
    }

    private void CopySingleTableData(sqlite3 srcDb, sqlite3 destDb, string table, int batchSize, int commitEvery, ref long copiedTotal, IProgress<long>? progress)
    {
        // 列清单
        List<(int cid, string name, string type, int pk)> cols = new();
        if (raw.sqlite3_prepare_v2(srcDb, $"PRAGMA table_info({QuoteIdent(table)})", out var infoStmt) == raw.SQLITE_OK)
        {
            while (raw.sqlite3_step(infoStmt) == raw.SQLITE_ROW)
            {
                int cid = raw.sqlite3_column_int(infoStmt, 0);
                string name = raw.sqlite3_column_text(infoStmt, 1).utf8_to_string();
                string type = raw.sqlite3_column_text(infoStmt, 2).utf8_to_string();
                int pk = raw.sqlite3_column_int(infoStmt, 5);
                cols.Add((cid, name, type, pk));
            }
            raw.sqlite3_finalize(infoStmt);
        }
        if (cols.Count == 0)
            return;

        var orderedCols = cols.OrderBy(c => c.cid).ToList();
        string quotedCols = string.Join(",", orderedCols.Select(c => QuoteIdent(c.name)));

        // 判定 rowid 或 INTEGER PRIMARY KEY
        bool hasRowId = false;
        long minKey = 0, maxKey = 0;
        if (raw.sqlite3_prepare_v2(srcDb, $"SELECT MIN(rowid), MAX(rowid) FROM {QuoteIdent(table)}", out var mmStmt) == raw.SQLITE_OK)
        {
            if (raw.sqlite3_step(mmStmt) == raw.SQLITE_ROW)
            {
                minKey = raw.sqlite3_column_int64(mmStmt, 0);
                maxKey = raw.sqlite3_column_int64(mmStmt, 1);
                hasRowId = true;
            }
            raw.sqlite3_finalize(mmStmt);
        }

        string? pkName = null;
        bool pkIsInteger = false;
        if (!hasRowId)
        {
            var pkCol = orderedCols.FirstOrDefault(c => c.pk == 1 && c.type.IndexOf("INT", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrEmpty(pkCol.name))
            {
                pkName = pkCol.name;
                pkIsInteger = true;
                if (raw.sqlite3_prepare_v2(srcDb, $"SELECT MIN({QuoteIdent(pkName)}), MAX({QuoteIdent(pkName)}) FROM {QuoteIdent(table)}", out var pkmm) == raw.SQLITE_OK)
                {
                    if (raw.sqlite3_step(pkmm) == raw.SQLITE_ROW)
                    {
                        minKey = raw.sqlite3_column_int64(pkmm, 0);
                        maxKey = raw.sqlite3_column_int64(pkmm, 1);
                    }
                    raw.sqlite3_finalize(pkmm);
                }
            }
        }

        // 准备 INSERT 语句
        string placeholders = string.Join(",", Enumerable.Range(1, orderedCols.Count).Select(i => $"?{i}"));
        string insertSql = $"INSERT INTO {QuoteIdent(table)}({quotedCols}) VALUES({placeholders})";
        if (raw.sqlite3_prepare_v2(destDb, insertSql, out var insertStmt) != raw.SQLITE_OK)
        {
            var em = raw.sqlite3_errmsg(destDb).utf8_to_string();
            throw new Exception($"准备目标插入失败({table}): {em}");
        }

        int currentBatch = Math.Max(1, batchSize);
        int copied = 0;

        try
        {
            if (hasRowId || pkIsInteger)
            {
                string keyExpr = hasRowId ? "rowid" : QuoteIdent(pkName!);
                string selectCols;
                if (hasRowId)
                {
                    // rowid 不在列集合中
                    selectCols = $"{keyExpr},{quotedCols}";
                }
                else
                {
                    // 将主键放在首列，后续列排除主键，避免重复
                    string restCols = string.Join(",", orderedCols.Where(c => !string.Equals(c.name, pkName, StringComparison.OrdinalIgnoreCase)).Select(c => QuoteIdent(c.name)));
                    selectCols = restCols.Length == 0 ? keyExpr : $"{keyExpr},{restCols}";
                }

                string selectSql = $"SELECT {selectCols} FROM {QuoteIdent(table)} WHERE {keyExpr} > ?1 ORDER BY {keyExpr} LIMIT ?2";
                if (raw.sqlite3_prepare_v2(srcDb, selectSql, out var selectStmt) != raw.SQLITE_OK)
                {
                    var em = raw.sqlite3_errmsg(srcDb).utf8_to_string();
                    throw new Exception($"准备源查询失败({table}): {em}");
                }

                long last = hasRowId ? (minKey - 1) : (minKey - 1);
                while (last < maxKey)
                {
                    raw.sqlite3_reset(selectStmt);
                    raw.sqlite3_clear_bindings(selectStmt);
                    raw.sqlite3_bind_int64(selectStmt, 1, last);
                    raw.sqlite3_bind_int(selectStmt, 2, currentBatch);

                    int stepRc;
                    bool anyRow = false;
                    long lastSuccess = last;

                    while ((stepRc = raw.sqlite3_step(selectStmt)) == raw.SQLITE_ROW)
                    {
                        anyRow = true;
                        long keyVal = raw.sqlite3_column_int64(selectStmt, 0);

                        raw.sqlite3_reset(insertStmt);
                        raw.sqlite3_clear_bindings(insertStmt);

                        // 绑定列
                        int srcIdx = 1; // select 第 0 列是 key
                        for (int i = 0; i < orderedCols.Count; i++)
                        {
                            var col = orderedCols[i];
                            if (!hasRowId && pkIsInteger && string.Equals(col.name, pkName, StringComparison.OrdinalIgnoreCase))
                            {
                                raw.sqlite3_bind_int64(insertStmt, i + 1, keyVal);
                                continue;
                            }

                            int type = raw.sqlite3_column_type(selectStmt, srcIdx);
                            if (type == raw.SQLITE_NULL)
                            {
                                raw.sqlite3_bind_null(insertStmt, i + 1);
                            }
                            else if (type == raw.SQLITE_INTEGER)
                            {
                                long v = raw.sqlite3_column_int64(selectStmt, srcIdx);
                                raw.sqlite3_bind_int64(insertStmt, i + 1, v);
                            }
                            else if (type == raw.SQLITE_FLOAT)
                            {
                                double v = raw.sqlite3_column_double(selectStmt, srcIdx);
                                raw.sqlite3_bind_double(insertStmt, i + 1, v);
                            }
                            else if (type == raw.SQLITE_TEXT)
                            {
                                string v = raw.sqlite3_column_text(selectStmt, srcIdx).utf8_to_string();
                                raw.sqlite3_bind_text(insertStmt, i + 1, v);
                            }
                            else if (type == raw.SQLITE_BLOB)
                            {
                                var span = raw.sqlite3_column_blob(selectStmt, srcIdx);
                                if (span.Length == 0)
                                {
                                    raw.sqlite3_bind_zeroblob(insertStmt, i + 1, 0);
                                }
                                else
                                {
                                    raw.sqlite3_bind_blob(insertStmt, i + 1, span.ToArray());
                                }
                            }

                            srcIdx++;
                        }

                        int irc = raw.sqlite3_step(insertStmt);
                        if (irc != raw.SQLITE_DONE)
                        {
                            var em = raw.sqlite3_errmsg(destDb).utf8_to_string();
                            throw new Exception($"插入失败({table}): {em} (code {irc})");
                        }

                        copied++;
                        copiedTotal++;
                        lastSuccess = keyVal;

                        if (copiedTotal % commitEvery == 0)
                        {
                            raw.sqlite3_exec(destDb, "COMMIT", null, IntPtr.Zero, out _);
                            raw.sqlite3_exec(destDb, "BEGIN IMMEDIATE", null, IntPtr.Zero, out _);
                            progress?.Report(copiedTotal);
                        }
                    }

                    if (stepRc == raw.SQLITE_DONE)
                    {
                        if (!anyRow)
                            break;
                        last = lastSuccess;
                        continue;
                    }

                    if (stepRc == raw.SQLITE_CORRUPT || stepRc == raw.SQLITE_IOERR)
                    {
                        if (currentBatch > 1)
                        {
                            currentBatch = 1;
                            continue;
                        }

                        long delta = 1;
                        bool jumped = false;
                        while (last + delta < maxKey)
                        {
                            raw.sqlite3_reset(selectStmt);
                            raw.sqlite3_clear_bindings(selectStmt);
                            raw.sqlite3_bind_int64(selectStmt, 1, last + delta);
                            raw.sqlite3_bind_int(selectStmt, 2, 1);
                            int trc = raw.sqlite3_step(selectStmt);
                            if (trc == raw.SQLITE_ROW)
                            {
                                long nextKey = raw.sqlite3_column_int64(selectStmt, 0);
                                last = nextKey - 1;
                                jumped = true;
                                break;
                            }
                            else if (trc == raw.SQLITE_DONE)
                            {
                                last = maxKey;
                                jumped = true;
                                break;
                            }
                            delta = Math.Min(delta << 1, maxKey - last);
                        }
                        if (!jumped)
                            break;
                        continue;
                    }
                    else
                    {
                        var em = raw.sqlite3_errmsg(srcDb).utf8_to_string();
                        throw new Exception($"读取失败({table}): {em} (code {stepRc})");
                    }
                }

                raw.sqlite3_finalize(selectStmt);
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

                    copied++;
                    copiedTotal++;
                    if (copiedTotal % commitEvery == 0)
                    {
                        raw.sqlite3_exec(destDb, "COMMIT", null, IntPtr.Zero, out _);
                        raw.sqlite3_exec(destDb, "BEGIN IMMEDIATE", null, IntPtr.Zero, out _);
                        progress?.Report(copiedTotal);
                    }
                }

                raw.sqlite3_finalize(selectStmt);

                if (stepRc == raw.SQLITE_CORRUPT || stepRc == raw.SQLITE_IOERR)
                {
                    //Console.WriteLine($"[{table}] 顺序扫描遇到损坏，可能未能完整复制");
                }
            }
        }
        finally
        {
            raw.sqlite3_finalize(insertStmt);
        }

    }

    /// <summary>
    /// 清理已存在的FTS表及其内部表
    /// </summary>
    private static void CleanupExistingFtsTable(sqlite3 db, string ftsTableName)
    {
        try
        {
            // FTS5会创建多个内部表，需要全部清理
            var internalTables = new[]
            {
                $"{ftsTableName}_data",
                $"{ftsTableName}_idx", 
                $"{ftsTableName}_content",
                $"{ftsTableName}_docsize",
                $"{ftsTableName}_config"
            };

            // 先删除FTS虚拟表本身
            raw.sqlite3_exec(db, $"DROP TABLE IF EXISTS {QuoteIdent(ftsTableName)}", null, IntPtr.Zero, out _);

            // 然后删除所有可能的内部表
            foreach (string internalTable in internalTables)
            {
                raw.sqlite3_exec(db, $"DROP TABLE IF EXISTS {QuoteIdent(internalTable)}", null, IntPtr.Zero, out _);
            }
        }
        catch
        {
            throw;
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
        Database.Dispose();
    }

}
