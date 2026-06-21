using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using QQDatabaseReader.Database;
using QQDatabaseReader.Sqlite;

namespace QQDatabaseReader;

public sealed class PCQQMessageReader : IQQDatabase
{
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
        RawDatabase.Initialize();
        _connection = new QQNTDbConnection(RawDatabase.Database, RawDatabase.DatabaseFilePath);
        _connection.Open();
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
            $"""
            SELECT Time, Rand, SenderUin, MsgContent, Info
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
            $"""
            SELECT Time, Rand, SenderUin, MsgContent, Info
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
            $"""
            SELECT Time, Rand, SenderUin, MsgContent, Info
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
            $"""
            SELECT Time, Rand, SenderUin, MsgContent, Info
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
            $"""
            SELECT Time, Rand, SenderUin, MsgContent, Info
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

        return QueryMessages(
            $"""
            SELECT Time, Rand, SenderUin, MsgContent, Info
            FROM {QuoteIdent(tableName)}
            ORDER BY Time DESC, Rand DESC
            """,
            _ => { })
            .FirstOrDefault(message => message.MessageSeq == messageSeq);
    }

    public PCQQMessageSearchPage SearchMessages(
        string tableName,
        string query,
        int pageSize,
        PCQQMessageSearchCursor? cursor = null)
    {
        EnsureConversationTableName(tableName);
        if (string.IsNullOrWhiteSpace(query) || pageSize <= 0)
            return new PCQQMessageSearchPage([], null, false);

        var results = new List<PCQQMessageRecord>(pageSize);
        PCQQMessageSearchCursor? nextCursor = cursor;
        var hasMore = true;
        while (results.Count < pageSize && hasMore)
        {
            var batch = LoadSearchBatch(tableName, nextCursor, 500);
            hasMore = batch.Count == 500;
            if (batch.Count == 0)
                break;

            var last = batch[^1];
            nextCursor = new PCQQMessageSearchCursor(last.MessageTime, last.MessageRandom);

            foreach (var message in batch)
            {
                if (message.PreviewText.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                {
                    results.Add(message);
                    if (results.Count >= pageSize)
                        break;
                }
            }
        }

        return new PCQQMessageSearchPage(
            results,
            hasMore ? nextCursor : null,
            hasMore);
    }

    private PCQQLatestMessage? GetLatestMessage(string tableName)
    {
        var messages = QueryMessages(
            $"""
            SELECT Time, Rand, SenderUin, MsgContent, Info
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
        int batchSize)
    {
        var cursorPredicate = cursor is null
            ? string.Empty
            : "WHERE Time < $time OR (Time = $time AND Rand < $rand)";
        return QueryMessages(
            $"""
            SELECT Time, Rand, SenderUin, MsgContent, Info
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
            });
    }

    private IReadOnlyList<PCQQMessageRecord> QueryMessages(string sql, Action<IDbCommand> bind)
    {
        EnsureConnection();

        using var command = _connection!.CreateCommand();
        command.CommandText = sql;
        bind(command);

        var messages = new List<PCQQMessageRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var content = ReadBlob(reader, 3);
            var parsedContent = PCQQMessageContentParser.Parse(content);
            var senderUin = unchecked((uint)reader.GetInt64(2));
            var senderNickname = ResolveSenderNickname(senderUin, parsedContent.SenderNickname);

            messages.Add(new PCQQMessageRecord(
                reader.GetInt64(0),
                reader.GetInt64(1),
                senderUin,
                content,
                ReadBlob(reader, 4),
                parsedContent.DisplayText,
                parsedContent.MessageSeq,
                senderNickname));
        }

        return messages;
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
                    AddParameter(command, "$sender" + i, unchecked((int)senderIds[i]));
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
    string? SenderNickname);

public sealed record PCQQMessageDate(
    int DayStartTime,
    int MessageCount);

public sealed record PCQQMessageSearchCursor(long MessageTime, long MessageRandom);

public sealed record PCQQMessageSearchPage(
    IReadOnlyList<PCQQMessageRecord> Messages,
    PCQQMessageSearchCursor? NextCursor,
    bool HasMore);

public enum PCQQConversationType
{
    Group,
    Private,
}

sealed record PCQQLatestMessage(long MessageTime, string PreviewText, uint SenderUin, string? SenderNickname);

sealed record PCQQFilterClause(string Sql, string Predicate, Action<IDbCommand> Bind);
