using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using SQLitePCL;

namespace QQDatabaseReader.Database;

/// <summary>
/// 打开的数据库里实际存在的表和列. 旧版 QQNT 缺少不少新列和新表,
/// EF 模型按它映射: 缺的表换成 <see cref="LegacyTableAttribute"/> 里的旧表, 缺的列用 <see cref="LegacyColumnAttribute"/> 的表达式或默认值补上
/// </summary>
public sealed class DatabaseSchema
{
    private readonly Dictionary<string, HashSet<string>> _tables;

    private DatabaseSchema(Dictionary<string, HashSet<string>> tables)
    {
        _tables = tables;
        var builder = new StringBuilder();
        foreach (var (table, columns) in tables.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(table).Append(':');
            builder.AppendJoin(',', columns.Order(StringComparer.OrdinalIgnoreCase));
            builder.Append(';');
        }

        Signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>表结构的指纹, 结构相同的库共用一个 EF 模型</summary>
    public string Signature { get; }

    /// <summary>只读 sqlite_master 和各表的列定义, 不读数据. 跳过虚表 (FTS 需要先注册分词器才能读列)</summary>
    public static DatabaseSchema Read(sqlite3 database)
    {
        const string sql = """
            SELECT m.name, p.name
            FROM sqlite_master AS m, pragma_table_info(m.name) AS p
            WHERE m.type = 'table' AND m.sql NOT LIKE 'CREATE VIRTUAL TABLE%'
            """;
        var tables = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var rc = raw.sqlite3_prepare_v2(database, sql, out var statement);
        if (rc != raw.SQLITE_OK)
            throw new InvalidOperationException($"读取表结构失败: {raw.sqlite3_errmsg(database).utf8_to_string()}");

        try
        {
            while ((rc = raw.sqlite3_step(statement)) == raw.SQLITE_ROW)
            {
                var table = raw.sqlite3_column_text(statement, 0).utf8_to_string();
                var column = raw.sqlite3_column_text(statement, 1).utf8_to_string();
                if (!tables.TryGetValue(table, out var columns))
                    tables[table] = columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                columns.Add(column);
            }

            if (rc != raw.SQLITE_DONE)
                throw new InvalidOperationException($"读取表结构失败: {raw.sqlite3_errmsg(database).utf8_to_string()}");
        }
        finally
        {
            raw.sqlite3_finalize(statement);
        }

        return new DatabaseSchema(tables);
    }

    public bool HasTable(string table) => _tables.ContainsKey(table);

    public bool HasColumn(string table, string column) =>
        _tables.TryGetValue(table, out var columns) && columns.Contains(column);

    /// <summary>实体实际对应的表: 优先 [Table], 不存在时依次尝试 [LegacyTable]; 都不存在返回 null</summary>
    public string? ResolveTable(Type entityType)
    {
        var table = entityType.GetCustomAttribute<TableAttribute>()?.Name ?? entityType.Name;
        return ResolveTable(table, entityType);
    }

    public string? ResolveTable<TEntity>() => ResolveTable(typeof(TEntity));

    /// <summary>
    /// 裸 SQL 里读取实体某一列的写法: 列存在时是 [列名], 缺列时是兼容表达式.
    /// 只能用在 SELECT 里, 不要用在 INDEXED BY 依赖的 WHERE 条件上
    /// </summary>
    public string ColumnSql<TEntity>(string column)
    {
        var table = ResolveTable<TEntity>();
        var property = typeof(TEntity).GetProperties()
            .FirstOrDefault(property => string.Equals(property.GetCustomAttribute<ColumnAttribute>()?.Name, column, StringComparison.Ordinal));
        var isLegacyTable = table is not null &&
                            !string.Equals(table, typeof(TEntity).GetCustomAttribute<TableAttribute>()?.Name, StringComparison.Ordinal);
        if (table is not null && HasColumn(table, column) &&
            !(isLegacyTable && property?.GetCustomAttribute<LegacyColumnAttribute>() is { OnLegacyTable: true }))
        {
            return QuoteIdentifier(column);
        }

        return property is null
            ? "NULL"
            : FallbackSql(property, property.PropertyType, isNullable: !property.PropertyType.IsValueType || Nullable.GetUnderlyingType(property.PropertyType) is not null);
    }

    /// <summary>
    /// 在 OnModelCreating 末尾调用: 让每个实体对应到实际存在的表, 缺列时改成映射到补全了缺失列的查询
    /// </summary>
    public void Apply(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var table = entityType.GetTableName();
            if (table is null)
                continue;

            var actualTable = ResolveTable(table, entityType.ClrType);
            if (actualTable is null)
                continue;

            var isLegacyTable = !string.Equals(actualTable, table, StringComparison.Ordinal);
            var replaced = entityType.GetProperties()
                .Select(property => (Property: property, Column: property.GetColumnName()))
                .Where(item => !HasColumn(actualTable, item.Column) ||
                               isLegacyTable && item.Property.PropertyInfo?.GetCustomAttribute<LegacyColumnAttribute>() is { OnLegacyTable: true })
                .ToList();
            if (replaced.Count == 0)
            {
                if (isLegacyTable)
                    entityType.SetTableName(actualTable);
                continue;
            }

            // 列逐个写出, 补上的列不会和原表同名列重复. SQLite 会把这层子查询展开 (flatten), WHERE 和 ORDER BY 照样走原表的索引
            var replacedColumns = replaced.Select(static item => item.Column).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var columns = _tables[actualTable]
                .Where(column => !replacedColumns.Contains(column))
                .Select(QuoteIdentifier)
                .Concat(replaced.Select(item =>
                    $"{FallbackSql(item.Property.PropertyInfo, item.Property.ClrType, item.Property.IsNullable)} AS {QuoteIdentifier(item.Column)}"));
            entityType.SetTableName(null);
            entityType.SetSqlQuery($"SELECT {string.Join(", ", columns)} FROM {QuoteIdentifier(actualTable)}");
        }
    }

    private string? ResolveTable(string table, Type entityType)
    {
        if (HasTable(table))
            return table;

        return entityType.GetCustomAttribute<LegacyTableAttribute>()?.Tables.FirstOrDefault(HasTable);
    }

    private static string FallbackSql(PropertyInfo? property, Type clrType, bool isNullable)
    {
        if (property?.GetCustomAttribute<LegacyColumnAttribute>() is { } legacy)
            return legacy.Sql;

        if (isNullable)
            return "NULL";

        return clrType == typeof(string) ? "''" : clrType == typeof(byte[]) ? "X''" : "0";
    }

    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

/// <summary>[Table] 指定的表不存在时 (旧版 QQNT), 依次尝试这些表</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class LegacyTableAttribute(params string[] tables) : Attribute
{
    public IReadOnlyList<string> Tables { get; } = tables;
}

/// <summary>列不存在时 (旧版 QQNT) 用这个 SQL 表达式代替, 可以引用同一行的其它列和 <see cref="QQNtUinFunction"/></summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class LegacyColumnAttribute(string sql) : Attribute
{
    public string Sql { get; } = sql;

    /// <summary>实体映射到 [LegacyTable] 旧表时, 即使列存在也用表达式 (旧表里有这一列但没有值)</summary>
    public bool OnLegacyTable { get; init; }
}

/// <summary>按表结构区分 EF 模型缓存, 同一个 DbContext 类型可以同时打开新旧两种库</summary>
public interface ISchemaMappedDbContext
{
    DatabaseSchema? Schema { get; }
}

internal sealed class SchemaModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        (context.GetType(), (context as ISchemaMappedDbContext)?.Schema?.Signature, designTime);
}

public static class DatabaseSchemaDbContextOptionsExtensions
{
    public static DbContextOptionsBuilder<TContext> UseSchemaMappedModel<TContext>(this DbContextOptionsBuilder<TContext> builder)
        where TContext : DbContext
    {
        builder.ReplaceService<IModelCacheKeyFactory, SchemaModelCacheKeyFactory>();
        return builder;
    }
}
