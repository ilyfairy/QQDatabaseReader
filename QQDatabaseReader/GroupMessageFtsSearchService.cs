using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QQDatabaseReader.Database;

namespace QQDatabaseReader;

public sealed class GroupMessageFtsSearchService
{
    private const string DefaultContentTableName = "group_msg_fts";
    private const int DetailLookupBatchSize = 500;
    private const int GroupFilterScanPageSize = 5000;
    private const int GroupFilterScanMultiplier = 50;
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private readonly QQGroupMessageFtsDbContext _dbContext;
    private readonly string _contentTableName;
    private readonly string _ftsTableName;
    private readonly object _gate = new();

    public GroupMessageFtsSearchService(
        QQGroupMessageFtsDbContext dbContext,
        string contentTableName = DefaultContentTableName)
    {
        _dbContext = dbContext;
        _contentTableName = ValidateTableName(contentTableName);
        _ftsTableName = _contentTableName + "_fts";
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

    public GroupMessageFtsSearchPage SearchPage(
        GroupMessageFtsSearchRequest request,
        int maxScanRows,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return new GroupMessageFtsSearchPage([], null, false);
        if (request.Order != GroupMessageFtsSearchOrder.Newest)
            throw new ArgumentException("Bounded FTS paging requires newest order.", nameof(request));
        if (maxScanRows <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxScanRows));

        var limit = Math.Max(request.Limit ?? 100, 1);
        var matchQuery = CreateMatchQuery(request.Query);
        try
        {
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var connection = _dbContext.Database.GetDbConnection();
                if (connection.State != ConnectionState.Open)
                    connection.Open();

                var scanLimit = request.HasConversationFilter
                    ? maxScanRows
                    : Math.Min(limit, maxScanRows);
                var rowIds = ReadMatchRowIds(
                    connection,
                    matchQuery,
                    request.BeforeRowId,
                    scanLimit,
                    cancellationToken);
                if (rowIds.Count == 0)
                    return new GroupMessageFtsSearchPage([], null, false);

                var rows = LoadSearchResultsByRowIds(
                    connection,
                    rowIds,
                    includeUnreadableRows: true,
                    cancellationToken: cancellationToken);
                var results = new List<GroupMessageFtsSearchResult>(Math.Min(limit, rows.Count));
                var examinedCount = 0;
                foreach (var row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    examinedCount++;
                    if (!request.HasConversationFilter || IsConversationMatch(row, request))
                        results.Add(row);
                    if (results.Count == limit)
                        break;
                }

                var nextBeforeRowId = rowIds[examinedCount - 1];
                var hasMore = examinedCount < rowIds.Count || rowIds.Count == scanLimit;
                return new GroupMessageFtsSearchPage(results, nextBeforeRowId, hasMore);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            cancellationToken.IsCancellationRequested &&
            IsSqliteInterrupt(exception))
        {
            throw new OperationCanceledException(
                "QQNT FTS message search was canceled.",
                exception,
                cancellationToken);
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
            if (request.HasConversationFilter)
                return CountConversationMatches(
                    connection,
                    matchQuery,
                    request.GroupId,
                    request.PeerUin,
                    request.PeerUid,
                    request.PrivateConversationIds);

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

    private IReadOnlyList<GroupMessageFtsSearchResult> SearchNewest(
        DbConnection connection,
        string matchQuery,
        GroupMessageFtsSearchRequest request,
        int? limit)
    {
        if (limit is null)
        {
            var rowIds = ReadAllMatchRowIds(connection, matchQuery, request.BeforeRowId);
            var rows = LoadSearchResultsByRowIds(connection, rowIds, includeUnreadableRows: true);
            return request.HasConversationFilter
                ? rows.Where(result => IsConversationMatch(result, request)).ToArray()
                : rows;
        }

        if (!request.HasConversationFilter)
        {
            var rowIds = ReadMatchRowIds(connection, matchQuery, request.BeforeRowId, limit.Value);
            return LoadSearchResultsByRowIds(connection, rowIds, includeUnreadableRows: true);
        }

        var scanLimit = Math.Max(GroupFilterScanPageSize, limit.Value * GroupFilterScanMultiplier);
        var filteredRowIds = ReadMatchRowIds(connection, matchQuery, request.BeforeRowId, scanLimit);
        return LoadSearchResultsByRowIds(connection, filteredRowIds, includeUnreadableRows: true)
            .Where(result => IsConversationMatch(result, request))
            .Take(limit.Value)
            .ToArray();
    }

    private IReadOnlyList<GroupMessageFtsSearchResult> SearchByRank(
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

        AddConversationFilterParameters(command, request);

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

    private IReadOnlyList<long> ReadAllMatchRowIds(
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

    private IReadOnlyList<long> ReadMatchRowIds(
        DbConnection connection,
        string matchQuery,
        long? beforeRowId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = connection.CreateCommand();
        command.CommandText = CreateRowIdSearchSql(beforeRowId is not null);
        AddParameter(command, "$query", matchQuery);
        AddParameter(command, "$limit", limit);
        if (beforeRowId is not null)
        {
            AddParameter(command, "$beforeRowId", beforeRowId.Value);
        }

        using var cancellationRegistration = cancellationToken.Register(
            static state => TryCancelCommand((DbCommand)state!),
            command);
        using var reader = command.ExecuteReader();
        var results = new List<long>(limit);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(ReadInt64(reader, 0));
        }

        return results;
    }

    private IReadOnlyList<GroupMessageFtsSearchResult> LoadSearchResultsByRowIds(
        DbConnection connection,
        IReadOnlyList<long> rowIds,
        bool includeUnreadableRows,
        CancellationToken cancellationToken = default)
    {
        var rowMap = new Dictionary<long, GroupMessageFtsSearchResult>();
        foreach (var batch in rowIds.Chunk(DetailLookupBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadSearchResultBatch(connection, batch, rowMap, cancellationToken);
            if (batch.Any(rowId => !rowMap.ContainsKey(rowId)))
            {
                RepairMissingSearchResultRows(connection, batch, rowMap, cancellationToken);
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

    private IReadOnlyList<GroupMessageFtsMatchScanRow> LoadMatchScanRowsByRowIds(
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

    private void ReadSearchResultBatch(
        DbConnection connection,
        IReadOnlyList<long> rowIds,
        Dictionary<long, GroupMessageFtsSearchResult> rowMap,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = connection.CreateCommand();
        command.CommandText = CreateDetailLookupSql(rowIds.Count);
        AddRowIdParameters(command, rowIds);

        using var cancellationRegistration = cancellationToken.Register(
            static state => TryCancelCommand((DbCommand)state!),
            command);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = ReadSearchResult(reader, rankOrdinal: null);
            rowMap[result.RowId] = result;
        }
    }

    private void RepairMissingSearchResultRows(
        DbConnection connection,
        IReadOnlyList<long> rowIds,
        Dictionary<long, GroupMessageFtsSearchResult> rowMap,
        CancellationToken cancellationToken = default)
    {
        foreach (var rowId in rowIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rowMap.ContainsKey(rowId))
                continue;

            ReadSearchResultBatch(connection, [rowId], rowMap, cancellationToken);
        }
    }

    private void ReadMatchScanBatch(
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
                GroupId = ReadUInt32(reader, 2),
                PrivateConversationId = ReadInt64(reader, 2),
                ChatType = (ChatType)ReadInt32(reader, 3),
            };
            rowMap[row.RowId] = row;
        }
    }

    private void RepairMissingMatchScanRows(
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

    private long CountConversationMatches(
        DbConnection connection,
        string matchQuery,
        uint? groupId,
        uint? peerUin,
        string? peerUid,
        IReadOnlyList<long>? privateConversationIds)
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
                if (IsConversationMatch(row, groupId, peerUin, peerUid, privateConversationIds))
                    count++;
            }

            beforeRowId = rowIds[^1];
            if (rowIds.Count < GroupFilterScanPageSize)
                break;
        }

        return count;
    }

    private static bool IsConversationMatch(GroupMessageFtsSearchResult result, GroupMessageFtsSearchRequest request)
    {
        return IsConversationMatch(
            result.ChatType,
            result.GroupId,
            result.PrivateConversationId,
            result.PeerUid,
            request.GroupId,
            request.PeerUin,
            request.PeerUid,
            request.PrivateConversationIds);
    }

    private static bool IsConversationMatch(
        GroupMessageFtsMatchScanRow result,
        uint? groupId,
        uint? peerUin,
        string? peerUid,
        IReadOnlyList<long>? privateConversationIds)
    {
        return IsConversationMatch(
            result.ChatType,
            result.GroupId,
            result.PrivateConversationId,
            result.PeerUid,
            groupId,
            peerUin,
            peerUid,
            privateConversationIds);
    }

    private static bool IsConversationMatch(
        ChatType chatType,
        uint groupId,
        long privateConversationId,
        string? peerUid,
        uint? requestedGroupId,
        uint? requestedPeerUin,
        string? requestedPeerUid,
        IReadOnlyList<long>? requestedPrivateConversationIds)
    {
        if (requestedGroupId is { } groupFilter &&
            chatType == ChatType.GroupMessage &&
            (groupId == groupFilter || string.Equals(peerUid, groupFilter.ToString(), StringComparison.Ordinal)))
        {
            return true;
        }

        if (chatType != ChatType.PrivateMessage)
            return false;

        if (requestedPrivateConversationIds is { Count: > 0 } conversationIds &&
            conversationIds.Contains(privateConversationId))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(requestedPeerUid) &&
            string.Equals(peerUid, requestedPeerUid, StringComparison.Ordinal))
        {
            return true;
        }

        return requestedPeerUin is { } peerFilter &&
            string.Equals(peerUid, peerFilter.ToString(), StringComparison.Ordinal);
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
            GroupId = ReadUInt32(reader, 6),
            PrivateConversationId = ReadInt64(reader, 6),
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

    private string CreateRowIdSearchSql(bool hasBeforeRowId)
    {
        var sql = new StringBuilder($"""
SELECT rowid
FROM {_ftsTableName}
WHERE {_ftsTableName} MATCH $query

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

    private string CreateDetailLookupSql(int count)
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
FROM {_contentTableName}
WHERE rowid IN ({CreateRowIdParameterList(count)})
""";
    }

    private string CreateGroupLookupSql(int count)
    {
        return $"""
SELECT rowid, [40021], [40027]
       , [40010]
FROM {_contentTableName}
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

    private string CreateRankSearchSql(GroupMessageFtsSearchRequest request)
    {
        var sql = new StringBuilder($"""
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
FROM {_ftsTableName}
WHERE {_ftsTableName} MATCH $query

""");

        var conversationFilter = CreateRankConversationFilterSql(request);
        if (!string.IsNullOrWhiteSpace(conversationFilter))
            sql.AppendLine("AND " + conversationFilter);

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

    private static string CreateRankConversationFilterSql(GroupMessageFtsSearchRequest request)
    {
        var filters = new List<string>();
        if (request.GroupId is not null)
        {
            filters.Add("([40010] = $groupChatType AND ([40027] = $groupId OR [40021] = $groupIdText))");
        }

        if (request.PeerUin is not null)
        {
            filters.Add("([40010] = $privateChatType AND [40021] = $peerUinText)");
        }

        if (!string.IsNullOrWhiteSpace(request.PeerUid))
        {
            filters.Add("([40010] = $privateChatType AND [40021] = $peerUid)");
        }

        if (request.PrivateConversationIds is { Count: > 0 } conversationIds)
        {
            var parameters = Enumerable.Range(0, conversationIds.Count)
                .Select(static index => "$privateConversationId" + index);
            filters.Add($"([40010] = $privateChatType AND [40027] IN ({string.Join(", ", parameters)}))");
        }

        return filters.Count switch
        {
            0 => string.Empty,
            1 => filters[0],
            _ => "(" + string.Join(" OR ", filters) + ")",
        };
    }

    private string CreateCountSql()
    {
        return $"""
SELECT count(*)
FROM {_ftsTableName}
WHERE {_ftsTableName} MATCH $query

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

    private static string ValidateTableName(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("FTS table name cannot be empty.", nameof(tableName));

        foreach (var item in tableName)
        {
            if (item is not (>= 'a' and <= 'z') &&
                item is not (>= 'A' and <= 'Z') &&
                item is not (>= '0' and <= '9') &&
                item != '_')
            {
                throw new ArgumentException($"Invalid FTS table name: {tableName}", nameof(tableName));
            }
        }

        return tableName;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void TryCancelCommand(DbCommand command)
    {
        try
        {
            command.Cancel();
        }
        catch
        {
        }
    }

    private static bool IsSqliteInterrupt(Exception exception)
    {
        return (exception is SqliteException sqliteException &&
                sqliteException.SqliteErrorCode == SQLitePCL.raw.SQLITE_INTERRUPT) ||
               (exception is InvalidOperationException &&
                exception.Message.Contains(
                    $"code {SQLitePCL.raw.SQLITE_INTERRUPT}",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static void AddConversationFilterParameters(DbCommand command, GroupMessageFtsSearchRequest request)
    {
        if (!request.HasConversationFilter)
            return;

        if (request.GroupId is { } groupId)
        {
            AddParameter(command, "$groupChatType", (int)ChatType.GroupMessage);
            AddParameter(command, "$groupId", ToSqliteUInt32(groupId));
            AddParameter(command, "$groupIdText", groupId.ToString());
        }

        if (request.PeerUin is not null ||
            !string.IsNullOrWhiteSpace(request.PeerUid) ||
            request.PrivateConversationIds is { Count: > 0 })
        {
            AddParameter(command, "$privateChatType", (int)ChatType.PrivateMessage);
        }

        if (request.PeerUin is { } peerUin)
        {
            AddParameter(command, "$peerUinText", peerUin.ToString());
        }

        if (!string.IsNullOrWhiteSpace(request.PeerUid))
        {
            AddParameter(command, "$peerUid", request.PeerUid);
        }

        if (request.PrivateConversationIds is not { Count: > 0 } conversationIds)
            return;

        for (var index = 0; index < conversationIds.Count; index++)
        {
            AddParameter(command, "$privateConversationId" + index, conversationIds[index]);
        }
    }

    // QQ 群号按正数存, 大于 2^31 的也是; 不能按位转成 int
    private static long ToSqliteUInt32(uint value)
    {
        return value;
    }

    private static long ReadInt64(DbDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? 0 : reader.GetInt64(ordinal);
    }

    private static int ReadInt32(DbDataReader reader, int ordinal)
    {
        return unchecked((int)ReadInt64(reader, ordinal));
    }

    private static uint ReadUInt32(DbDataReader reader, int ordinal)
    {
        return Database.UInt32IntegerConverter.FromStored(ReadInt64(reader, ordinal));
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
    long? BeforeRowId = null,
    IReadOnlyList<long>? PrivateConversationIds = null,
    uint? PeerUin = null,
    string? PeerUid = null)
{
    public bool HasConversationFilter =>
        GroupId is not null ||
        PeerUin is not null ||
        !string.IsNullOrWhiteSpace(PeerUid) ||
        PrivateConversationIds is { Count: > 0 };
}

public sealed record GroupMessageFtsSearchPage(
    IReadOnlyList<GroupMessageFtsSearchResult> Results,
    long? NextBeforeRowId,
    bool HasMore);

public sealed record GroupMessageFtsCountRequest(
    string Query,
    uint? GroupId = null,
    IReadOnlyList<long>? PrivateConversationIds = null,
    uint? PeerUin = null,
    string? PeerUid = null)
{
    public bool HasConversationFilter =>
        GroupId is not null ||
        PeerUin is not null ||
        !string.IsNullOrWhiteSpace(PeerUid) ||
        PrivateConversationIds is { Count: > 0 };
}

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
    public long PrivateConversationId { get; init; }
    public string? SenderUid { get; init; }
    public IReadOnlyList<string> TextFields { get; init; } = [];
    public string PreviewText { get; init; } = string.Empty;
    public double Rank { get; init; }
    public bool IsUnreadable { get; init; }
}

public sealed class GroupMessageFtsMatchScanRow
{
    public long RowId { get; init; }
    public ChatType ChatType { get; init; }
    public string? PeerUid { get; init; }
    public uint GroupId { get; init; }
    public long PrivateConversationId { get; init; }
    public bool IsUnreadable { get; init; }
}
