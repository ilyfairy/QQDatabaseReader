using System.Data;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QQDatabaseReader.Database;
using QQDatabaseReader.Sqlite;

namespace QQDatabaseReader;

public sealed class IcalinguaMessageReader : IQQDatabase
{
    private const string UnifiedMessagesTableName = "messages";
    private readonly Dictionary<long, string> _legacyMessageTables = new();
    private readonly Dictionary<string, HashSet<string>> _tableColumns = new(StringComparer.OrdinalIgnoreCase);

    private QQNTDbConnection? _connection;
    private bool _hasUnifiedMessagesTable;
    private IReadOnlyList<IcalinguaConversation>? _conversationCache;
    private IReadOnlyList<long>? _messageRoomIdCache;

    public IcalinguaMessageReader(string databaseFilePath, string? dataPath = null)
    {
        RawDatabase = new RawDatabase(databaseFilePath);
        DataPath = string.IsNullOrWhiteSpace(dataPath) ? null : dataPath;
    }

    public QQDatabaseType DatabaseType => QQDatabaseType.IcalinguaMessage;

    public string DatabaseFilePath => RawDatabase.DatabaseFilePath;

    public RawDatabase RawDatabase { get; }

    public string? DataPath { get; }

    public void Initialize()
    {
        RawDatabase.Initialize();
        _connection = new QQNTDbConnection(RawDatabase.Database, RawDatabase.DatabaseFilePath);
        _connection.Open();
        _hasUnifiedMessagesTable = HasTable(UnifiedMessagesTableName) &&
                                   GetTableColumns(UnifiedMessagesTableName).Contains("roomId");
        LoadLegacyMessageTables();
    }

    private void EnsureConnection()
    {
        if (_connection is null)
            throw new InvalidOperationException("Icalingua database is not initialized.");
    }

    public void Dispose()
    {
        _connection?.Dispose();
        RawDatabase.Dispose();
    }

    // Icalingua 有新旧两种表结构：统一 messages 表，以及按房间拆分的 msg{roomId} 表。
    private IReadOnlyList<IcalinguaRoomRow> ReadRooms()
    {
        var columns = GetTableColumns("rooms");
        if (!columns.Contains("roomId"))
            return [];

        using var command = _connection!.CreateCommand();
        command.CommandText =
            $"""
            SELECT {SelectColumn(columns, "roomId")},
                   {SelectColumn(columns, "roomName")},
                   {SelectColumn(columns, "utime")},
                   {SelectColumn(columns, "lastMessage")},
                   {SelectColumn(columns, "downloadPath")}
            FROM rooms
            ORDER BY {SelectColumn(columns, "utime", "0")} DESC
            """;

        var rooms = new List<IcalinguaRoomRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var roomId = ParseInt64(ReadNullableString(reader, 0));
            if (roomId == 0)
                continue;

            rooms.Add(new IcalinguaRoomRow(
                roomId,
                ReadNullableString(reader, 1),
                ReadInt64(reader, 2),
                NormalizeJson(ReadNullableString(reader, 3)),
                ReadNullableString(reader, 4)));
        }

        return rooms;
    }

    private IReadOnlyList<long> GetMessageRoomIds()
    {
        if (_messageRoomIdCache is { } cachedRoomIds)
            return cachedRoomIds;

        var roomIds = new HashSet<long>();
        if (_hasUnifiedMessagesTable)
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = """
                SELECT DISTINCT roomId
                FROM messages
                WHERE roomId != 0
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var roomId = ReadInt64(reader, 0);
                if (roomId != 0)
                    roomIds.Add(roomId);
            }
        }

        foreach (var roomId in _legacyMessageTables.Keys)
            roomIds.Add(roomId);

        _messageRoomIdCache = roomIds.ToArray();
        return _messageRoomIdCache;
    }

    private IReadOnlyList<IcalinguaMessageSource> ResolveMessageSources(long roomId)
    {
        var sources = new List<IcalinguaMessageSource>();
        var sourceIndex = 0;

        if (_hasUnifiedMessagesTable)
        {
            sources.Add(new IcalinguaMessageSource(
                UnifiedMessagesTableName,
                roomId,
                true,
                GetTableColumns(UnifiedMessagesTableName),
                sourceIndex++));
        }

        if (_legacyMessageTables.TryGetValue(roomId, out var tableName) && HasTable(tableName))
        {
            sources.Add(new IcalinguaMessageSource(
                tableName,
                roomId,
                false,
                GetTableColumns(tableName),
                sourceIndex++));
        }

        var fallbackTableName = "msg" + roomId.ToString(CultureInfo.InvariantCulture);
        if (!_legacyMessageTables.ContainsKey(roomId) && HasTable(fallbackTableName))
        {
            sources.Add(new IcalinguaMessageSource(
                fallbackTableName,
                roomId,
                false,
                GetTableColumns(fallbackTableName),
                sourceIndex));
        }

        return sources;
    }

    private void LoadLegacyMessageTables()
    {
        _legacyMessageTables.Clear();
        if (HasTable("msgTableName"))
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT tableName FROM msgTableName";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                AddLegacyMessageTable(ReadNullableString(reader, 0));
            }
        }

        using var masterCommand = _connection!.CreateCommand();
        masterCommand.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND name LIKE 'msg%'
            """;
        using var masterReader = masterCommand.ExecuteReader();
        while (masterReader.Read())
        {
            AddLegacyMessageTable(ReadNullableString(masterReader, 0));
        }
    }

    private void AddLegacyMessageTable(string? tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName) ||
            !tableName.StartsWith("msg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tableName, "msgTableName", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tableName, UnifiedMessagesTableName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!long.TryParse(tableName[3..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var roomId) ||
            roomId == 0)
        {
            return;
        }

        _legacyMessageTables[roomId] = tableName;
    }

    private bool HasTable(string tableName)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = $name
            LIMIT 1
            """;
        AddParameter(command, "$name", tableName);
        return command.ExecuteScalar() is not null;
    }

    private HashSet<string> GetTableColumns(string tableName)
    {
        if (_tableColumns.TryGetValue(tableName, out var cached))
            return cached;

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = _connection!.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteString(tableName)})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var columnName = ReadNullableString(reader, 1);
            if (!string.IsNullOrWhiteSpace(columnName))
                columns.Add(columnName);
        }

        _tableColumns[tableName] = columns;
        return columns;
    }

    private static readonly string[] SearchableColumnNames =
        [
            "content",
        "title",
        "code",
        "file",
        "files",
        "mirai",
        "recallInfo",
        "username",
    ];

    private static bool HasSearchableColumns(IReadOnlySet<string> columns)
    {
        return GetSearchableColumns(columns).Length > 0;
    }

    private static string[] GetSearchableColumns(IReadOnlySet<string> columns)
    {
        return SearchableColumnNames
            .Where(columns.Contains)
            .ToArray();
    }

    private static IcalinguaFilterClause CreateSearchClause(string keyword, IcalinguaMessageSource source)
    {
        var columns = GetSearchableColumns(source.Columns);
        if (columns.Length == 0)
            return new IcalinguaFilterClause("0 = 1", _ => { });

        var predicates = columns
            .Select(column => $"{QuoteIdent(column)} LIKE $searchKeyword ESCAPE '\\'")
            .ToArray();

        return new IcalinguaFilterClause(
            "(" + string.Join(" OR ", predicates) + ")",
            command => AddParameter(command, "$searchKeyword", "%" + EscapeLike(keyword.Trim()) + "%"));
    }

    private static IcalinguaFilterClause CreateSearchCursorClause(
        IcalinguaMessageSearchCursor? cursor,
        IcalinguaMessageSource source,
        string timeExpression)
    {
        if (cursor is null || cursor.MessageSortTime <= 0 || timeExpression == "0")
            return IcalinguaFilterClause.Empty;

        var cursorSourceIndex = (int)((cursor.MessageId >> 48) & 0xFFFF);
        var cursorRowId = cursor.MessageId & 0x0000_FFFF_FFFF_FFFFL;
        var sameTimePredicate = CreateSameTimePositionPredicate(
            IcalinguaMessagePositionKind.Older,
            source.SourceIndex,
            cursorSourceIndex);
        var bindRowId = source.SourceIndex == cursorSourceIndex;

        return new IcalinguaFilterClause(
            $"({timeExpression} < $searchCursorTime OR ({timeExpression} = $searchCursorTime AND {sameTimePredicate}))",
            command =>
            {
                AddParameter(command, "$searchCursorTime", cursor.MessageSortTime);
                if (bindRowId)
                    AddParameter(command, "$positionRowId", cursorRowId);
            });
    }

    private static string EscapeLike(string value)
    {
        return value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
    }

    // 所有消息查询都先规整成同一组列，后续显示层不需要关心原始表结构差异。
    private static string CreateMessageSelectSql(IcalinguaMessageSource source)
    {
        return
            $"""
            SELECT rowid AS __rowid,
                   {SelectColumn(source.Columns, "roomId", source.RoomId.ToString(CultureInfo.InvariantCulture))} AS __room_id,
                   {SelectColumn(source.Columns, "_id")},
                   {SelectColumn(source.Columns, "senderId")},
                   {SelectColumn(source.Columns, "username")},
                   {SelectColumn(source.Columns, "content")},
                   {SelectColumn(source.Columns, "file")},
                   {SelectColumn(source.Columns, "files")},
                   {CreateTimeSelectExpression(source.Columns)},
                   {CreateSortTimeSelectExpression(source.Columns)},
                   {SelectColumn(source.Columns, "replyMessage")},
                   {SelectColumn(source.Columns, "deleted", "0")},
                   {SelectColumn(source.Columns, "system", "0")},
                   {SelectColumn(source.Columns, "mirai")},
                   {SelectColumn(source.Columns, "title")},
                   {SelectColumn(source.Columns, "recallInfo")},
                   {SelectColumn(source.Columns, "code")},
                   {SelectColumn(source.Columns, "hide", "0")},
                   {SelectColumn(source.Columns, "reveal", "0")},
                   {SelectColumn(source.Columns, "flash", "0")},
                   {SelectColumn(source.Columns, "role")},
                   {SelectColumn(source.Columns, "anonymousId")},
                   {SelectColumn(source.Columns, "anonymousflag")},
                   {SelectColumn(source.Columns, "bubble_id")},
                   {SelectColumn(source.Columns, "subid")},
                   {SelectColumn(source.Columns, "head_img")}
            FROM {QuoteIdent(source.TableName)}
            """;
    }

    private static string SelectColumn(IReadOnlySet<string> columns, string name, string fallback = "NULL")
    {
        return columns.Contains(name) ? QuoteIdent(name) : fallback;
    }

    private static string GetTimeExpression(IReadOnlySet<string> columns)
    {
        var candidates = new List<string>();
        if (columns.Contains("time"))
        {
            candidates.Add("CASE WHEN time > 10000000000 THEN time / 1000 ELSE time END");
        }

        if (columns.Contains("timestamp") && columns.Contains("date"))
            candidates.Add("CAST(strftime('%s', replace(date, '/', '-') || ' ' || timestamp, 'utc') AS INTEGER)");
        if (columns.Contains("date"))
            candidates.Add("CAST(strftime('%s', replace(date, '/', '-'), 'utc') AS INTEGER)");

        return candidates.Count switch
        {
            0 => "0",
            1 => "NULLIF(" + candidates[0] + ", 0)",
            _ => "COALESCE(NULLIF(" + string.Join(", 0), NULLIF(", candidates) + ", 0))",
        };
    }

    private static string CreateTimeSelectExpression(IReadOnlySet<string> columns)
    {
        return GetTimeExpression(columns) + " AS __time";
    }

    private static string GetSortTimeExpression(IReadOnlySet<string> columns)
    {
        if (columns.Contains("time"))
            return QuoteIdent("time");

        return GetTimeExpression(columns);
    }

    private static string CreateSortTimeSelectExpression(IReadOnlySet<string> columns)
    {
        return GetSortTimeExpression(columns) + " AS __sort_time";
    }

    private static List<string> CreateRoomPredicates(IcalinguaMessageSource source, bool includeRoomIdParameter)
    {
        return source.HasRoomIdColumn && includeRoomIdParameter
            ? [QuoteIdent("roomId") + " = $roomId"]
            : ["1 = 1"];
    }

    private static void BindRoomParameter(IDbCommand command, IcalinguaMessageSource source, long roomId)
    {
        if (source.HasRoomIdColumn)
            AddParameter(command, "$roomId", roomId);
    }

    private static IcalinguaFilterClause CreateIcalinguaFilterClause(
        MessageQueryFilter? filter,
        IcalinguaMessageSource source,
        string timeExpression)
    {
        if (filter is null || filter.IsEmpty)
            return IcalinguaFilterClause.Empty;

        var predicates = new List<string>();
        if (timeExpression != "0")
        {
            if (filter.StartTime is not null)
                predicates.Add($"{timeExpression} >= $filterStartTime");
            if (filter.EndTimeExclusive is not null)
                predicates.Add($"{timeExpression} < $filterEndTime");
        }

        var selectedDayStartTimes = filter.SelectedDayStartTimes
            .Where(dayStartTime => dayStartTime > 0)
            .Distinct()
            .OrderBy(dayStartTime => dayStartTime)
            .ToArray();
        if (selectedDayStartTimes.Length > 0 && timeExpression != "0")
        {
            var dayPredicates = selectedDayStartTimes
                .Select((_, index) => $"({timeExpression} >= $dayStart{index} AND {timeExpression} < $dayEnd{index})");
            predicates.Add("(" + string.Join(" OR ", dayPredicates) + ")");
        }

        var senderIds = filter.SenderIds
            .Where(senderId => senderId != 0)
            .Distinct()
            .ToArray();
        if (senderIds.Length > 0 && source.Columns.Contains("senderId"))
            predicates.Add($"senderId IN ({string.Join(", ", senderIds.Select((_, index) => "$sender" + index))})");

        return new IcalinguaFilterClause(
            string.Join(" AND ", predicates),
            command =>
            {
                if (filter.StartTime is { } startTime)
                    AddParameter(command, "$filterStartTime", ToSortTimeParameter(startTime, source.Columns));
                if (filter.EndTimeExclusive is { } endTime)
                    AddParameter(command, "$filterEndTime", ToSortTimeParameter(endTime, source.Columns));

                for (var i = 0; i < selectedDayStartTimes.Length; i++)
                {
                    AddParameter(command, "$dayStart" + i, ToSortTimeParameter(selectedDayStartTimes[i], source.Columns));
                    AddParameter(command, "$dayEnd" + i, ToSortTimeParameter(selectedDayStartTimes[i] + 86400, source.Columns));
                }

                for (var i = 0; i < senderIds.Length; i++)
                {
                    AddParameter(command, "$sender" + i, senderIds[i].ToString(CultureInfo.InvariantCulture));
                }
            });
    }

    private static long ToSortTimeParameter(long unixTimeSeconds, IReadOnlySet<string> columns)
    {
        return columns.Contains("time")
            ? unixTimeSeconds * 1000
            : unixTimeSeconds;
    }

    private static IcalinguaFilterClause CreateIcalinguaPositionClause(
        IcalinguaMessagePosition? position,
        IcalinguaMessageSource source,
        string timeExpression)
    {
        if (position is null)
            return IcalinguaFilterClause.Empty;

        var positionSourceIndex = (int)((position.MessageId >> 48) & 0xFFFF);
        var positionRowId = position.MessageId & 0x0000_FFFF_FFFF_FFFFL;
        var sameTimePredicate = CreateSameTimePositionPredicate(position.Kind, source.SourceIndex, positionSourceIndex);
        var bindRowId = source.SourceIndex == positionSourceIndex;

        if (timeExpression == "0")
        {
            return new IcalinguaFilterClause(
                sameTimePredicate,
                command =>
                {
                    if (bindRowId)
                        AddParameter(command, "$positionRowId", positionRowId);
                });
        }

        return new IcalinguaFilterClause(
            position.Kind == IcalinguaMessagePositionKind.Older
                ? $"({timeExpression} < $positionTime OR ({timeExpression} = $positionTime AND {sameTimePredicate}))"
                : $"({timeExpression} > $positionTime OR ({timeExpression} = $positionTime AND {sameTimePredicate}))",
            command =>
            {
                AddParameter(command, "$positionTime", position.MessageSortTime);
                if (bindRowId)
                    AddParameter(command, "$positionRowId", positionRowId);
            });
    }

    private static string CreateSameTimePositionPredicate(
        IcalinguaMessagePositionKind kind,
        int sourceIndex,
        int positionSourceIndex)
    {
        if (sourceIndex == positionSourceIndex)
        {
            return kind == IcalinguaMessagePositionKind.Older
                ? "rowid < $positionRowId"
                : "rowid > $positionRowId";
        }

        var sourceIsBeforeAnchor = sourceIndex < positionSourceIndex;
        return kind == IcalinguaMessagePositionKind.Older == sourceIsBeforeAnchor
            ? "1 = 1"
            : "0 = 1";
    }

    private static string QuoteIdent(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static string QuoteString(string value) => "'" + value.Replace("'", "''") + "'";

    // SQLite 读值比较松散，这里统一把数字、布尔、时间和 NULL 做容错转换。
    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string? NormalizeJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "null")
            return null;

        return value;
    }

    public static long CreateStableNumericId(string value)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return (long)(BitConverter.ToUInt64(bytes, 0) & 0x7FFF_FFFF_FFFF_FFFF);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static bool IsNumericString(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.All(char.IsAsciiDigit);
    }

    private static string? ReadNullableString(IDataRecord reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        return reader.GetValue(ordinal) switch
        {
            string value => value,
            long value => value.ToString(CultureInfo.InvariantCulture),
            int value => value.ToString(CultureInfo.InvariantCulture),
            double value => value.ToString(CultureInfo.InvariantCulture),
            _ => reader.GetValue(ordinal)?.ToString(),
        };
    }

    private static long ReadInt64(IDataRecord reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return 0;

        return reader.GetValue(ordinal) switch
        {
            long value => value,
            int value => value,
            short value => value,
            byte value => value,
            double value => (long)value,
            string value when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }

    private static long ReadInt64Scalar(IDbCommand command)
    {
        return command.ExecuteScalar() switch
        {
            long value => value,
            int value => value,
            short value => value,
            byte value => value,
            double value => (long)value,
            string value when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }

    private static bool ReadBoolean(IDataRecord reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return false;

        return reader.GetValue(ordinal) switch
        {
            bool value => value,
            long value => value != 0,
            int value => value != 0,
            string value when bool.TryParse(value, out var parsed) => parsed,
            string value when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed != 0,
            _ => false,
        };
    }

    private static uint ParseUInt32(string? value)
    {
        return uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static long ParseInt64(string? value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static int ClampUnixTime(long timestamp)
    {
        if (timestamp > int.MaxValue)
            return int.MaxValue;

        if (timestamp < int.MinValue)
            return int.MinValue;

        return (int)timestamp;
    }

    private static int ClampInt(long value)
    {
        if (value > int.MaxValue)
            return int.MaxValue;

        if (value < int.MinValue)
            return int.MinValue;

        return (int)value;
    }

    private static int NormalizeUnixTime(long timestamp)
    {
        if (timestamp > 10_000_000_000)
            timestamp /= 1000;

        return ClampUnixTime(timestamp);
    }

    private IcalinguaMessageRecord ReadMessage(IDataRecord reader, IcalinguaMessageSource source)
    {
        var sourceRowId = ReadInt64(reader, 0);
        var roomId = ReadInt64(reader, 1);
        if (roomId == 0)
            roomId = source.RoomId;
        var rawId = FirstNonEmpty(
            ReadNullableString(reader, 2),
            source.TableName + ":" + sourceRowId.ToString(CultureInfo.InvariantCulture));
        var senderIdText = ReadNullableString(reader, 3);
        var senderId = ParseUInt32(senderIdText);
        var username = ReadNullableString(reader, 4);
        var content = ReadNullableString(reader, 5);
        var fileJson = NormalizeJson(ReadNullableString(reader, 6));
        var filesJson = NormalizeJson(ReadNullableString(reader, 7));
        var messageTime = NormalizeUnixTime(ReadInt64(reader, 8));
        var messageSortTime = ReadInt64(reader, 9);
        if (messageSortTime <= 0)
            messageSortTime = messageTime;
        var replyMessageJson = NormalizeJson(ReadNullableString(reader, 10));
        var deleted = ReadBoolean(reader, 11);
        var system = ReadBoolean(reader, 12);
        var miraiJson = NormalizeJson(ReadNullableString(reader, 13));
        var title = ReadNullableString(reader, 14);
        var recallInfo = ReadNullableString(reader, 15);
        var code = NormalizeJson(ReadNullableString(reader, 16));
        var hide = ReadBoolean(reader, 17);
        var reveal = ReadBoolean(reader, 18);
        var flash = ReadBoolean(reader, 19);
        var role = ReadNullableString(reader, 20);
        var anonymousId = ReadNullableString(reader, 21);
        var anonymousFlag = ReadNullableString(reader, 22);
        var bubbleId = ReadNullableString(reader, 23);
        var subId = ReadNullableString(reader, 24);
        var headImage = ReadNullableString(reader, 25);
        var rowId = CreateSyntheticRowId(source.SourceIndex, sourceRowId);
        var previewText = CreatePreviewText(content, fileJson, filesJson, deleted, hide, reveal, system, recallInfo, miraiJson, code);

        return new IcalinguaMessageRecord(
            rawId,
            rowId,
            CreateStableNumericId(rawId),
            roomId,
            senderId,
            FirstNonEmpty(username, senderId == 0 ? null : senderId.ToString(CultureInfo.InvariantCulture)),
            content ?? string.Empty,
            fileJson,
            filesJson,
            messageTime,
            messageSortTime,
            deleted,
            hide,
            reveal,
            flash,
            system,
            replyMessageJson,
            miraiJson,
            title,
            recallInfo,
            code,
            role,
            anonymousId,
            anonymousFlag,
            bubbleId,
            subId,
            headImage,
            previewText);
    }

    private static IReadOnlyList<IcalinguaMessageRecord> OrderAndLimitMessages(
            IEnumerable<IcalinguaMessageRecord> messages,
            IcalinguaMessagePosition? position,
            string orderBy,
            int pageSize)
    {
        var filtered = DeduplicateMessages(messages);
        if (position is { } messagePosition)
        {
            filtered = filtered.Where(message => IsMessageOnRequestedSide(message, messagePosition));
        }

        var ordered = string.Equals(orderBy, "DESC", StringComparison.OrdinalIgnoreCase)
            ? filtered
                .OrderByDescending(static message => message.MessageSeq)
                .ThenByDescending(static message => message.MessageId)
            : filtered
                .OrderBy(static message => message.MessageSeq)
                .ThenBy(static message => message.MessageId);

        return ordered
            .Take(Math.Max(1, pageSize))
            .ToArray();
    }

    private static IEnumerable<IcalinguaMessageRecord> DeduplicateMessages(IEnumerable<IcalinguaMessageRecord> messages)
    {
        var seenRawIds = new HashSet<string>(StringComparer.Ordinal);
        var seenRows = new HashSet<(long RoomId, int MessageTime, uint SenderId, string Content)>();

        foreach (var message in messages)
        {
            if (!string.IsNullOrWhiteSpace(message.RawId) && !seenRawIds.Add(message.RawId))
                continue;

            var rawIdIsSynthetic = message.RawId.Contains(':', StringComparison.Ordinal);
            var rowKey = (message.RoomId, message.MessageTime, message.SenderId, message.Content);
            if (rawIdIsSynthetic && !seenRows.Add(rowKey))
                continue;

            yield return message;
        }
    }

    private static IEnumerable<IcalinguaDateRow> DeduplicateDateRows(IEnumerable<IcalinguaDateRow> rows)
    {
        var seenRawIds = new HashSet<string>(StringComparer.Ordinal);
        var seenRows = new HashSet<(int MessageTime, uint SenderId, string Content)>();

        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.RawId) && !seenRawIds.Add(row.RawId))
                continue;

            var rowKey = (row.MessageTime, row.SenderId, row.Content);
            if (string.IsNullOrWhiteSpace(row.RawId) && !seenRows.Add(rowKey))
                continue;

            yield return row;
        }
    }

    private static int GetLocalDayStartUnixTime(int messageTime)
    {
        var localDate = DateTimeOffset.FromUnixTimeSeconds(messageTime).LocalDateTime.Date;
        return ClampUnixTime(new DateTimeOffset(localDate).ToUnixTimeSeconds());
    }

    private static bool IsMessageOnRequestedSide(IcalinguaMessageRecord message, IcalinguaMessagePosition position)
    {
        return position.Kind == IcalinguaMessagePositionKind.Older
            ? message.MessageSeq < position.MessageSortTime ||
              message.MessageSeq == position.MessageSortTime && message.MessageId < position.MessageId
            : message.MessageSeq > position.MessageSortTime ||
              message.MessageSeq == position.MessageSortTime && message.MessageId > position.MessageId;
    }

    private static long CreateSyntheticRowId(int sourceIndex, long rowId)
    {
        return ((long)sourceIndex << 48) | (rowId & 0x0000_FFFF_FFFF_FFFFL);
    }

    private static bool TryDecodeSyntheticRowId(long syntheticRowId, out int sourceIndex, out long rowId)
    {
        sourceIndex = (int)((syntheticRowId >> 48) & 0xFFFF);
        rowId = syntheticRowId & 0x0000_FFFF_FFFF_FFFFL;
        return sourceIndex > 0;
    }

    // 对外的会话和时间线读取入口。多表结果会在 reader 内部排序、去重后再返回。
    public IReadOnlyList<IcalinguaConversation> GetConversations()
    {
        EnsureConnection();
        if (_conversationCache is { } cachedConversations)
            return cachedConversations;

        var conversations = new Dictionary<long, IcalinguaConversation>();
        if (HasTable("rooms"))
        {
            foreach (var room in ReadRooms())
            {
                conversations[room.RoomId] = new IcalinguaConversation(
                    room.RoomId,
                    FirstNonEmpty(room.RoomName, room.RoomId.ToString(CultureInfo.InvariantCulture)),
                    NormalizeUnixTime(room.UpdateTime),
                    CreatePreviewText(room.LastMessageJson),
                    room.RoomId < 0,
                    room.DownloadPath);
            }
        }

        foreach (var roomId in GetMessageRoomIds())
        {
            if (conversations.ContainsKey(roomId))
                continue;

            var latest = GetLatestMessage(roomId);
            conversations[roomId] = new IcalinguaConversation(
                roomId,
                roomId.ToString(CultureInfo.InvariantCulture),
                latest?.MessageTime ?? 0,
                latest?.PreviewText ?? string.Empty,
                roomId < 0,
                null);
        }

        _conversationCache = conversations.Values
            .OrderByDescending(conversation => conversation.LatestMessageTime)
            .ThenBy(conversation => conversation.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return _conversationCache;
    }

    public IReadOnlyDictionary<long, IcalinguaConversation> GetConversationsByRoomIds(IEnumerable<long> roomIds)
    {
        EnsureConnection();
        var requestedRoomIds = roomIds
            .Where(static roomId => roomId != 0)
            .Distinct()
            .ToArray();
        if (requestedRoomIds.Length == 0)
            return new Dictionary<long, IcalinguaConversation>();

        if (_conversationCache is { } cachedConversations)
        {
            return cachedConversations
                .Where(conversation => requestedRoomIds.Contains(conversation.RoomId))
                .GroupBy(static conversation => conversation.RoomId)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.First());
        }

        var conversations = ReadRoomConversationsByIds(requestedRoomIds);
        foreach (var roomId in requestedRoomIds)
        {
            if (conversations.ContainsKey(roomId))
                continue;

            conversations[roomId] = new IcalinguaConversation(
                roomId,
                roomId.ToString(CultureInfo.InvariantCulture),
                0,
                string.Empty,
                roomId < 0,
                null);
        }

        return conversations;
    }

    private Dictionary<long, IcalinguaConversation> ReadRoomConversationsByIds(IReadOnlyList<long> roomIds)
    {
        var conversations = new Dictionary<long, IcalinguaConversation>();
        if (!HasTable("rooms"))
            return conversations;

        var columns = GetTableColumns("rooms");
        if (!columns.Contains("roomId"))
            return conversations;

        var parameterNames = roomIds
            .Select(static (_, index) => "$roomId" + index.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        using var command = _connection!.CreateCommand();
        command.CommandText =
            $"""
            SELECT {SelectColumn(columns, "roomId")},
                   {SelectColumn(columns, "roomName")},
                   {SelectColumn(columns, "utime")},
                   {SelectColumn(columns, "lastMessage")},
                   {SelectColumn(columns, "downloadPath")}
            FROM rooms
            WHERE {QuoteIdent("roomId")} IN ({string.Join(", ", parameterNames)})
            """;
        for (var index = 0; index < roomIds.Count; index++)
        {
            AddParameter(command, parameterNames[index], roomIds[index]);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var roomId = ParseInt64(ReadNullableString(reader, 0));
            if (roomId == 0)
                continue;

            conversations[roomId] = new IcalinguaConversation(
                roomId,
                FirstNonEmpty(ReadNullableString(reader, 1), roomId.ToString(CultureInfo.InvariantCulture)),
                NormalizeUnixTime(ReadInt64(reader, 2)),
                CreatePreviewText(NormalizeJson(ReadNullableString(reader, 3))),
                roomId < 0,
                ReadNullableString(reader, 4));
        }

        return conversations;
    }

    public IReadOnlyList<IcalinguaMessageRecord> LoadLatestMessages(
            long roomId,
            int pageSize,
            MessageQueryFilter? filter = null)
    {
        return QueryMessages(
                roomId,
                filter,
                position: null,
                orderBy: "DESC",
                pageSize)
            .OrderBy(message => message.MessageSeq)
            .ThenBy(message => message.MessageId)
            .ToArray();
    }

    public IReadOnlyList<IcalinguaMessageRecord> LoadEarliestMessages(
        long roomId,
        int pageSize,
        MessageQueryFilter? filter = null)
    {
        return QueryMessages(
            roomId,
            filter,
            position: null,
            orderBy: "ASC",
            pageSize);
    }

    public IReadOnlyList<IcalinguaMessageRecord> LoadOlderMessages(
        long roomId,
        long messageSortTime,
        long rowId,
        int pageSize,
        MessageQueryFilter? filter = null)
    {
        return QueryMessages(
            roomId,
            filter,
            position: new IcalinguaMessagePosition(IcalinguaMessagePositionKind.Older, messageSortTime, rowId),
            orderBy: "DESC",
            pageSize);
    }

    public IReadOnlyList<IcalinguaMessageRecord> LoadNewerMessages(
        long roomId,
        long messageSortTime,
        long rowId,
        int pageSize,
        MessageQueryFilter? filter = null)
    {
        return QueryMessages(
            roomId,
            filter,
            position: new IcalinguaMessagePosition(IcalinguaMessagePositionKind.Newer, messageSortTime, rowId),
            orderBy: "ASC",
            pageSize);
    }

    public IcalinguaMessageRecord? LoadMessage(long roomId, long messageSortTime, long rowId)
    {
        EnsureConnection();

        var sources = ResolveMessageSources(roomId);
        if (sources.Count == 0)
            return null;

        if (TryDecodeSyntheticRowId(rowId, out var sourceIndex, out var sourceRowId) &&
            sources.FirstOrDefault(candidate => candidate.SourceIndex == sourceIndex) is { } resolvedSource)
        {
            return LoadMessageFromSource(resolvedSource, sourceRowId);
        }

        foreach (var source in sources)
        {
            if (LoadMessageFromSource(source, rowId) is { } message &&
                (messageSortTime <= 0 || message.MessageSeq == messageSortTime))
            {
                return message;
            }
        }

        return null;
    }

    public IcalinguaMessageRecord? LoadMessageByRawId(long roomId, string? rawId)
    {
        EnsureConnection();
        if (string.IsNullOrWhiteSpace(rawId))
            return null;

        var sources = ResolveMessageSources(roomId)
            .Where(static source => source.Columns.Contains("_id"))
            .ToArray();
        if (sources.Length == 0)
            return null;

        foreach (var source in sources)
        {
            using var command = _connection!.CreateCommand();
            command.CommandText =
                $"""
                {CreateMessageSelectSql(source)}
                WHERE {string.Join(" AND ", CreateRoomPredicates(source, includeRoomIdParameter: true))}
                  AND "_id" = $rawId
                LIMIT 1
                """;
            BindRoomParameter(command, source, roomId);
            AddParameter(command, "$rawId", rawId);

            using var reader = command.ExecuteReader();
            if (reader.Read())
                return ReadMessage(reader, source);
        }

        return null;
    }

    private IcalinguaMessageRecord? GetLatestMessage(long roomId)
    {
        return QueryMessages(
                roomId,
                null,
                position: null,
                orderBy: "DESC",
                pageSize: 1)
            .FirstOrDefault();
    }

    private IReadOnlyList<IcalinguaMessageRecord> QueryMessages(
        long roomId,
        MessageQueryFilter? filter,
        IcalinguaMessagePosition? position,
        string orderBy,
        int pageSize)
    {
        EnsureConnection();
        var sources = ResolveMessageSources(roomId);
        if (sources.Count == 0)
            return [];

        var messages = new List<IcalinguaMessageRecord>();
        foreach (var source in sources)
        {
            var sortTimeExpression = GetSortTimeExpression(source.Columns);
            var filterClause = CreateIcalinguaFilterClause(filter, source, sortTimeExpression);
            var positionClause = CreateIcalinguaPositionClause(position, source, sortTimeExpression);
            var predicates = CreateRoomPredicates(source, includeRoomIdParameter: true);
            if (!string.IsNullOrWhiteSpace(filterClause.Predicate))
                predicates.Add(filterClause.Predicate);
            if (!string.IsNullOrWhiteSpace(positionClause.Predicate))
                predicates.Add(positionClause.Predicate);

            using var command = _connection!.CreateCommand();
            command.CommandText =
                $"""
                {CreateMessageSelectSql(source)}
                WHERE {string.Join(" AND ", predicates)}
                ORDER BY {sortTimeExpression} {orderBy}, rowid {orderBy}
                LIMIT $limit
                """;
            BindRoomParameter(command, source, roomId);
            filterClause.Bind(command);
            positionClause.Bind(command);
            AddParameter(command, "$limit", Math.Max(1, pageSize));

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                messages.Add(ReadMessage(reader, source));
            }
        }

        return OrderAndLimitMessages(messages, position, orderBy, pageSize);
    }

    private IcalinguaMessageRecord? LoadMessageFromSource(IcalinguaMessageSource source, long sourceRowId)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText =
            $"""
            {CreateMessageSelectSql(source)}
            WHERE {string.Join(" AND ", CreateRoomPredicates(source, includeRoomIdParameter: true))}
              AND rowid = $rowId
            LIMIT 1
            """;
        BindRoomParameter(command, source, source.RoomId);
        AddParameter(command, "$rowId", sourceRowId);

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? ReadMessage(reader, source)
            : null;
    }

    // 筛选弹窗需要的日期和发送人选项，按当前房间从实际消息表里聚合。
    public IReadOnlyList<IcalinguaMessageDate> LoadMessageDates(long roomId)
    {
        EnsureConnection();
        var sources = ResolveMessageSources(roomId);
        if (sources.Count == 0)
            return [];

        if (sources.Count > 1)
            return LoadDeduplicatedMessageDates(sources);

        var source = sources[0];
        var days = new Dictionary<int, int>();
        var timeExpression = GetTimeExpression(source.Columns);
        var sortTimeExpression = GetSortTimeExpression(source.Columns);
        if (timeExpression == "0")
            return [];

        var predicates = CreateRoomPredicates(source, includeRoomIdParameter: true);
        using var command = _connection!.CreateCommand();
        command.CommandText =
            $"""
            SELECT strftime('%s', date({timeExpression}, 'unixepoch', 'localtime'), 'utc') AS DayStart,
                   COUNT(*) AS MessageCount
            FROM {QuoteIdent(source.TableName)}
            WHERE {string.Join(" AND ", predicates)}
              AND {sortTimeExpression} > 0
            GROUP BY date({timeExpression}, 'unixepoch', 'localtime')
            ORDER BY DayStart
            """;
        BindRoomParameter(command, source, roomId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var dayStart = ClampUnixTime(ReadInt64(reader, 0));
            if (dayStart <= 0)
                continue;

            days[dayStart] = ClampInt(ReadInt64(reader, 1));
        }

        return days
            .OrderBy(static item => item.Key)
            .Select(static item => new IcalinguaMessageDate(item.Key, item.Value))
            .ToArray();
    }

    private IReadOnlyList<IcalinguaMessageDate> LoadDeduplicatedMessageDates(IReadOnlyList<IcalinguaMessageSource> sources)
    {
        var rows = new List<IcalinguaDateRow>();
        foreach (var source in sources)
        {
            var timeExpression = GetTimeExpression(source.Columns);
            var sortTimeExpression = GetSortTimeExpression(source.Columns);
            if (timeExpression == "0")
                continue;

            var predicates = CreateRoomPredicates(source, includeRoomIdParameter: true);
            using var command = _connection!.CreateCommand();
            command.CommandText =
                $"""
                SELECT rowid AS __rowid,
                       {SelectColumn(source.Columns, "_id")},
                       {SelectColumn(source.Columns, "senderId")},
                       {SelectColumn(source.Columns, "content")},
                       {timeExpression} AS __time
                FROM {QuoteIdent(source.TableName)}
                WHERE {string.Join(" AND ", predicates)}
                  AND {sortTimeExpression} > 0
                ORDER BY {sortTimeExpression} ASC, rowid ASC
                """;
            BindRoomParameter(command, source, source.RoomId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var time = NormalizeUnixTime(ReadInt64(reader, 4));
                if (time <= 0)
                    continue;

                rows.Add(new IcalinguaDateRow(
                    ReadNullableString(reader, 1) ?? string.Empty,
                    time,
                    ParseUInt32(ReadNullableString(reader, 2)),
                    ReadNullableString(reader, 3) ?? string.Empty));
            }
        }

        var days = new Dictionary<int, int>();
        foreach (var row in DeduplicateDateRows(rows))
        {
            var dayStart = GetLocalDayStartUnixTime(row.MessageTime);
            days[dayStart] = days.TryGetValue(dayStart, out var count) ? count + 1 : 1;
        }

        return days
            .OrderBy(static item => item.Key)
            .Select(static item => new IcalinguaMessageDate(item.Key, item.Value))
            .ToArray();
    }

    public IReadOnlyList<IcalinguaSender> LoadSenders(long roomId)
    {
        EnsureConnection();
        var sources = ResolveMessageSources(roomId)
            .Where(static source => source.Columns.Contains("senderId"))
            .ToArray();
        if (sources.Length == 0)
            return [];

        var senders = new Dictionary<uint, string>();
        foreach (var source in sources)
        {
            var usernameProjection = source.Columns.Contains("username")
                ? "MAX(NULLIF(username, ''))"
                : "NULL";
            var predicates = CreateRoomPredicates(source, includeRoomIdParameter: true);
            using var command = _connection!.CreateCommand();
            command.CommandText =
                $"""
                SELECT senderId AS SenderId,
                       {usernameProjection} AS Username
                FROM {QuoteIdent(source.TableName)}
                WHERE {string.Join(" AND ", predicates)}
                  AND senderId IS NOT NULL
                  AND senderId != ''
                  AND senderId != '0'
                GROUP BY senderId
                ORDER BY Username COLLATE NOCASE, SenderId
                """;
            BindRoomParameter(command, source, roomId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var senderId = unchecked((uint)ReadInt64(reader, 0));
                if (senderId == 0)
                    continue;

                var displayName = FirstNonEmpty(ReadNullableString(reader, 1), senderId.ToString(CultureInfo.InvariantCulture));
                if (!senders.ContainsKey(senderId) || IsNumericString(senders[senderId]))
                    senders[senderId] = displayName;
            }
        }

        return senders
            .Select(static item => new IcalinguaSender(item.Key, item.Value))
            .OrderBy(static sender => sender.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static sender => sender.SenderId)
            .ToArray();
    }

    // 搜索入口保留在 reader 里，查询层只返回规整后的消息记录。
    public long CountSearchMatches(string keyword, long? roomId)
    {
        EnsureConnection();
        if (string.IsNullOrWhiteSpace(keyword))
            return 0;

        return ResolveSearchRoomIds(roomId)
            .Sum(searchRoomId => CountSearchMatchesInRoom(searchRoomId, keyword));
    }

    public long CountSearchMatchesInRoom(long roomId, string keyword)
    {
        EnsureConnection();
        if (string.IsNullOrWhiteSpace(keyword))
            return 0;

        return CountSearchMatchesInRoomCore(roomId, keyword);
    }

    public IReadOnlyList<IcalinguaMessageSearchGroupCount> CountSearchMatchesByRoom(
        string keyword,
        long? roomId,
        int? maxRooms = null)
    {
        EnsureConnection();
        if (string.IsNullOrWhiteSpace(keyword))
            return [];

        var conversations = LoadConversationMap();
        return ResolveSearchRoomIds(roomId)
            .Select(searchRoomId => new IcalinguaMessageSearchGroupCount(
                searchRoomId,
                CountSearchMatchesInRoomCore(searchRoomId, keyword),
                conversations.TryGetValue(searchRoomId, out var conversation)
                    ? conversation.DisplayName
                    : GetRoomDisplayName(searchRoomId)))
            .Where(static result => result.MatchCount > 0)
            .OrderByDescending(static result => result.MatchCount)
            .ThenBy(static result => result.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(maxRooms.GetValueOrDefault(int.MaxValue))
            .ToArray();
    }

    public IReadOnlyList<IcalinguaMessageRecord> SearchMessages(
        string keyword,
        long? roomId,
        int pageSize,
        IcalinguaMessageSearchCursor? cursor = null)
    {
        EnsureConnection();
        if (string.IsNullOrWhiteSpace(keyword))
            return [];

        if (roomId is null && _hasUnifiedMessagesTable)
            return SearchUnifiedMessages(keyword, cursor, pageSize);

        var roomIds = ResolveSearchRoomIds(roomId)
            .ToArray();
        if (roomIds.Length == 0)
            return [];

        var messages = new List<IcalinguaMessageRecord>();
        foreach (var searchRoomId in roomIds)
        {
            messages.AddRange(SearchMessagesInRoom(searchRoomId, keyword, cursor, pageSize));
        }

        return DeduplicateMessages(messages)
            .Where(message => cursor is null || IsBeforeSearchCursor(message, cursor))
            .OrderByDescending(static message => message.MessageSeq)
            .ThenByDescending(static message => message.MessageId)
            .Take(Math.Max(1, pageSize))
            .ToArray();
    }

    private IReadOnlyList<IcalinguaMessageRecord> SearchUnifiedMessages(
        string keyword,
        IcalinguaMessageSearchCursor? cursor,
        int pageSize)
    {
        var source = new IcalinguaMessageSource(
            UnifiedMessagesTableName,
            0,
            true,
            GetTableColumns(UnifiedMessagesTableName),
            0);
        if (!HasSearchableColumns(source.Columns))
            return [];

        var sortTimeExpression = GetSortTimeExpression(source.Columns);
        var searchClause = CreateSearchClause(keyword, source);
        var predicates = new List<string> { QuoteIdent("roomId") + " != 0", searchClause.Predicate };
        var cursorClause = CreateSearchCursorClause(cursor, source, sortTimeExpression);
        if (!string.IsNullOrWhiteSpace(cursorClause.Predicate))
            predicates.Add(cursorClause.Predicate);

        using var command = _connection!.CreateCommand();
        command.CommandText =
            $"""
            {CreateMessageSelectSql(source)}
            WHERE {string.Join(" AND ", predicates)}
            ORDER BY {sortTimeExpression} DESC, rowid DESC
            LIMIT $limit
            """;
        searchClause.Bind(command);
        cursorClause.Bind(command);
        AddParameter(command, "$limit", Math.Max(1, pageSize));

        var messages = new List<IcalinguaMessageRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            messages.Add(ReadMessage(reader, source));
        }

        return messages;
    }

    public IReadOnlyList<IcalinguaMessageRecord> SearchMessagesInRoom(
        long roomId,
        string keyword,
        IcalinguaMessageSearchCursor? cursor,
        int pageSize)
    {
        EnsureConnection();
        if (string.IsNullOrWhiteSpace(keyword))
            return [];

        var sources = ResolveMessageSources(roomId);
        if (sources.Count == 0)
            return [];

        var messages = new List<IcalinguaMessageRecord>();
        foreach (var source in sources)
        {
            if (!HasSearchableColumns(source.Columns))
                continue;

            var sortTimeExpression = GetSortTimeExpression(source.Columns);
            var searchClause = CreateSearchClause(keyword, source);
            var predicates = CreateRoomPredicates(source, includeRoomIdParameter: true);
            predicates.Add(searchClause.Predicate);
            var cursorClause = CreateSearchCursorClause(cursor, source, sortTimeExpression);
            if (!string.IsNullOrWhiteSpace(cursorClause.Predicate))
                predicates.Add(cursorClause.Predicate);

            using var command = _connection!.CreateCommand();
            command.CommandText =
                $"""
                {CreateMessageSelectSql(source)}
                WHERE {string.Join(" AND ", predicates)}
                ORDER BY {sortTimeExpression} DESC, rowid DESC
                LIMIT $limit
                """;
            BindRoomParameter(command, source, roomId);
            searchClause.Bind(command);
            cursorClause.Bind(command);
            AddParameter(command, "$limit", Math.Max(1, pageSize));

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                messages.Add(ReadMessage(reader, source));
            }
        }

        return messages
            .Where(message => cursor is null || IsBeforeSearchCursor(message, cursor))
            .OrderByDescending(static message => message.MessageSeq)
            .ThenByDescending(static message => message.MessageId)
            .Take(Math.Max(1, pageSize))
            .ToArray();
    }

    private long CountSearchMatchesInRoomCore(long roomId, string keyword)
    {
        var sources = ResolveMessageSources(roomId);
        if (sources.Count == 0)
            return 0;

        long count = 0;
        foreach (var source in sources)
        {
            if (!HasSearchableColumns(source.Columns))
                continue;

            var searchClause = CreateSearchClause(keyword, source);
            var predicates = CreateRoomPredicates(source, includeRoomIdParameter: true);
            predicates.Add(searchClause.Predicate);

            using var command = _connection!.CreateCommand();
            command.CommandText =
                $"""
                SELECT COUNT(1)
                FROM {QuoteIdent(source.TableName)}
                WHERE {string.Join(" AND ", predicates)}
                """;
            BindRoomParameter(command, source, roomId);
            searchClause.Bind(command);

            count += ReadInt64Scalar(command);
        }

        return count;
    }

    private IReadOnlyList<long> ResolveSearchRoomIds(long? roomId)
    {
        if (roomId is { } requestedRoomId && requestedRoomId != 0)
        {
            return CreateRequestedRoomCandidates(requestedRoomId)
                .Where(candidate => ResolveMessageSources(candidate).Count != 0)
                .Distinct()
                .ToArray();
        }

        return GetMessageRoomIds()
            .OrderByDescending(GetRoomLatestSortTime)
            .ThenBy(static value => value)
            .ToArray();
    }

    private static IEnumerable<long> CreateRequestedRoomCandidates(long roomId)
    {
        yield return roomId;

        if (roomId > 0)
            yield return -roomId;
    }

    private long GetRoomLatestSortTime(long roomId)
    {
        return GetLatestMessage(roomId)?.MessageSeq ?? 0;
    }

    private Dictionary<long, IcalinguaConversation> LoadConversationMap()
    {
        return GetConversations()
            .GroupBy(static conversation => conversation.RoomId)
            .ToDictionary(
                static group => group.Key,
                static group => group.First());
    }

    private static string GetRoomDisplayName(long roomId)
    {
        return roomId < 0
            ? (-roomId).ToString(CultureInfo.InvariantCulture)
            : roomId.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsBeforeSearchCursor(IcalinguaMessageRecord message, IcalinguaMessageSearchCursor cursor)
    {
        return message.MessageSeq < cursor.MessageSortTime ||
               message.MessageSeq == cursor.MessageSortTime && message.MessageId < cursor.MessageId;
    }

    private static readonly Regex IcalinguaMarkdownImageRegex = new(
            @"!\[[^\]]*\]\((?<url>.*?)\)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IcalinguaXmlMd5ImageRegex = new(
        @"image [^<>]*md5=""(?<md5>[A-Fa-f\d]{32})""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string CreateFilePreviewText(string? fileJson, string? filesJson, string? code = null, string? content = null)
    {
        foreach (var file in ParseFiles(fileJson, filesJson, code, content))
            return CreateFilePreviewText(file);

        return string.Empty;
    }

    private static string CreateFilePreviewText(IcalinguaMessageFile file)
    {
        var type = file.Type;
        var name = file.Name;
        var display = type switch
        {
            { } value when value.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && file.IsFace => "[动画表情]",
            { } value when value.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => "[图片]",
            { } value when value.StartsWith("video/", StringComparison.OrdinalIgnoreCase) => "[视频]",
            { } value when value.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => "[语音]",
            _ => "[文件]",
        };

        return string.Equals(display, "[文件]", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(name)
            ? $"{display} {name}"
            : display;
    }

    private static IcalinguaMessageFile CreateFile(JsonElement file, bool contentHasSticker)
    {
        var type = GetString(file, "type");
        var url = NormalizeStructuredUrl(GetString(file, "url"));
        return new IcalinguaMessageFile(
            type,
            url,
            GetString(file, "name"),
            GetString(file, "fid"),
            GetBoolean(file, "isFace") || IsIcalinguaStickerFile(type, url, contentHasSticker),
            GetInt32(file, "width"),
            GetInt32(file, "height"),
            GetInt64(file, "size"));
    }

    private static bool IsIcalinguaStickerFile(string? type, string? url, bool contentHasSticker)
    {
        if (string.IsNullOrWhiteSpace(type) ||
            !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (contentHasSticker)
            return true;

        if (type.Equals("image/webp", StringComparison.OrdinalIgnoreCase))
            return true;

        return url is not null &&
               (url.Contains("gxh.vip.qq.com/club/item/parcel/item/", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("/club/item/parcel/item/", StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetIcalinguaStickerSuffix(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var displayContent = CreateDisplayContentText(content);
        if (displayContent is null)
            return null;

        var trimmed = displayContent.Trim();
        return trimmed.StartsWith("[Sticker]", StringComparison.OrdinalIgnoreCase)
            ? trimmed["[Sticker]".Length..].Trim()
            : null;
    }

    public static IReadOnlyList<IcalinguaMessageFile> ParseFiles(
        string? fileJson,
        string? filesJson,
        string? code = null,
        string? content = null)
    {
        var contentHasSticker = GetIcalinguaStickerSuffix(content) is not null;
        var files = EnumerateFiles(fileJson, filesJson)
            .Select(file => CreateFile(file, contentHasSticker))
            .Concat(ParseCodeFiles(code))
            .Concat(ParseMarkdownContentFiles(content));
        var result = new List<IcalinguaMessageFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var key = FirstNonEmpty(file.Url, file.Name, file.Fid);
            if (string.IsNullOrWhiteSpace(key))
            {
                result.Add(file);
            }
            else if (seen.Add(key))
            {
                result.Add(file);
            }
        }

        return result;
    }

    private static IEnumerable<IcalinguaMessageFile> ParseMarkdownContentFiles(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            yield break;

        var markerIndex = content.IndexOf(MarkdownContentMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
            yield break;

        var markdown = content[(markerIndex + MarkdownContentMarker.Length)..];
        foreach (Match match in IcalinguaMarkdownImageRegex.Matches(markdown))
        {
            var url = NormalizeStructuredUrl(match.Groups["url"].Value);
            if (string.IsNullOrWhiteSpace(url))
                continue;

            yield return new IcalinguaMessageFile("image/jpeg", url, null, null, false, null, null, null);
        }
    }

    private static IEnumerable<IcalinguaMessageFile> ParseCodeFiles(string? rawCode)
    {
        if (string.IsNullOrWhiteSpace(rawCode))
            yield break;

        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(rawCode);
        }
        catch
        {
        }

        using (document)
        {
            if (document is not null)
            {
                foreach (var file in ParseStructuredJsonFiles(document.RootElement))
                    yield return file;
                yield break;
            }
        }

        foreach (var file in ParseXmlFiles(rawCode))
            yield return file;
    }

    private static IEnumerable<IcalinguaMessageFile> ParseStructuredJsonFiles(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        var app = GetString(root, "app");
        if (TryGetNestedProperty(root, out var mannounce, "meta", "mannounce", "pic") &&
            mannounce.ValueKind == JsonValueKind.Array)
        {
            foreach (var pic in mannounce.EnumerateArray())
            {
                var url = GetString(pic, "url");
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                yield return new IcalinguaMessageFile(
                    "image/jpeg",
                    "https://gdynamic.qpic.cn/gdynamic/" + url + "/0",
                    null,
                    null,
                    false,
                    null,
                    null,
                    null);
            }
        }

        if ((string.Equals(app, "com.tencent.groupphoto", StringComparison.Ordinal) ||
             string.Equals(app, "com.tencent.qzone.albumShare", StringComparison.Ordinal)) &&
            TryGetNestedProperty(root, out var pics, "meta", "albumData", "pics") &&
            pics.ValueKind == JsonValueKind.Array)
        {
            foreach (var pic in pics.EnumerateArray())
            {
                var url = NormalizeStructuredUrl(GetString(pic, "url"));
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                yield return new IcalinguaMessageFile("image/jpeg", url, null, null, false, null, null, null);
            }
        }

        if (TryGetStructuredPreviewUrl(root) is { Length: > 0 } previewUrl)
            yield return new IcalinguaMessageFile("image/jpeg", previewUrl, null, null, false, null, null, null);
    }

    private static IEnumerable<IcalinguaMessageFile> ParseXmlFiles(string xml)
    {
        if (IcalinguaXmlMd5ImageRegex.Match(xml) is { Success: true } md5Match)
        {
            yield return new IcalinguaMessageFile(
                "image/jpeg",
                GetImageUrlByMd5(md5Match.Groups["md5"].Value),
                null,
                null,
                false,
                null,
                null,
                null);
        }
    }

    private static string GetImageUrlByMd5(string md5)
    {
        return "https://gchat.qpic.cn/gchatpic_new/0/0-0-" + md5.ToUpperInvariant() + "/0";
    }

    private static IEnumerable<JsonElement> EnumerateFiles(string? fileJson, string? filesJson)
    {
        foreach (var item in EnumerateFileElements(fileJson))
            yield return item;
        foreach (var item in EnumerateFileElements(filesJson))
            yield return item;
    }

    private static IEnumerable<JsonElement> EnumerateFileElements(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null")
            yield break;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch
        {
            yield break;
        }

        using (document)
        {
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                        yield return item.Clone();
                }
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                yield return document.RootElement.Clone();
            }
        }
    }

    private static string CreateStructuredJsonPreviewText(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return string.Empty;

        var app = GetString(root, "app");
        if (string.Equals(app, "com.tencent.mannounce", StringComparison.Ordinal) &&
            TryGetNestedProperty(root, out var mannounce, "meta", "mannounce"))
        {
            return FirstNonEmpty(
                TryBase64Decode(GetString(mannounce, "title")),
                TrimPromptPrefix(GetString(root, "prompt")),
                "[公告]");
        }

        if (string.Equals(app, "com.tencent.multimsg", StringComparison.Ordinal) &&
            TryGetNestedProperty(root, out var detail, "meta", "detail"))
        {
            var resId = GetString(detail, "resid");
            if (!string.IsNullOrWhiteSpace(resId))
                return "[聊天记录]";

            var fileName = GetString(detail, "uniseq");
            if (!string.IsNullOrWhiteSpace(fileName))
                return "[聊天记录]";
        }

        if (TryGetStructuredAppUrl(root) is { Length: > 0 } jumpUrl)
        {
            var title = string.Empty;
            var desc = string.Empty;
            if (TryGetNestedProperty(root, out var detail1, "meta", "detail_1"))
            {
                title = GetString(detail1, "title") ?? string.Empty;
                desc = GetString(detail1, "desc") ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(title) &&
                string.IsNullOrWhiteSpace(desc) &&
                TryGetNestedProperty(root, out var news, "meta", "news"))
            {
                title = GetString(news, "title") ?? string.Empty;
                desc = GetString(news, "desc") ?? string.Empty;
            }

            return JoinPreviewLines(
                FirstNonEmpty(title, TrimPromptPrefix(GetString(root, "prompt"))),
                desc,
                jumpUrl);
        }

        if (string.Equals(app, "com.tencent.groupphoto", StringComparison.Ordinal) ||
            string.Equals(app, "com.tencent.qzone.albumShare", StringComparison.Ordinal))
        {
            return FirstNonEmpty("[群相册]" + TrimPromptPrefix(GetString(root, "prompt")), "[群相册]");
        }

        var prompt = GetString(root, "prompt");
        var genericUrl = NormalizeStructuredUrl(FirstNonEmpty(
            TryFindJsonStringProperty(root, "pcJumpUrl"),
            TryFindJsonStringProperty(root, "jumpUrl")));
        if (!string.IsNullOrWhiteSpace(prompt))
            return string.IsNullOrWhiteSpace(genericUrl)
                ? "[JSON]" + prompt
                : JoinPreviewLines("[JSON]" + prompt, genericUrl);

        return genericUrl ?? string.Empty;
    }

    private static string? TryGetStructuredAppUrl(JsonElement root)
    {
        var url = FirstNonEmpty(
            TryFindKnownAppUrl(root),
            NormalizeStructuredUrl(TryFindJsonStringProperty(root, "contentJumpUrl")),
            TryGetNestedProperty(root, out var detail1, "meta", "detail_1")
                ? FirstNonEmpty(
                    GetString(detail1, "qqdocurl"),
                    GetString(detail1, "qddocurl"),
                    GetString(detail1, "url"),
                    GetString(detail1, "contentJumpUrl"))
                : null,
            TryGetNestedProperty(root, out var news, "meta", "news")
                ? FirstNonEmpty(
                    GetString(news, "contentJumpUrl"),
                    GetString(news, "jumpUrl"),
                    GetString(news, "url"))
                : null,
            TryFindJsonStringProperty(root, "pcJumpUrl"),
            TryFindJsonStringProperty(root, "jumpUrl"));

        return NormalizeStructuredUrl(url);
    }

    private static string? TryFindKnownAppUrl(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (TryFindKnownAppUrl(property.Value) is { } nestedUrl)
                        return nestedUrl;
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindKnownAppUrl(item) is { } nestedUrl)
                        return nestedUrl;
                }

                break;
            case JsonValueKind.String:
                var value = NormalizeStructuredUrl(element.GetString());
                if (IsKnownAppUrl(value))
                    return value;
                break;
        }

        return null;
    }

    private static bool IsKnownAppUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains("b23.tv/", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("bilibili.com/", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("zhihu.com/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetStructuredPreviewUrl(JsonElement root)
    {
        var url = FirstNonEmpty(
            TryGetNestedProperty(root, out var detail1, "meta", "detail_1")
                ? GetString(detail1, "preview")
                : null,
            TryGetNestedProperty(root, out var news, "meta", "news")
                ? GetString(news, "preview")
                : null,
            TryFindJsonStringProperty(root, "preview"));

        return NormalizeStructuredUrl(url);
    }

    private static bool TryGetNestedProperty(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var propertyName in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(propertyName, out value))
            {
                value = default;
                return false;
            }
        }

        return true;
    }

    private static string? TryFindJsonStringProperty(JsonElement element, string propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                        return GetJsonStringValue(property.Value);

                    if (TryFindJsonStringProperty(property.Value, propertyName) is { } nestedValue)
                        return nestedValue;
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindJsonStringProperty(item, propertyName) is { } nestedValue)
                        return nestedValue;
                }

                break;
        }

        return null;
    }

    private static string? GetJsonStringValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null,
        };
    }

    private static string? NormalizeStructuredUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = WebUtility.HtmlDecode(value.Trim().Replace("\\/", "/", StringComparison.Ordinal));
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
            return "https:" + trimmed;

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return trimmed.Contains('.', StringComparison.Ordinal)
            ? "https://" + trimmed.TrimStart('/')
            : trimmed;
    }

    private static string? TrimPromptPrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        var colonIndex = trimmed.IndexOfAny(['：', ':']);
        return colonIndex >= 0 && colonIndex + 1 < trimmed.Length
            ? trimmed[(colonIndex + 1)..].Trim()
            : trimmed;
    }

    private static string JoinPreviewLines(params string?[] values)
    {
        return string.Join(
            "\n\n",
            values
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!.Trim()));
    }

    private static string? TryBase64Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch
        {
            return value;
        }
    }

    private const string MarkdownContentMarker = "\n\n[markdown]\n";
    private static readonly Regex IcalinguaAtRegex = new(
        @"<IcalinguaAt qq=\d+>(?<text>.*?)</IcalinguaAt>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex IcalinguaXmlUrlRegex = new(
        @"url=""(?<url>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IcalinguaXmlResIdRegex = new(
        @"m_resid=""(?<value>[\w+=/]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IcalinguaXmlFileNameRegex = new(
        @"m_fileName=""(?<value>[\w+\-=/]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IcalinguaXmlBriefRegex = new(
        @"brief=""(?<value>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string CreatePreviewText(
        string? content,
        string? fileJson,
        string? filesJson,
        bool deleted,
        bool hide,
        bool reveal,
        bool system,
        string? recallInfo,
        string? miraiJson,
        string? code = null)
    {
        if (deleted && !reveal)
            return FirstNonEmpty(CreateRecallPreviewText(recallInfo), "[已撤回]");

        if (hide && !reveal)
            return "[已隐藏]";

        var fileText = CreateFilePreviewText(fileJson, filesJson, code, content);
        var text = CreateDisplayMessageText(content, miraiJson, code);
        if (!string.IsNullOrWhiteSpace(text))
            return CreateContentPreviewText(text, fileText);

        if (!string.IsNullOrWhiteSpace(fileText))
            return fileText;

        return system ? "[系统消息]" : string.Empty;
    }

    public static string CreateDisplayMessageText(string? content, string? miraiJson, string? code)
    {
        var contentText = CreateDisplayContentText(content);
        var codeText = CreatePreviewText(code);
        if (ShouldPreferStructuredCodeText(code, contentText, codeText))
            return codeText;

        return FirstNonEmpty(contentText, CreatePreviewText(miraiJson), codeText);
    }

    private static bool ShouldPreferStructuredCodeText(string? code, string? contentText, string? codeText)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(codeText))
            return false;

        try
        {
            using var document = JsonDocument.Parse(code);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            var app = GetString(root, "app");
            if (string.Equals(app, "com.tencent.miniapp_01", StringComparison.Ordinal) &&
                TryGetStructuredAppUrl(root) is { Length: > 0 })
            {
                return true;
            }

            if (string.Equals(app, "com.tencent.multimsg", StringComparison.Ordinal))
                return true;

            return IsGeneratedJsonPlaceholder(contentText) &&
                   (!string.IsNullOrWhiteSpace(app) || !string.IsNullOrWhiteSpace(GetString(root, "prompt")));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsGeneratedJsonPlaceholder(string? contentText)
    {
        if (string.IsNullOrWhiteSpace(contentText))
            return false;

        var trimmed = contentText.Trim();
        return string.Equals(trimmed, "app", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "json", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "[JSON]", StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateContentPreviewText(string text, string? fileText)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var trimmed = text.Trim();
        if (!string.IsNullOrWhiteSpace(fileText) &&
            IsIcalinguaMediaPlaceholder(trimmed, out var placeholderSuffix))
        {
            return string.IsNullOrWhiteSpace(placeholderSuffix)
                ? fileText
                : fileText + " " + CreateContentPreviewText(placeholderSuffix, fileText);
        }

        if (Regex.IsMatch(trimmed, @"^\[QLottie:\s*\d+,\d+(,\d+)?\]$", RegexOptions.CultureInvariant))
            return "[动画表情]";

        if (Regex.IsMatch(trimmed, @"^\[Face:\s*\d+\]$", RegexOptions.CultureInvariant))
            return "[QQ表情]";

        if (trimmed.StartsWith("[Forward: ", StringComparison.Ordinal) ||
            trimmed.StartsWith("[NestedForward: ", StringComparison.Ordinal))
        {
            return "[聊天记录]";
        }

        var result = IcalinguaAtRegex.Replace(text, match => DecodeIcalinguaText(match.Groups["text"].Value));
        result = result.Replace("<ica:img>", FirstNonEmpty(fileText, "[图片]"), StringComparison.Ordinal);
        return result;
    }

    private static bool IsIcalinguaMediaPlaceholder(string text, out string suffix)
    {
        suffix = string.Empty;
        foreach (var placeholder in new[] { "[Image]", "[Sticker]" })
        {
            if (string.Equals(text, placeholder, StringComparison.OrdinalIgnoreCase))
                return true;

            if (text.StartsWith(placeholder, StringComparison.OrdinalIgnoreCase))
            {
                suffix = text[placeholder.Length..].Trim();
                return true;
            }
        }

        return false;
    }

    public static string CreateRecallPreviewText(string? recallInfo)
    {
        if (string.IsNullOrWhiteSpace(recallInfo) || recallInfo == "null")
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(recallInfo);
            var root = document.RootElement;
            var operatorId = GetString(root, "operator_id");
            var time = GetInt64(root, "time");
            var timeText = time is > 0
                ? DateTimeOffset
                    .FromUnixTimeMilliseconds(time.Value > 10000000000 ? time.Value : time.Value * 1000)
                    .LocalDateTime
                    .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(timeText) && !string.IsNullOrWhiteSpace(operatorId))
                return $"消息于 {timeText} 被 {operatorId} 撤回";
            if (!string.IsNullOrWhiteSpace(operatorId))
                return $"消息被 {operatorId} 撤回";
            if (!string.IsNullOrWhiteSpace(timeText))
                return $"消息于 {timeText} 被撤回";
        }
        catch
        {
        }

        return recallInfo;
    }

    public static string CreatePreviewText(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;
            return FirstNonEmpty(
                CreateStructuredJsonPreviewText(root),
                GetString(root, "content"),
                GetString(root, "title"),
                GetString(root, "desc"),
                CreateFilePreviewText(GetRawText(root, "file"), GetRawText(root, "files")));
        }
        catch
        {
            return CreateXmlPreviewText(rawJson);
        }
    }

    private static string CreateXmlPreviewText(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return string.Empty;

        if (rawText.Contains("action=\"viewMultiMsg\"", StringComparison.OrdinalIgnoreCase))
        {
            if (IcalinguaXmlResIdRegex.Match(rawText) is { Success: true } resIdMatch)
                return "[聊天记录]";
            if (IcalinguaXmlFileNameRegex.Match(rawText) is { Success: true } fileNameMatch)
                return "[聊天记录]";

            return "[聊天记录]";
        }

        if (IcalinguaXmlUrlRegex.Match(rawText) is { Success: true } urlMatch)
            return NormalizeStructuredUrl(urlMatch.Groups["url"].Value) ?? string.Empty;

        if (IcalinguaXmlMd5ImageRegex.IsMatch(rawText))
            return "[图片]";

        var brief = IcalinguaXmlBriefRegex.Match(rawText);
        return brief.Success
            ? "[XML]" + WebUtility.HtmlDecode(brief.Groups["value"].Value)
            : string.Empty;
    }

    public static string? CreateDisplayContentText(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        var markerIndex = content.IndexOf(MarkdownContentMarker, StringComparison.Ordinal);
        return markerIndex < 0
            ? content
            : content[..markerIndex];
    }

    public static IcalinguaReplyPreview? ParseReplyPreview(string? replyMessageJson)
    {
        if (string.IsNullOrWhiteSpace(replyMessageJson) || replyMessageJson == "null")
            return null;

        try
        {
            using var document = JsonDocument.Parse(replyMessageJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var rawId = FirstNonEmpty(GetString(root, "_id"), GetString(root, "id"));
            var senderId = ParseUInt32(GetString(root, "senderId"));
            var senderName = GetString(root, "username");
            var sortTime = ParseIcalinguaJsonMessageSortTime(root);
            var time = NormalizeUnixTime(sortTime);
            var content = GetString(root, "content");
            var fileJson = GetRawText(root, "file");
            var filesJson = GetRawText(root, "files");
            var code = GetRawOrStringText(root, "code");
            var mirai = GetRawOrStringText(root, "mirai");
            var files = ParseFiles(fileJson, filesJson, code, content);
            var preview = CreatePreviewText(
                content,
                fileJson,
                filesJson,
                GetBoolean(root, "deleted"),
                GetBoolean(root, "hide"),
                GetBoolean(root, "reveal"),
                GetBoolean(root, "system"),
                GetString(root, "recallInfo"),
                mirai,
                code);
            return string.IsNullOrWhiteSpace(preview)
                ? null
                : new IcalinguaReplyPreview(rawId, senderId, senderName, time, sortTime, preview, files);
        }
        catch
        {
            return null;
        }
    }

    private static long ParseIcalinguaJsonMessageSortTime(JsonElement element)
    {
        if (GetInt64(element, "time") is { } time && time != 0)
            return time;

        var date = GetString(element, "date");
        var timestamp = GetString(element, "timestamp");
        if (!string.IsNullOrWhiteSpace(date))
        {
            var dateTimeText = string.IsNullOrWhiteSpace(timestamp)
                ? date
                : date + " " + timestamp;
            if (DateTimeOffset.TryParse(
                    dateTimeText,
                    CultureInfo.CurrentCulture,
                    DateTimeStyles.AssumeLocal,
                    out var parsed))
            {
                return parsed.ToUnixTimeSeconds();
            }
        }

        return 0;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static string? GetRawText(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? property.GetRawText()
            : null;
    }

    private static string? GetRawOrStringText(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.GetRawText();
    }

    private static bool GetBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return false;

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => property.TryGetInt64(out var value) && value != 0,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) && parsed,
            _ => false,
        };
    }

    private static int? GetInt32(JsonElement element, string propertyName)
    {
        var value = GetInt64(element, propertyName);
        return value is null ? null : ClampInt(value.Value);
    }

    private static long? GetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => null,
        };
    }

    private static string DecodeIcalinguaText(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        try
        {
            return WebUtility.UrlDecode(value);
        }
        catch
        {
            return value;
        }
    }
}

