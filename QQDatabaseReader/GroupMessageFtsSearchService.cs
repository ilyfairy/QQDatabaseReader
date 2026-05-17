using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using QQDatabaseReader.Database;

namespace QQDatabaseReader;

public sealed class GroupMessageFtsSearchService
{
    private const int DetailLookupBatchSize = 500;
    private const int GroupFilterScanPageSize = 5000;
    private const int GroupFilterScanMultiplier = 50;
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private readonly QQGroupMessageFtsDbContext _dbContext;
    private readonly object _gate = new();

    public GroupMessageFtsSearchService(QQGroupMessageFtsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public IReadOnlyList<GroupMessageFtsSearchResult> Search(GroupMessageFtsSearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return [];

        var matchQuery = CreateMatchQuery(request.Query);
        var limit = request.Limit is { } requestedLimit
            ? Math.Max(requestedLimit, 1)
            : (int?)null;

        lock (_gate)
        {
            var connection = _dbContext.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
            {
                connection.Open();
            }

            return request.Order == GroupMessageFtsSearchOrder.Newest
                ? SearchNewest(connection, matchQuery, request, limit)
                : SearchByRank(connection, matchQuery, request, limit);
        }
    }

    public long Count(GroupMessageFtsCountRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return 0;

        lock (_gate)
        {
            var connection = _dbContext.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
            {
                connection.Open();
            }

            var matchQuery = CreateMatchQuery(request.Query);
            if (request.GroupId is { } groupId)
                return CountGroupMatches(connection, matchQuery, groupId);

            using var command = connection.CreateCommand();
            command.CommandText = CreateCountSql();
            AddParameter(command, "$query", matchQuery);
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }

    public IReadOnlyList<GroupMessageFtsMatchScanRow> ScanMatches(GroupMessageFtsMatchScanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return [];

        var limit = Math.Max(request.Limit, 1);

        lock (_gate)
        {
            var connection = _dbContext.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
            {
                connection.Open();
            }

            var rowIds = ReadMatchRowIds(
                connection,
                CreateMatchQuery(request.Query),
                request.BeforeRowId,
                limit);
            return LoadMatchScanRowsByRowIds(connection, rowIds, includeUnreadableRows: true);
        }
    }

    public GroupMessageFtsMatchRowIdPage ScanMatchRowIds(GroupMessageFtsMatchRowIdScanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return new GroupMessageFtsMatchRowIdPage([], null, false);

        var limit = Math.Max(request.Limit, 1);

        lock (_gate)
        {
            var connection = _dbContext.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
            {
                connection.Open();
            }

            var rowIds = ReadMatchRowIds(
                connection,
                CreateMatchQuery(request.Query),
                request.BeforeRowId,
                limit);
            return new GroupMessageFtsMatchRowIdPage(
                rowIds,
                rowIds.LastOrDefault(),
                rowIds.Count == limit);
        }
    }

    private static IReadOnlyList<GroupMessageFtsSearchResult> SearchNewest(
        DbConnection connection,
        string matchQuery,
        GroupMessageFtsSearchRequest request,
        int? limit)
    {
        if (limit is null)
        {
            var rowIds = ReadAllMatchRowIds(connection, matchQuery, request.BeforeRowId);
            return LoadSearchResultsByRowIds(connection, rowIds, includeUnreadableRows: true);
        }

        if (request.GroupId is null)
        {
            var rowIds = ReadMatchRowIds(connection, matchQuery, request.BeforeRowId, limit.Value);
            return LoadSearchResultsByRowIds(connection, rowIds, includeUnreadableRows: true);
        }

        var results = new List<GroupMessageFtsSearchResult>(limit.Value);
        var beforeRowId = request.BeforeRowId;
        var scanLimit = Math.Max(GroupFilterScanPageSize, limit.Value * GroupFilterScanMultiplier);
        while (results.Count < limit.Value)
        {
            var rowIds = ReadMatchRowIds(connection, matchQuery, beforeRowId, scanLimit);
            if (rowIds.Count == 0)
                break;

            var rows = LoadSearchResultsByRowIds(connection, rowIds, includeUnreadableRows: true);
            foreach (var row in rows)
            {
                if (!IsGroupMatch(row, request.GroupId.Value))
                    continue;

                results.Add(row);
                if (results.Count == limit.Value)
                    break;
            }

            if (results.Count == limit.Value || rowIds.Count < scanLimit)
                break;

            beforeRowId = rowIds[^1];
        }

        return results;
    }

    private static IReadOnlyList<GroupMessageFtsSearchResult> SearchByRank(
        DbConnection connection,
        string matchQuery,
        GroupMessageFtsSearchRequest request,
        int? limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = CreateRankSearchSql(request);
        AddParameter(command, "$query", matchQuery);
        if (limit is not null)
        {
            AddParameter(command, "$limit", limit.Value);
        }

        if (request.GroupId is { } groupId)
        {
            AddParameter(command, "$groupId", ToSqliteUInt32BitPattern(groupId));
            AddParameter(command, "$groupIdText", groupId.ToString());
        }

        if (request.BeforeRowId is { } beforeRowId)
        {
            AddParameter(command, "$beforeRowId", beforeRowId);
        }

        using var reader = command.ExecuteReader();
        var results = limit is { } capacity
            ? new List<GroupMessageFtsSearchResult>(capacity)
            : new List<GroupMessageFtsSearchResult>();
        while (reader.Read())
        {
            results.Add(ReadSearchResult(reader, rankOrdinal: 15));
        }

        return results;
    }

    private static IReadOnlyList<long> ReadAllMatchRowIds(
        DbConnection connection,
        string matchQuery,
        long? beforeRowId)
    {
        var results = new List<long>();
        while (true)
        {
            var rowIds = ReadMatchRowIds(connection, matchQuery, beforeRowId, GroupFilterScanPageSize);
            if (rowIds.Count == 0)
                break;

            results.AddRange(rowIds);
            beforeRowId = rowIds[^1];
            if (rowIds.Count < GroupFilterScanPageSize)
                break;
        }

        return results;
    }

    private static IReadOnlyList<long> ReadMatchRowIds(
        DbConnection connection,
        string matchQuery,
        long? beforeRowId,
        int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = CreateRowIdSearchSql(beforeRowId is not null);
        AddParameter(command, "$query", matchQuery);
        AddParameter(command, "$limit", limit);
        if (beforeRowId is not null)
        {
            AddParameter(command, "$beforeRowId", beforeRowId.Value);
        }

        using var reader = command.ExecuteReader();
        var results = new List<long>(limit);
        while (reader.Read())
        {
            results.Add(ReadInt64(reader, 0));
        }

        return results;
    }

    private static IReadOnlyList<GroupMessageFtsSearchResult> LoadSearchResultsByRowIds(
        DbConnection connection,
        IReadOnlyList<long> rowIds,
        bool includeUnreadableRows)
    {
        var rowMap = new Dictionary<long, GroupMessageFtsSearchResult>();
        foreach (var batch in rowIds.Chunk(DetailLookupBatchSize))
        {
            ReadSearchResultBatch(connection, batch, rowMap);
            if (batch.Any(rowId => !rowMap.ContainsKey(rowId)))
            {
                RepairMissingSearchResultRows(connection, batch, rowMap);
            }
        }

        var results = new List<GroupMessageFtsSearchResult>(rowIds.Count);
        foreach (var rowId in rowIds)
        {
            if (rowMap.TryGetValue(rowId, out var result))
            {
                results.Add(result);
                continue;
            }

            if (includeUnreadableRows)
                results.Add(CreateUnreadableSearchResult(rowId));
        }

        return results;
    }

    private static IReadOnlyList<GroupMessageFtsMatchScanRow> LoadMatchScanRowsByRowIds(
        DbConnection connection,
        IReadOnlyList<long> rowIds,
        bool includeUnreadableRows)
    {
        var rowMap = new Dictionary<long, GroupMessageFtsMatchScanRow>();
        foreach (var batch in rowIds.Chunk(DetailLookupBatchSize))
        {
            ReadMatchScanBatch(connection, batch, rowMap);
            if (batch.Any(rowId => !rowMap.ContainsKey(rowId)))
            {
                RepairMissingMatchScanRows(connection, batch, rowMap);
            }
        }

        var results = new List<GroupMessageFtsMatchScanRow>(rowIds.Count);
        foreach (var rowId in rowIds)
        {
            if (rowMap.TryGetValue(rowId, out var row))
            {
                results.Add(row);
                continue;
            }

            if (includeUnreadableRows)
                results.Add(CreateUnreadableMatchScanRow(rowId));
        }

        return results;
    }

    private static void ReadSearchResultBatch(
        DbConnection connection,
        IReadOnlyList<long> rowIds,
        Dictionary<long, GroupMessageFtsSearchResult> rowMap)
    {
        using var command = connection.CreateCommand();
        command.CommandText = CreateDetailLookupSql(rowIds.Count);
        AddRowIdParameters(command, rowIds);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var result = ReadSearchResult(reader, rankOrdinal: null);
            rowMap[result.RowId] = result;
        }
    }

    private static void RepairMissingSearchResultRows(
        DbConnection connection,
        IReadOnlyList<long> rowIds,
        Dictionary<long, GroupMessageFtsSearchResult> rowMap)
    {
        foreach (var rowId in rowIds)
        {
            if (rowMap.ContainsKey(rowId))
                continue;

            ReadSearchResultBatch(connection, [rowId], rowMap);
        }
    }

    private static void ReadMatchScanBatch(
        DbConnection connection,
        IReadOnlyList<long> rowIds,
        Dictionary<long, GroupMessageFtsMatchScanRow> rowMap)
    {
        using var command = connection.CreateCommand();
        command.CommandText = CreateGroupLookupSql(rowIds.Count);
        AddRowIdParameters(command, rowIds);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new GroupMessageFtsMatchScanRow
            {
                RowId = ReadInt64(reader, 0),
                PeerUid = ReadString(reader, 1),
                GroupId = ReadUInt32BitPattern(reader, 2),
            };
            rowMap[row.RowId] = row;
        }
    }

    private static void RepairMissingMatchScanRows(
        DbConnection connection,
        IReadOnlyList<long> rowIds,
        Dictionary<long, GroupMessageFtsMatchScanRow> rowMap)
    {
        foreach (var rowId in rowIds)
        {
            if (rowMap.ContainsKey(rowId))
                continue;

            ReadMatchScanBatch(connection, [rowId], rowMap);
        }
    }

    private static long CountGroupMatches(
        DbConnection connection,
        string matchQuery,
        uint groupId)
    {
        long count = 0;
        long? beforeRowId = null;
        while (true)
        {
            var rowIds = ReadMatchRowIds(connection, matchQuery, beforeRowId, GroupFilterScanPageSize);
            if (rowIds.Count == 0)
                break;

            foreach (var row in LoadMatchScanRowsByRowIds(connection, rowIds, includeUnreadableRows: false))
            {
                if (IsGroupMatch(row, groupId))
                    count++;
            }

            beforeRowId = rowIds[^1];
            if (rowIds.Count < GroupFilterScanPageSize)
                break;
        }

        return count;
    }

    private static bool IsGroupMatch(GroupMessageFtsSearchResult result, uint groupId)
    {
        return result.GroupId == groupId ||
            string.Equals(result.PeerUid, groupId.ToString(), StringComparison.Ordinal);
    }

    private static bool IsGroupMatch(GroupMessageFtsMatchScanRow result, uint groupId)
    {
        return result.GroupId == groupId ||
            string.Equals(result.PeerUid, groupId.ToString(), StringComparison.Ordinal);
    }

    private static GroupMessageFtsSearchResult ReadSearchResult(DbDataReader reader, int? rankOrdinal)
    {
        var texts = new[]
        {
            ReadString(reader, 8),
            ReadString(reader, 9),
            ReadString(reader, 10),
            ReadString(reader, 11),
            ReadString(reader, 12),
            ReadString(reader, 13),
            ReadString(reader, 14),
        };
        var previewTexts = GroupMessageFtsText.GetPreviewFields(texts);

        return new GroupMessageFtsSearchResult
        {
            RowId = ReadInt64(reader, 0),
            MessageId = ReadInt64(reader, 1),
            MessageSeq = ReadInt64(reader, 2),
            MessageTime = ReadInt32(reader, 3),
            ChatType = (ChatType)ReadInt32(reader, 4),
            PeerUid = ReadString(reader, 5),
            GroupId = ReadUInt32BitPattern(reader, 6),
            SenderUid = ReadString(reader, 7),
            TextFields = previewTexts.Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value!).ToArray(),
            PreviewText = GroupMessageFtsText.Combine(previewTexts.ToArray()),
            Rank = rankOrdinal is { } ordinal ? ReadDouble(reader, ordinal) : 0,
        };
    }

    private static GroupMessageFtsSearchResult CreateUnreadableSearchResult(long rowId)
    {
        return new GroupMessageFtsSearchResult
        {
            RowId = rowId,
            PreviewText = "[搜索命中，但消息详情读取失败]",
            IsUnreadable = true,
        };
    }

    private static GroupMessageFtsMatchScanRow CreateUnreadableMatchScanRow(long rowId)
    {
        return new GroupMessageFtsMatchScanRow
        {
            RowId = rowId,
            IsUnreadable = true,
        };
    }

    private static string CreateRowIdSearchSql(bool hasBeforeRowId)
    {
        var sql = new StringBuilder("""
SELECT rowid
FROM group_msg_fts_fts
WHERE group_msg_fts_fts MATCH $query

""");

        if (hasBeforeRowId)
        {
            sql.AppendLine("AND rowid < $beforeRowId");
        }

        // FTS 虚表只负责从倒排索引定位 rowid，不在这里读取外部内容列。
        // 部分 QQNT 数据库在 FTS 虚表回读 [40021]/[40027]/正文列时会触发
        // SQLITE_CORRUPT；先拿 rowid 再回普通表取详情可以避开这个查询路径。
        sql.AppendLine("ORDER BY rowid DESC");
        sql.AppendLine("LIMIT $limit");
        return sql.ToString();
    }

    private static string CreateDetailLookupSql(int count)
    {
        return $"""
SELECT
    rowid,
    [40001],
    [40003],
    [40050],
    [40010],
    [40021],
    [40027],
    [40020],
    [41701],
    [41702],
    [41703],
    [41704],
    [41705],
    [41706],
    [41707]
FROM group_msg_fts
WHERE rowid IN ({CreateRowIdParameterList(count)})
""";
    }

    private static string CreateGroupLookupSql(int count)
    {
        return $"""
SELECT rowid, [40021], [40027]
FROM group_msg_fts
WHERE rowid IN ({CreateRowIdParameterList(count)})
""";
    }

    private static string CreateRowIdParameterList(int count)
    {
        return string.Join(", ", Enumerable.Range(0, count).Select(static index => "$rowid" + index));
    }

    private static void AddRowIdParameters(DbCommand command, IReadOnlyList<long> rowIds)
    {
        for (var i = 0; i < rowIds.Count; i++)
        {
            AddParameter(command, "$rowid" + i, rowIds[i]);
        }
    }

    private static string CreateRankSearchSql(GroupMessageFtsSearchRequest request)
    {
        var sql = new StringBuilder("""
SELECT
    rowid,
    [40001],
    [40003],
    [40050],
    [40010],
    [40021],
    [40027],
    [40020],
    [41701],
    [41702],
    [41703],
    [41704],
    [41705],
    [41706],
    [41707],
    rank
FROM group_msg_fts_fts
WHERE group_msg_fts_fts MATCH $query

""");

        if (request.GroupId is not null)
        {
            sql.AppendLine("AND ([40027] = $groupId OR [40021] = $groupIdText)");
        }

        if (request.BeforeRowId is not null)
        {
            sql.AppendLine("AND rowid < $beforeRowId");
        }

        sql.AppendLine("ORDER BY rank");
        if (request.Limit is not null)
        {
            sql.AppendLine("LIMIT $limit");
        }

        return sql.ToString();
    }

    private static string CreateCountSql()
    {
        return """
SELECT count(*)
FROM group_msg_fts_fts
WHERE group_msg_fts_fts MATCH $query

""";
    }

    private static string CreateMatchQuery(string query)
    {
        var terms = WhitespaceRegex.Split(query.Trim())
            .Where(static value => value.Length > 0)
            .Select(static value => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")
            .ToArray();

        return terms.Length == 0
            ? "\"\""
            : string.Join(" ", terms);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static int ToSqliteUInt32BitPattern(uint value)
    {
        return unchecked((int)value);
    }

    private static long ReadInt64(DbDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? 0 : reader.GetInt64(ordinal);
    }

    private static int ReadInt32(DbDataReader reader, int ordinal)
    {
        return unchecked((int)ReadInt64(reader, ordinal));
    }

    private static uint ReadUInt32BitPattern(DbDataReader reader, int ordinal)
    {
        return unchecked((uint)ReadInt32(reader, ordinal));
    }

    private static double ReadDouble(DbDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? 0 : reader.GetDouble(ordinal);
    }

    private static string? ReadString(DbDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}

public sealed record GroupMessageFtsSearchRequest(
    string Query,
    uint? GroupId = null,
    int? Limit = null,
    GroupMessageFtsSearchOrder Order = GroupMessageFtsSearchOrder.Relevance,
    long? BeforeRowId = null);

public sealed record GroupMessageFtsCountRequest(
    string Query,
    uint? GroupId = null);

public sealed record GroupMessageFtsMatchScanRequest(
    string Query,
    int Limit,
    long? BeforeRowId = null);

public sealed record GroupMessageFtsMatchRowIdScanRequest(
    string Query,
    int Limit,
    long? BeforeRowId = null);

public sealed record GroupMessageFtsMatchRowIdPage(
    IReadOnlyList<long> RowIds,
    long? NextBeforeRowId,
    bool HasMore);

public enum GroupMessageFtsSearchOrder
{
    Relevance,
    Newest,
}

public sealed class GroupMessageFtsSearchResult
{
    public long RowId { get; init; }
    public long MessageId { get; init; }
    public long MessageSeq { get; init; }
    public int MessageTime { get; init; }
    public ChatType ChatType { get; init; }
    public string? PeerUid { get; init; }
    public uint GroupId { get; init; }
    public string? SenderUid { get; init; }
    public IReadOnlyList<string> TextFields { get; init; } = [];
    public string PreviewText { get; init; } = string.Empty;
    public double Rank { get; init; }
    public bool IsUnreadable { get; init; }
}

public sealed class GroupMessageFtsMatchScanRow
{
    public long RowId { get; init; }
    public string? PeerUid { get; init; }
    public uint GroupId { get; init; }
    public bool IsUnreadable { get; init; }
}
