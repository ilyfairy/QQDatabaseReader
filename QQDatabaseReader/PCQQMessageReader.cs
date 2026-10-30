using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using QQDatabaseReader.Database;
using QQDatabaseReader.Sqlite;

namespace QQDatabaseReader;

public sealed class PCQQMessageReader : IQQDatabase
{
    private const int MaxSequenceLookupProbes = 64;
    private const int SequenceLookupProbeRadius = 16;
    private const int MessageSequenceRowIdCacheCapacity = 8192;
    internal const int MaxSearchScanRowsPerPage = 5000;
    private const int SearchScanBatchSize = 500;

    private static readonly Regex ConversationTableRegex = new(
        @"^(?<kind>group|buddy)_(?<id>\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public RawDatabase RawDatabase { get; }
    public PCQQInfoDbReader? InfoDatabase { get; }
    public QQDatabaseType DatabaseType => QQDatabaseType.PCQQMessage;
    public string DatabaseFilePath => RawDatabase.DatabaseFilePath;
    public uint CurrentUin { get; }
    public string? InfoDbPath { get; }
    public string? InfoDbKey { get; }

    private QQNTDbConnection? _connection;
    private readonly object _messageRowIdCacheGate = new();
    private readonly Dictionary<
        (string TableName, long MessageSeq),
        (long RowId, LinkedListNode<(string TableName, long MessageSeq)> OrderNode)> _messageRowIdsBySeq = new();
    private readonly LinkedList<(string TableName, long MessageSeq)> _messageRowIdCacheOrder = new();
    private bool _messageRowIdCacheDisposed;

    public PCQQMessageReader(
        string databaseFilePath,
        string key,
        string? infoDbPath = null,
        string? infoDbKey = null)
    {
        RawDatabase = RawDatabase.OpenPCQQ(databaseFilePath, key);
        CurrentUin = TryGetCurrentUin(databaseFilePath);
        InfoDbPath = string.IsNullOrWhiteSpace(infoDbPath) ? null : infoDbPath;
        InfoDbKey = string.IsNullOrWhiteSpace(infoDbKey) ? null : infoDbKey;
        if (!string.IsNullOrWhiteSpace(infoDbPath) &&
            !string.IsNullOrWhiteSpace(infoDbKey))
        {
            InfoDatabase = new PCQQInfoDbReader(infoDbPath, infoDbKey);
        }
    }

    public void Initialize()
    {
        try
        {
            RawDatabase.Initialize();
            _connection = new QQNTDbConnection(RawDatabase.Database, RawDatabase.DatabaseFilePath);
            _connection.Open();
        }
        catch
        {
            _connection?.Dispose();
            _connection = null;
            RawDatabase.Dispose();
            throw;
        }
    }

    public IReadOnlyList<PCQQConversation> GetConversations()
    {
        EnsureConnection();

        var conversations = new List<PCQQConversation>();
        using var command = _connection!.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND (name LIKE 'group\_%' ESCAPE '\' OR name LIKE 'buddy\_%' ESCAPE '\')
            ORDER BY name
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var tableName = reader.GetString(0);
            var match = ConversationTableRegex.Match(tableName);
            if (!match.Success)
                continue;

            var conversationType = match.Groups["kind"].Value == "group"
                ? PCQQConversationType.Group
                : PCQQConversationType.Private;
            if (!uint.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var rawPeerId))
                continue;

            var peerId = rawPeerId;
            PCQQInfoGroup? groupInfo = null;
            if (conversationType == PCQQConversationType.Group &&
                InfoDatabase is not null &&
                InfoDatabase.TryGetGroup(rawPeerId, out var matchedGroup))
            {
                groupInfo = matchedGroup;
                peerId = matchedGroup.GroupCode != 0 ? matchedGroup.GroupCode : matchedGroup.GroupId;
            }

            var latest = GetLatestMessage(tableName);
            var displayName = conversationType == PCQQConversationType.Group
                ? groupInfo?.GroupName ?? tableName
                : ResolveContactDisplayName(rawPeerId);
            conversations.Add(new PCQQConversation(
                conversationType,
                peerId,
                rawPeerId,
                tableName,
                displayName,
                groupInfo?.GroupId ?? 0,
                groupInfo?.GroupCode ?? 0,
                latest?.MessageTime ?? 0,
                latest?.PreviewText ?? string.Empty,
                latest?.SenderUin ?? 0,
                latest?.SenderNickname));
        }

        return conversations
            .OrderByDescending(conversation => conversation.LatestMessageTime)
            .ThenBy(conversation => conversation.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<PCQQMessageRecord> LoadLatestMessages(
        string tableName,
        int pageSize,
        MessageQueryFilter? filter = null)
    {
        EnsureConversationTableName(tableName);
        var whereClause = CreatePCQQFilterClause(filter);
        return QueryMessages(
            tableName,
            $"""
            SELECT rowid, Time, Rand, SenderUin, MsgContent, Info
            FROM {QuoteIdent(tableName)}
            {whereClause.Sql}
            ORDER BY Time DESC, Rand DESC
            LIMIT $limit
            """,
            command =>
            {
                whereClause.Bind(command);
                AddParameter(command, "$limit", pageSize);
            })
            .OrderBy(message => message.MessageTime)
            .ThenBy(message => message.MessageRandom)
            .ToArray();
    }

    public IReadOnlyList<PCQQMessageRecord> LoadEarliestMessages(
        string tableName,
        int pageSize,
        MessageQueryFilter? filter = null)
    {
        EnsureConversationTableName(tableName);
        var whereClause = CreatePCQQFilterClause(filter);
        return QueryMessages(
            tableName,
            $"""
            SELECT rowid, Time, Rand, SenderUin, MsgContent, Info
            FROM {QuoteIdent(tableName)}
            {whereClause.Sql}
            ORDER BY Time ASC, Rand ASC
            LIMIT $limit
            """,
            command =>
            {
                whereClause.Bind(command);
                AddParameter(command, "$limit", pageSize);
            });
    }

    public IReadOnlyList<PCQQMessageDate> LoadMessageDates(string tableName)
    {
        EnsureConversationTableName(tableName);
        EnsureConnection();

        using var command = _connection!.CreateCommand();
        command.CommandText =
            $"""
            SELECT strftime('%s', date(Time, 'unixepoch', 'localtime'), 'utc') AS DayStart,
                   COUNT(*) AS MessageCount
            FROM {QuoteIdent(tableName)}
            GROUP BY date(Time, 'unixepoch', 'localtime')
            ORDER BY DayStart
            """;

        var dates = new List<PCQQMessageDate>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var dayStart = reader.GetValue(0) switch
            {
                long value => value,
                int value => value,
                string value when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => 0,
            };
            if (dayStart <= 0)
                continue;

            dates.Add(new PCQQMessageDate(
                (int)dayStart,
                Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture)));
        }

        return dates;
    }

    public IReadOnlyList<PCQQMessageRecord> LoadOlderMessages(
        string tableName,
        long messageTime,
        long messageRandom,
        int pageSize,
        MessageQueryFilter? filter = null)
    {
        EnsureConversationTableName(tableName);
        var whereClause = CreatePCQQFilterClause(filter);
        var olderPredicate = string.IsNullOrWhiteSpace(whereClause.Predicate)
            ? "Time < $time OR (Time = $time AND Rand < $rand)"
            : $"{whereClause.Predicate} AND (Time < $time OR (Time = $time AND Rand < $rand))";
        return QueryMessages(
            tableName,
            $"""
            SELECT rowid, Time, Rand, SenderUin, MsgContent, Info
            FROM {QuoteIdent(tableName)}
            WHERE {olderPredicate}
            ORDER BY Time DESC, Rand DESC
            LIMIT $limit
            """,
            command =>
            {
                whereClause.Bind(command);
                AddParameter(command, "$time", messageTime);
                AddParameter(command, "$rand", messageRandom);
                AddParameter(command, "$limit", pageSize);
            });
    }

    public IReadOnlyList<PCQQMessageRecord> LoadNewerMessages(
        string tableName,
        long messageTime,
        long messageRandom,
        int pageSize,
        MessageQueryFilter? filter = null)
    {
        EnsureConversationTableName(tableName);
        var whereClause = CreatePCQQFilterClause(filter);
        var newerPredicate = string.IsNullOrWhiteSpace(whereClause.Predicate)
            ? "Time > $time OR (Time = $time AND Rand > $rand)"
            : $"{whereClause.Predicate} AND (Time > $time OR (Time = $time AND Rand > $rand))";
        return QueryMessages(
            tableName,
            $"""
            SELECT rowid, Time, Rand, SenderUin, MsgContent, Info
            FROM {QuoteIdent(tableName)}
            WHERE {newerPredicate}
            ORDER BY Time ASC, Rand ASC
            LIMIT $limit
            """,
            command =>
            {
                whereClause.Bind(command);
                AddParameter(command, "$time", messageTime);
                AddParameter(command, "$rand", messageRandom);
                AddParameter(command, "$limit", pageSize);
            });
    }

    public PCQQMessageRecord? LoadMessage(string tableName, long messageTime, long messageRandom)
    {
        EnsureConversationTableName(tableName);
        return QueryMessages(
            tableName,
            $"""
            SELECT rowid, Time, Rand, SenderUin, MsgContent, Info
            FROM {QuoteIdent(tableName)}
            WHERE Time = $time AND Rand = $rand
            LIMIT 1
            """,
            command =>
            {
                AddParameter(command, "$time", messageTime);
                AddParameter(command, "$rand", messageRandom);
            })
            .FirstOrDefault();
    }

    public PCQQMessageRecord? LoadMessageBySeq(string tableName, long messageSeq)
    {
        EnsureConversationTableName(tableName);
        if (messageSeq <= 0)
            return null;

        if (!TryGetCachedMessageRowId(tableName, messageSeq, out var rowId))
            return LocateMessageBySeq(tableName, messageSeq);

        var cachedMessage = QueryMessages(
                tableName,
                $"""
                SELECT rowid, Time, Rand, SenderUin, MsgContent, Info
                FROM {QuoteIdent(tableName)}
                WHERE rowid = $rowid
                LIMIT 1
                """,
                command => AddParameter(command, "$rowid", rowId))
            .FirstOrDefault(message => message.MessageSeq == messageSeq);
        if (cachedMessage is not null)
            return cachedMessage;

        RemoveCachedMessageRowId(tableName, messageSeq);
        return LocateMessageBySeq(tableName, messageSeq);
    }

    private PCQQMessageRecord? LocateMessageBySeq(string tableName, long messageSeq)
    {
        var bounds = GetRowIdBounds(tableName);
        if (bounds is null)
            return null;

        var pendingRanges = new Stack<(long LowerRowId, long UpperRowId)>();
        pendingRanges.Push((bounds.Value.FirstRowId, bounds.Value.LastRowId));

        var probeCount = 0;
        while (pendingRanges.Count > 0 && probeCount < MaxSequenceLookupProbes)
        {
            var (lowerRowId, upperRowId) = pendingRanges.Pop();
            if (lowerRowId > upperRowId)
                continue;

            probeCount++;
            var midpoint = (long)(((Int128)lowerRowId + upperRowId) / 2);
            var messages = LoadMessagesNearRowId(tableName, lowerRowId, upperRowId, midpoint);
            if (messages.Count == 0)
                continue;

            var match = messages.FirstOrDefault(message => message.MessageSeq == messageSeq);
            if (match is not null)
                return match;

            var firstRowId = messages[0].RowId;
            var lastRowId = messages[^1].RowId;
            var hasLeftRange = firstRowId > lowerRowId;
            var hasRightRange = lastRowId < upperRowId;
            var positiveSequences = messages
                .Where(static message => message.MessageSeq > 0)
                .Select(static message => message.MessageSeq)
                .ToArray();

            if (positiveSequences.Length == 0 ||
                messageSeq >= positiveSequences.Min() && messageSeq <= positiveSequences.Max())
            {
                if (hasRightRange)
                    pendingRanges.Push((lastRowId + 1, upperRowId));
                if (hasLeftRange)
                    pendingRanges.Push((lowerRowId, firstRowId - 1));
                continue;
            }

            if (messageSeq < positiveSequences.Min())
            {
                if (hasRightRange)
                    pendingRanges.Push((lastRowId + 1, upperRowId));
                if (hasLeftRange)
                    pendingRanges.Push((lowerRowId, firstRowId - 1));
            }
            else
            {
                if (hasLeftRange)
                    pendingRanges.Push((lowerRowId, firstRowId - 1));
                if (hasRightRange)
                    pendingRanges.Push((lastRowId + 1, upperRowId));
            }
        }

        return null;
    }

    private IReadOnlyList<PCQQMessageRecord> LoadMessagesNearRowId(
        string tableName,
        long lowerRowId,
        long upperRowId,
        long midpoint)
    {
        var before = QueryMessages(
                tableName,
                $"""
                SELECT rowid, Time, Rand, SenderUin, MsgContent, Info
                FROM {QuoteIdent(tableName)}
                WHERE rowid >= $lowerRowId AND rowid < $midpoint
                ORDER BY rowid DESC
                LIMIT $limit
                """,
                command =>
                {
                    AddParameter(command, "$lowerRowId", lowerRowId);
                    AddParameter(command, "$midpoint", midpoint);
                    AddParameter(command, "$limit", SequenceLookupProbeRadius);
                });
        var after = QueryMessages(
            tableName,
            $"""
            SELECT rowid, Time, Rand, SenderUin, MsgContent, Info
            FROM {QuoteIdent(tableName)}
            WHERE rowid >= $midpoint AND rowid <= $upperRowId
            ORDER BY rowid
            LIMIT $limit
            """,
            command =>
            {
                AddParameter(command, "$midpoint", midpoint);
                AddParameter(command, "$upperRowId", upperRowId);
                AddParameter(command, "$limit", SequenceLookupProbeRadius + 1);
            });

        return before
            .Concat(after)
            .OrderBy(static message => message.RowId)
            .ToArray();
    }

    private (long FirstRowId, long LastRowId)? GetRowIdBounds(string tableName)
    {
        EnsureConnection();
        using var firstCommand = _connection!.CreateCommand();
        firstCommand.CommandText = $"SELECT rowid FROM {QuoteIdent(tableName)} WHERE rowid >= $minimumRowId ORDER BY rowid LIMIT 1";
        AddParameter(firstCommand, "$minimumRowId", long.MinValue);
        var firstValue = firstCommand.ExecuteScalar();
        if (firstValue is null || firstValue is DBNull)
            return null;

        using var lastCommand = _connection.CreateCommand();
        lastCommand.CommandText = $"SELECT rowid FROM {QuoteIdent(tableName)} WHERE rowid <= $maximumRowId ORDER BY rowid DESC LIMIT 1";
        AddParameter(lastCommand, "$maximumRowId", long.MaxValue);
        var lastValue = lastCommand.ExecuteScalar();
        if (lastValue is null || lastValue is DBNull)
            return null;

        return (
            Convert.ToInt64(firstValue, CultureInfo.InvariantCulture),
            Convert.ToInt64(lastValue, CultureInfo.InvariantCulture));
    }

    public PCQQMessageSearchPage SearchMessages(
        string tableName,
        string query,
        int pageSize,
        PCQQMessageSearchCursor? cursor = null,
        CancellationToken cancellationToken = default,
        int maxScanRows = MaxSearchScanRowsPerPage)
    {
        EnsureConversationTableName(tableName);
        if (string.IsNullOrWhiteSpace(query) || pageSize <= 0)
            return new PCQQMessageSearchPage([], null, false, 0);
        if (maxScanRows <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxScanRows));

        var results = new List<PCQQMessageRecord>(pageSize);
        PCQQMessageSearchCursor? nextCursor = cursor;
        var hasMore = true;
        var scannedRows = 0;
        var scanBudget = Math.Min(MaxSearchScanRowsPerPage, maxScanRows);
        while (results.Count < pageSize &&
               hasMore &&
               scannedRows < scanBudget)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestedBatchSize = Math.Min(
                SearchScanBatchSize,
                scanBudget - scannedRows);
            var batch = LoadSearchBatch(
                tableName,
                nextCursor,
                requestedBatchSize,
                cancellationToken);
            hasMore = batch.Count == requestedBatchSize;
            if (batch.Count == 0)
                break;

            for (var index = 0; index < batch.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var message = batch[index];
                scannedRows++;
                nextCursor = new PCQQMessageSearchCursor(message.MessageTime, message.MessageRandom);
                if (message.PreviewText.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                {
                    results.Add(message);
                    if (results.Count >= pageSize)
                    {
                        hasMore = index + 1 < batch.Count || hasMore;
                        break;
                    }
                }
            }
        }

        if (scannedRows >= scanBudget)
            hasMore = true;

        return new PCQQMessageSearchPage(
            results,
            hasMore ? nextCursor : null,
            hasMore,
            scannedRows);
    }

    private PCQQLatestMessage? GetLatestMessage(string tableName)
    {
        var messages = QueryMessages(
            tableName,
            $"""
            SELECT rowid, Time, Rand, SenderUin, MsgContent, Info
            FROM {QuoteIdent(tableName)}
            ORDER BY Time DESC, Rand DESC
            LIMIT 1
            """,
            _ => { });
        var latest = messages.FirstOrDefault();
        return latest is null
            ? null
            : new PCQQLatestMessage(latest.MessageTime, latest.PreviewText, latest.SenderUin, latest.SenderNickname);
    }

    private IReadOnlyList<PCQQMessageRecord> LoadSearchBatch(
        string tableName,
        PCQQMessageSearchCursor? cursor,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var cursorPredicate = cursor is null
            ? string.Empty
            : "WHERE Time < $time OR (Time = $time AND Rand < $rand)";
        return QueryMessages(
            tableName,
            $"""
            SELECT rowid, Time, Rand, SenderUin, MsgContent, Info
            FROM {QuoteIdent(tableName)}
            {cursorPredicate}
            ORDER BY Time DESC, Rand DESC
            LIMIT $limit
            """,
            command =>
            {
                if (cursor is not null)
                {
                    AddParameter(command, "$time", cursor.MessageTime);
                    AddParameter(command, "$rand", cursor.MessageRandom);
                }

                AddParameter(command, "$limit", batchSize);
            },
            cacheMessageSequence: false,
            cancellationToken: cancellationToken);
    }

    private IReadOnlyList<PCQQMessageRecord> QueryMessages(
        string tableName,
        string sql,
        Action<IDbCommand> bind,
        bool cacheMessageSequence = true,
        CancellationToken cancellationToken = default)
    {
        EnsureConnection();
        cancellationToken.ThrowIfCancellationRequested();

        using var command = _connection!.CreateCommand();
        command.CommandText = sql;
        bind(command);

        var messages = new List<PCQQMessageRecord>();
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((IDbCommand)state!).Cancel(),
            command);
        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rowId = reader.GetInt64(0);
                var content = ReadBlob(reader, 4);
                var parsedContent = PCQQMessageContentParser.Parse(content);
                var senderUin = unchecked((uint)reader.GetInt64(3));
                var senderNickname = ResolveSenderNickname(senderUin, parsedContent.SenderNickname);

                var message = new PCQQMessageRecord(
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    senderUin,
                    content,
                    ReadBlob(reader, 5),
                    parsedContent.DisplayText,
                    parsedContent.MessageSeq,
                    senderNickname,
                    rowId);
                messages.Add(message);

                if (cacheMessageSequence && message.MessageSeq > 0)
                    CacheMessageRowId(tableName, message.MessageSeq, rowId);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "PCQQ message search was canceled.",
                exception,
                cancellationToken);
        }

        return messages;
    }

    private bool TryGetCachedMessageRowId(string tableName, long messageSeq, out long rowId)
    {
        lock (_messageRowIdCacheGate)
        {
            if (!_messageRowIdCacheDisposed &&
                _messageRowIdsBySeq.TryGetValue((tableName, messageSeq), out var entry))
            {
                rowId = entry.RowId;
                return true;
            }
        }

        rowId = 0;
        return false;
    }

    private void CacheMessageRowId(string tableName, long messageSeq, long rowId)
    {
        var key = (tableName, messageSeq);
        lock (_messageRowIdCacheGate)
        {
            if (_messageRowIdCacheDisposed)
                return;

            if (_messageRowIdsBySeq.TryGetValue(key, out var existing))
            {
                _messageRowIdsBySeq[key] = (rowId, existing.OrderNode);
                return;
            }

            if (_messageRowIdsBySeq.Count >= MessageSequenceRowIdCacheCapacity)
            {
                var oldest = _messageRowIdCacheOrder.First!;
                _messageRowIdCacheOrder.RemoveFirst();
                _messageRowIdsBySeq.Remove(oldest.Value);
            }

            var orderNode = _messageRowIdCacheOrder.AddLast(key);
            _messageRowIdsBySeq.Add(key, (rowId, orderNode));
        }
    }

    private void RemoveCachedMessageRowId(string tableName, long messageSeq)
    {
        lock (_messageRowIdCacheGate)
        {
            if (_messageRowIdsBySeq.Remove((tableName, messageSeq), out var entry))
                _messageRowIdCacheOrder.Remove(entry.OrderNode);
        }
    }

    private void ClearMessageRowIdCache()
    {
        lock (_messageRowIdCacheGate)
        {
            _messageRowIdCacheDisposed = true;
            _messageRowIdsBySeq.Clear();
            _messageRowIdCacheOrder.Clear();
        }
    }

    private static PCQQFilterClause CreatePCQQFilterClause(MessageQueryFilter? filter)
    {
        if (filter is null || filter.IsEmpty)
            return new PCQQFilterClause(string.Empty, string.Empty, _ => { });

        var predicates = new List<string>();
        if (filter.StartTime is not null)
            predicates.Add("Time >= $filterStartTime");
        if (filter.EndTimeExclusive is not null)
            predicates.Add("Time < $filterEndTime");
        var selectedDayStartTimes = filter.SelectedDayStartTimes
            .Where(dayStartTime => dayStartTime > 0)
            .Distinct()
            .OrderBy(dayStartTime => dayStartTime)
            .ToArray();
        if (selectedDayStartTimes.Length > 0)
        {
            var dayPredicates = selectedDayStartTimes
                .Select((_, index) => $"(Time >= $dayStart{index} AND Time < $dayEnd{index})");
            predicates.Add("(" + string.Join(" OR ", dayPredicates) + ")");
        }

        var senderIds = filter.SenderIds
            .Where(senderId => senderId != 0)
            .Distinct()
            .ToArray();
        if (senderIds.Length > 0)
        {
            predicates.Add($"SenderUin IN ({string.Join(", ", senderIds.Select((_, index) => "$sender" + index))})");
        }

        var predicate = string.Join(" AND ", predicates);
        var sql = string.IsNullOrWhiteSpace(predicate) ? string.Empty : $"WHERE {predicate}";
        return new PCQQFilterClause(
            sql,
            predicate,
            command =>
            {
                if (filter.StartTime is { } startTime)
                    AddParameter(command, "$filterStartTime", startTime);
                if (filter.EndTimeExclusive is { } endTime)
                    AddParameter(command, "$filterEndTime", endTime);

                for (var i = 0; i < selectedDayStartTimes.Length; i++)
                {
                    AddParameter(command, "$dayStart" + i, selectedDayStartTimes[i]);
                    AddParameter(command, "$dayEnd" + i, selectedDayStartTimes[i] + 86400);
                }

                for (var i = 0; i < senderIds.Length; i++)
                {
                    // SQLite stores SenderUin as INTEGER.  Binding a QQ number
                    // above Int32.MaxValue as int turns it into a negative
                    // value and makes the sender filter miss valid messages.
                    AddParameter(command, "$sender" + i, (long)senderIds[i]);
                }
            });
    }

    public string? ResolveContactName(uint uin)
    {
        return ResolveContactDisplayName(uin);
    }

    private string? ResolveSenderNickname(uint senderUin, string? parsedNickname)
    {
        if (!string.IsNullOrWhiteSpace(parsedNickname))
            return parsedNickname.Trim();

        return ResolveContactDisplayName(senderUin);
    }

    private string? ResolveContactDisplayName(uint uin)
    {
        return uin != 0 &&
               InfoDatabase is not null &&
               InfoDatabase.TryGetContact(uin, out var contact)
            ? contact.DisplayName
            : null;
    }

    private static uint TryGetCurrentUin(string databaseFilePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databaseFilePath));
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (uint.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var uin))
                return uin;

            directory = Path.GetDirectoryName(directory);
        }

        return 0;
    }

    private void EnsureConnection()
    {
        if (_connection is null)
            throw new InvalidOperationException("PCQQ database is not initialized.");
    }

    private static byte[]? ReadBlob(IDataRecord reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        return reader.GetValue(ordinal) is byte[] bytes ? bytes : null;
    }

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void EnsureConversationTableName(string tableName)
    {
        if (!ConversationTableRegex.IsMatch(tableName))
            throw new ArgumentException("Invalid PCQQ conversation table name.", nameof(tableName));
    }

    private static string QuoteIdent(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    public void Dispose()
    {
        ClearMessageRowIdCache();
        _connection?.Dispose();
        RawDatabase.Dispose();
    }
}

public sealed record PCQQConversation(
    PCQQConversationType ConversationType,
    uint PeerId,
    uint RawPeerId,
    string TableName,
    string? DisplayNameOverride,
    uint InfoGroupId,
    uint InfoGroupCode,
    long LatestMessageTime,
    string LatestMessageText,
    uint LatestMessageSenderUin,
    string? LatestMessageSenderNickname)
{
    public string DisplayName => !string.IsNullOrWhiteSpace(DisplayNameOverride)
        ? DisplayNameOverride
        : PeerId == 0 ? TableName : PeerId.ToString(CultureInfo.InvariantCulture);
}

public sealed record PCQQMessageRecord(
    long MessageTime,
    long MessageRandom,
    uint SenderUin,
    byte[]? Content,
    byte[]? Info,
    string PreviewText,
    long MessageSeq,
    string? SenderNickname,
    long RowId = 0);

public sealed record PCQQMessageDate(
    int DayStartTime,
    int MessageCount);

public sealed record PCQQMessageSearchCursor(long MessageTime, long MessageRandom);

public sealed record PCQQMessageSearchPage(
    IReadOnlyList<PCQQMessageRecord> Messages,
    PCQQMessageSearchCursor? NextCursor,
    bool HasMore,
    int ScannedRows = 0);

public enum PCQQConversationType
{
    Group,
    Private,
}

sealed record PCQQLatestMessage(long MessageTime, string PreviewText, uint SenderUin, string? SenderNickname);

sealed record PCQQFilterClause(string Sql, string Predicate, Action<IDbCommand> Bind);
