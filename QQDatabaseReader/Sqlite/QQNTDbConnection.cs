using System.Data;
using System.Data.Common;
using System.Diagnostics;
using SQLitePCL;

namespace QQDatabaseReader.Sqlite;

/// <summary>
/// 自定义的 DbConnection，包装了使用自定义 VFS 和加密的 SQLite 连接
/// 完全基于 SQLitePCL.raw API 实现，不依赖 SqliteConnection 的内部实现
/// </summary>
public class QQNTDbConnection : DbConnection
{
    private readonly sqlite3 _sqlite3Handle;
    private readonly string _dbFilePath;
    private ConnectionState _state = ConnectionState.Closed;

    /// <summary>
    /// 使用已创建的 sqlite3 句柄创建连接
    /// </summary>
    /// <param name="sqlite3Handle">已打开并配置好的 sqlite3 数据库句柄</param>
    /// <param name="dbFilePath">数据库文件路径（用于显示）</param>
    public QQNTDbConnection(sqlite3 sqlite3Handle, string dbFilePath)
    {
        _sqlite3Handle = sqlite3Handle;
        _dbFilePath = dbFilePath;
    }

    /// <summary>
    /// 获取底层的 sqlite3 句柄（供内部使用）
    /// </summary>
    internal sqlite3 Handle => _sqlite3Handle;

#pragma warning disable CS8765 // Nullability of type of parameter doesn't match overridden member
    public override string ConnectionString
    {
        get => $"Data Source={_dbFilePath}";
        set => throw new NotSupportedException("不支持修改连接字符串");
    }
#pragma warning restore CS8765

    public override string Database => _dbFilePath;

    public override string DataSource => _dbFilePath;

    public override string ServerVersion
    {
        get
        {
            var version = raw.sqlite3_libversion();
            return version.utf8_to_string();
        }
    }

    public override ConnectionState State => _state;

    public override void Open()
    {
        if (_state == ConnectionState.Open)
            return;

        // sqlite3 句柄已经在外部打开了，我们只需要标记状态
        _state = ConnectionState.Open;
    }

    public override void Close()
    {
        // 不关闭 sqlite3 句柄，因为它由外部管理
        _state = ConnectionState.Closed;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        if (_state != ConnectionState.Open)
            throw new InvalidOperationException("连接未打开");

        return new QQSqliteTransaction(this, isolationLevel);
    }

    public override void ChangeDatabase(string databaseName)
    {
        throw new NotSupportedException("SQLite 不支持更改数据库");
    }

    protected override DbCommand CreateDbCommand()
    {
        if (_state != ConnectionState.Open)
            throw new InvalidOperationException("连接未打开");

        return new QQSqliteCommand(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 不释放 sqlite3 句柄，因为它由外部管理
            _state = ConnectionState.Closed;
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// 自定义的 DbCommand 实现，使用 SQLitePCL.raw API
/// </summary>
internal class QQSqliteCommand : DbCommand
{
    private QQNTDbConnection? _connection;
    private string? _commandText;
    private readonly DbParameterCollection _parameters = new QQParameterCollection();

    public QQSqliteCommand(QQNTDbConnection connection)
    {
        _connection = connection;
    }

#pragma warning disable CS8765
    public override string CommandText
    {
        get => _commandText ?? string.Empty;
        set => _commandText = value;
    }
#pragma warning restore CS8765

    public override int CommandTimeout { get; set; } = 30;
    public override CommandType CommandType { get; set; } = CommandType.Text;
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = value as QQNTDbConnection;
    }

    protected override DbParameterCollection DbParameterCollection => _parameters;
    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel()
    {
        // SQLite 不支持取消
    }

    public override int ExecuteNonQuery()
    {
        if (_connection == null || _connection.State != ConnectionState.Open)
            throw new InvalidOperationException("连接未打开");

        if (string.IsNullOrEmpty(_commandText))
            throw new InvalidOperationException("CommandText 为空");

        // 使用 prepare + bind + step 支持参数化
        int rc = raw.sqlite3_prepare_v2(_connection.Handle, _commandText, out var stmt);
        if (rc != raw.SQLITE_OK)
        {
            var error = raw.sqlite3_errmsg(_connection.Handle).utf8_to_string();
            throw new InvalidOperationException($"SQL 准备失败: {error}");
        }

        try
        {
            QQSqliteBindHelpers.BindAllParameters(stmt, _parameters);

            // 执行直到 DONE；对非查询语句通常一次 step 即完成
            while (true)
            {
                rc = raw.sqlite3_step(stmt);
                if (rc == raw.SQLITE_ROW)
                    continue;
                if (rc == raw.SQLITE_DONE)
                    break;
                var error = raw.sqlite3_errmsg(_connection.Handle).utf8_to_string();
                throw new InvalidOperationException($"SQL 执行失败: {error} (code {rc})");
            }

            return raw.sqlite3_changes(_connection.Handle);
        }
        finally
        {
            raw.sqlite3_finalize(stmt);
        }
    }

    public override object? ExecuteScalar()
    {
        using var reader = ExecuteReader();
        if (reader.Read() && reader.FieldCount > 0)
        {
            return reader.GetValue(0);
        }
        return null;
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        if (_connection == null || _connection.State != ConnectionState.Open)
            throw new InvalidOperationException("连接未打开");

        if (string.IsNullOrEmpty(_commandText))
            throw new InvalidOperationException("CommandText 为空");

        // 预编译并绑定参数，将已绑定的语句交给 DataReader 管理
        int rc = raw.sqlite3_prepare_v2(_connection.Handle, _commandText, out var stmt);
        if (rc != raw.SQLITE_OK)
        {
            var error = raw.sqlite3_errmsg(_connection.Handle).utf8_to_string();
            throw new InvalidOperationException($"SQL 准备失败: {error}");
        }

        try
        {
            QQSqliteBindHelpers.BindAllParameters(stmt, _parameters);
        }
        catch
        {
            raw.sqlite3_finalize(stmt);
            throw;
        }

        return new QQSqliteDataReader(_connection.Handle, stmt, _commandText);
    }

    public override void Prepare()
    {
        // 不需要预编译
    }

    protected override DbParameter CreateDbParameter()
    {
        return new QQSqliteParameter();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _connection = null;
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// 简单的参数集合实现（目前不支持参数化查询）
/// </summary>
internal class QQParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _parameters = new();

    public override int Count => _parameters.Count;
    public override object SyncRoot => _parameters;

    public override int Add(object value)
    {
        _parameters.Add((DbParameter)value);
        return _parameters.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (DbParameter p in values)
            _parameters.Add(p);
    }

    public override void Clear() => _parameters.Clear();
    public override bool Contains(object value) => _parameters.Contains((DbParameter)value);
    public override bool Contains(string value) => _parameters.Any(p => p.ParameterName == value);
    public override void CopyTo(Array array, int index) => _parameters.ToArray().CopyTo(array, index);
    public override System.Collections.IEnumerator GetEnumerator() => _parameters.GetEnumerator();
    public override int IndexOf(object value) => _parameters.IndexOf((DbParameter)value);
    public override int IndexOf(string parameterName) => _parameters.FindIndex(p => p.ParameterName == parameterName);
    public override void Insert(int index, object value) => _parameters.Insert(index, (DbParameter)value);
    public override void Remove(object value) => _parameters.Remove((DbParameter)value);
    public override void RemoveAt(int index) => _parameters.RemoveAt(index);
    public override void RemoveAt(string parameterName)
    {
        var index = IndexOf(parameterName);
        if (index >= 0) _parameters.RemoveAt(index);
    }

    protected override DbParameter GetParameter(int index) => _parameters[index];
    protected override DbParameter GetParameter(string parameterName) => _parameters.First(p => p.ParameterName == parameterName);
    protected override void SetParameter(int index, DbParameter value) => _parameters[index] = value;
    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var index = IndexOf(parameterName);
        if (index >= 0) _parameters[index] = value;
    }
}

/// <summary>
/// 简单的参数实现
/// </summary>
internal class QQSqliteParameter : DbParameter
{
#pragma warning disable CS8765
    public override DbType DbType { get; set; }
    public override ParameterDirection Direction { get; set; }
    public override bool IsNullable { get; set; }
    public override string ParameterName { get; set; } = string.Empty;
    public override int Size { get; set; }
    public override string SourceColumn { get; set; } = string.Empty;
    public override bool SourceColumnNullMapping { get; set; }
    public override object? Value { get; set; }

    public override void ResetDbType() => DbType = DbType.String;
#pragma warning restore CS8765
}

internal static class QQSqliteBindHelpers
{
    public static void BindAllParameters(sqlite3_stmt stmt, DbParameterCollection parameters)
    {
        if (parameters.Count == 0) return;

        // 若 SQL 使用命名参数，优先按名称绑定；否则按顺序绑定 1..N
        int positional = 1;
        foreach (DbParameter p in parameters)
        {
            string name = p.ParameterName ?? string.Empty;
            int index = 0;
            if (!string.IsNullOrEmpty(name) && (name[0] == '@' || name[0] == ':' || name[0] == '$' || name[0] == '?'))
            {
                index = raw.sqlite3_bind_parameter_index(stmt, name);
            }
            if (index == 0)
            {
                index = positional++;
            }

            BindOne(stmt, index, p.Value);
        }
    }

    private static void BindOne(sqlite3_stmt stmt, int index, object? value)
    {
        if (value == null || value == DBNull.Value)
        {
            raw.sqlite3_bind_null(stmt, index);
            return;
        }

        switch (value)
        {
            case long v:
                raw.sqlite3_bind_int64(stmt, index, v);
                break;
            case int v:
                raw.sqlite3_bind_int64(stmt, index, v);
                break;
            case short v:
                raw.sqlite3_bind_int64(stmt, index, v);
                break;
            case sbyte v:
                raw.sqlite3_bind_int64(stmt, index, v);
                break;
            case byte v:
                raw.sqlite3_bind_int64(stmt, index, v);
                break;
            case bool v:
                raw.sqlite3_bind_int64(stmt, index, v ? 1 : 0);
                break;
            case ulong v:
                // SQLite 仅有有符号 int64，尽量保留下 63 位
                unchecked { raw.sqlite3_bind_int64(stmt, index, (long)v); }
                break;
            case uint v:
                raw.sqlite3_bind_int64(stmt, index, v);
                break;
            case ushort v:
                raw.sqlite3_bind_int64(stmt, index, v);
                break;
            case double v:
                raw.sqlite3_bind_double(stmt, index, v);
                break;
            case float v:
                raw.sqlite3_bind_double(stmt, index, v);
                break;
            case decimal v:
                raw.sqlite3_bind_double(stmt, index, (double)v);
                break;
            case string v:
                raw.sqlite3_bind_text(stmt, index, v);
                break;
            case ReadOnlyMemory<byte> v:
                raw.sqlite3_bind_blob(stmt, index, v.ToArray());
                break;
            case byte[] v:
                raw.sqlite3_bind_blob(stmt, index, v);
                break;
            case Guid v:
                raw.sqlite3_bind_text(stmt, index, v.ToString());
                break;
            case DateTimeOffset v:
                raw.sqlite3_bind_int64(stmt, index, v.ToUnixTimeSeconds());
                break;
            case DateTime v:
                // 以 ISO 文本绑定，避免时区歧义
                raw.sqlite3_bind_text(stmt, index, v.ToString("o"));
                break;
            default:
                // 兜底：ToString 或抛错
                raw.sqlite3_bind_text(stmt, index, value.ToString() ?? string.Empty);
                break;
        }
    }
}

/// <summary>
/// 自定义的 DbDataReader 实现，支持智能跳过损坏数据块
/// </summary>
internal class QQSqliteDataReader : DbDataReader
{
    private readonly sqlite3 _db;
    private sqlite3_stmt _stmt;
    private readonly string _originalSql;
    private bool _hasRow;
    private bool _closed;
    private int _consecutiveErrors = 0;
    private long _skipJump = 10000;
    private int _totalErrorBlocks = 0;
    
    // 用于智能跳过的状态
    private bool _isRowIdQuery = false;
    private int _rowIdColumnIndex = -1;
    private long _lastSuccessRowId = 0;

    // 接受已预编译并绑定过参数的 stmt
    public QQSqliteDataReader(sqlite3 db, sqlite3_stmt preparedStmt, string sql)
    {
        _db = db;
        _stmt = preparedStmt;
        _originalSql = sql;
    }


    public override int FieldCount => raw.sqlite3_column_count(_stmt);
    public override bool HasRows => _hasRow;
    public override bool IsClosed => _closed;
    public override int RecordsAffected => raw.sqlite3_changes(_db);
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));
    public override int Depth => 0;

    public override bool Read()
    {
        if (_closed) return false;

        int rc = raw.sqlite3_step(_stmt);
        
        if (rc == raw.SQLITE_ROW)
        {
            _hasRow = true;
            _consecutiveErrors = 0; // 重置错误计数
            _skipJump = 10000; // 重置跳跃步长
            
            // 如果是 ROWID 查询，记录最后成功的 ROWID
            if (_isRowIdQuery && _rowIdColumnIndex >= 0 && _rowIdColumnIndex < FieldCount)
            {
                try
                {
                    _lastSuccessRowId = raw.sqlite3_column_int64(_stmt, _rowIdColumnIndex);
                }
                catch { }
            }
            
            return true;
        }
        else if (rc == raw.SQLITE_DONE)
        {
            return false;
        }
        else if (rc == raw.SQLITE_CORRUPT || rc == raw.SQLITE_IOERR)
        {
            var error = raw.sqlite3_errmsg(_db).utf8_to_string();
            Console.WriteLine($"[DataReader] 读取数据失败: {error} (错误码: {rc})");
            return false;
        }
        else
        {
            var error = raw.sqlite3_errmsg(_db).utf8_to_string();
            Console.WriteLine($"[DataReader] 读取数据失败: {error} (错误码: {rc})");
            return false;
        }
    }
    

    public override bool NextResult() => false;

    public override string GetName(int ordinal)
    {
        return raw.sqlite3_column_name(_stmt, ordinal).utf8_to_string();
    }

    public override int GetOrdinal(string name)
    {
        for (int i = 0; i < FieldCount; i++)
        {
            if (GetName(i) == name)
                return i;
        }
        throw new IndexOutOfRangeException($"列 '{name}' 不存在");
    }

    public override string GetDataTypeName(int ordinal)
    {
        return raw.sqlite3_column_decltype(_stmt, ordinal).utf8_to_string() ?? "TEXT";
    }

    public override Type GetFieldType(int ordinal)
    {
        var type = raw.sqlite3_column_type(_stmt, ordinal);
        return type switch
        {
            raw.SQLITE_INTEGER => typeof(long),
            raw.SQLITE_FLOAT => typeof(double),
            raw.SQLITE_TEXT => typeof(string),
            raw.SQLITE_BLOB => typeof(byte[]),
            _ => typeof(object)
        };
    }

    public override object GetValue(int ordinal)
    {
        var type = raw.sqlite3_column_type(_stmt, ordinal);
        return type switch
        {
            raw.SQLITE_INTEGER => raw.sqlite3_column_int64(_stmt, ordinal),
            raw.SQLITE_FLOAT => raw.sqlite3_column_double(_stmt, ordinal),
            raw.SQLITE_TEXT => raw.sqlite3_column_text(_stmt, ordinal).utf8_to_string(),
            raw.SQLITE_BLOB => GetBlobSafe(ordinal),
            raw.SQLITE_NULL => DBNull.Value,
            _ => DBNull.Value
        };
    }

    private byte[] GetBlobSafe(int ordinal)
    {
        try
        {
            var blob = raw.sqlite3_column_blob(_stmt, ordinal);
            var length = raw.sqlite3_column_bytes(_stmt, ordinal);
            
            if (length == 0)
                return Array.Empty<byte>();
            
            return blob.ToArray();
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    public override int GetValues(object[] values)
    {
        int count = Math.Min(values.Length, FieldCount);
        for (int i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }
        return count;
    }

    public override bool IsDBNull(int ordinal)
    {
        return raw.sqlite3_column_type(_stmt, ordinal) == raw.SQLITE_NULL;
    }

    public override bool GetBoolean(int ordinal) => GetInt64(ordinal) != 0;
    public override byte GetByte(int ordinal) => (byte)GetInt64(ordinal);
    public override char GetChar(int ordinal) => (char)GetInt64(ordinal);
    public override DateTime GetDateTime(int ordinal) => DateTime.Parse(GetString(ordinal));
    public override decimal GetDecimal(int ordinal) => (decimal)GetDouble(ordinal);
    public override double GetDouble(int ordinal) => raw.sqlite3_column_double(_stmt, ordinal);
    public override float GetFloat(int ordinal) => (float)GetDouble(ordinal);
    public override Guid GetGuid(int ordinal) => Guid.Parse(GetString(ordinal));
    public override short GetInt16(int ordinal) => (short)GetInt64(ordinal);
    public override int GetInt32(int ordinal) => (int)GetInt64(ordinal);
    public override long GetInt64(int ordinal) => raw.sqlite3_column_int64(_stmt, ordinal);
    public override string GetString(int ordinal) => raw.sqlite3_column_text(_stmt, ordinal).utf8_to_string();

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var bytes = raw.sqlite3_column_blob(_stmt, ordinal).ToArray();
        if (buffer != null)
        {
            Array.Copy(bytes, dataOffset, buffer, bufferOffset, length);
        }
        return bytes.Length;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var str = GetString(ordinal);
        if (buffer != null)
        {
            str.CopyTo((int)dataOffset, buffer, bufferOffset, length);
        }
        return str.Length;
    }

    public override System.Collections.IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    public override void Close()
    {
        if (!_closed)
        {
            raw.sqlite3_finalize(_stmt);
            _closed = true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// 自定义的 DbTransaction 实现
/// </summary>
internal class QQSqliteTransaction : DbTransaction
{
    private QQNTDbConnection? _connection;
    private readonly IsolationLevel _isolationLevel;
    private bool _completed;

    public QQSqliteTransaction(QQNTDbConnection connection, IsolationLevel isolationLevel)
    {
        _connection = connection;
        _isolationLevel = isolationLevel;

        // 开始事务
        var beginCmd = isolationLevel switch
        {
            IsolationLevel.Serializable => "BEGIN EXCLUSIVE TRANSACTION",
            _ => "BEGIN TRANSACTION"
        };

        raw.sqlite3_exec(connection.Handle, beginCmd, null, IntPtr.Zero, out _);
    }

    public override IsolationLevel IsolationLevel => _isolationLevel;

    protected override DbConnection? DbConnection => _connection;

    public override void Commit()
    {
        if (_completed || _connection == null)
            throw new InvalidOperationException("事务已完成或连接已关闭");

        raw.sqlite3_exec(_connection.Handle, "COMMIT TRANSACTION", null, IntPtr.Zero, out _);
        _completed = true;
    }

    public override void Rollback()
    {
        if (_completed || _connection == null)
            throw new InvalidOperationException("事务已完成或连接已关闭");

        raw.sqlite3_exec(_connection.Handle, "ROLLBACK TRANSACTION", null, IntPtr.Zero, out _);
        _completed = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_completed && _connection != null)
        {
            Rollback();
        }
        _connection = null;
        base.Dispose(disposing);
    }
}

