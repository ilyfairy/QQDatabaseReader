using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Google.Protobuf;
using QQDatabaseReader.Database;
using QQDatabaseReader.Sqlite;
using SQLitePCL;

namespace QQDatabaseReader;

public sealed class AndroidMobileQQMessageReader : IQQDatabase
{
    private static readonly Regex MessageTableNameRegex = new(
        @"^mr_(friend|troop)_[0-9A-F]{32}_New$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _rootPath;
    private readonly byte[] _fieldKey;
    private readonly RawDatabase? _slowRawDatabase;
    private readonly RawDatabase? _indexRawDatabase;
    private QQNTDbConnection? _connection;
    private QQNTDbConnection? _slowConnection;
    private QQNTDbConnection? _indexConnection;
    private bool _indexSearchUnavailable;
    private IReadOnlySet<string>? _mainTables;
    private IReadOnlySet<string>? _slowTables;
    private Dictionary<string, string>? _friendNames;
    private Dictionary<string, string>? _groupNames;
    private Dictionary<string, AndroidMobileQQConversation>? _conversationByTableName;
    private readonly Dictionary<string, Dictionary<string, string>> _groupMemberNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<AndroidMobileQQMessageDate>> _messageDates = new(StringComparer.Ordinal);

    public AndroidMobileQQMessageReader(string rootPath, string selfUin)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Android QQ directory is required.", nameof(rootPath));
        if (string.IsNullOrWhiteSpace(selfUin))
            throw new ArgumentException("Self UIN is required.", nameof(selfUin));

        _rootPath = Path.GetFullPath(rootPath);
        SelfUin = selfUin.Trim();
        var databaseDirectory = ResolveChildDirectory(_rootPath, "databases", "db");
        var filesDirectory = ResolveChildDirectory(_rootPath, "files", "f");
        var databaseFilePath = Path.Combine(databaseDirectory, SelfUin + ".db");
        var slowDatabaseFilePath = Path.Combine(databaseDirectory, "slowtable_" + SelfUin + ".db");
        var indexDatabaseFilePath = Path.Combine(databaseDirectory, SelfUin + "-IndexQQMsg.db");
        RawDatabase = new RawDatabase(databaseFilePath, useImmutableReadOnly: true);
        if (File.Exists(slowDatabaseFilePath))
            _slowRawDatabase = new RawDatabase(slowDatabaseFilePath, useImmutableReadOnly: true);
        if (File.Exists(indexDatabaseFilePath))
            _indexRawDatabase = new RawDatabase(indexDatabaseFilePath, useImmutableReadOnly: true);

        var keyPath = Path.Combine(filesDirectory, "kc");
        if (!File.Exists(keyPath))
            throw new FileNotFoundException("Android QQ field key file not found.", keyPath);

        _fieldKey = Encoding.UTF8.GetBytes(File.ReadAllText(keyPath).Trim());
        if (_fieldKey.Length == 0)
            throw new InvalidOperationException("Android QQ field key is empty.");
    }

    public QQDatabaseType DatabaseType => QQDatabaseType.AndroidMobileQQMessage;

    public string DatabaseFilePath => RawDatabase.DatabaseFilePath;

    public RawDatabase RawDatabase { get; }

    public string RootPath => _rootPath;

    public string SelfUin { get; }

    public string? SlowDatabaseFilePath => _slowRawDatabase?.DatabaseFilePath;

    public string? SearchIndexDatabaseFilePath => _indexRawDatabase?.DatabaseFilePath;

    public bool HasMessageSearchIndex => _indexConnection is not null && !_indexSearchUnavailable;

    public void Initialize()
    {
        RawDatabase.Initialize();
        _connection = new QQNTDbConnection(RawDatabase.Database, RawDatabase.DatabaseFilePath);
        _connection.Open();

        if (_slowRawDatabase is not null)
        {
            _slowRawDatabase.Initialize();
            _slowConnection = new QQNTDbConnection(_slowRawDatabase.Database, _slowRawDatabase.DatabaseFilePath);
            _slowConnection.Open();
        }

        if (_indexRawDatabase is not null)
        {
            try
            {
                _indexRawDatabase.Initialize();
                RegisterIndexCompressionFunction(_indexRawDatabase.Database, "qqcompress");
                RegisterIndexCompressionFunction(_indexRawDatabase.Database, "qquncompress");
                _indexConnection = new QQNTDbConnection(_indexRawDatabase.Database, _indexRawDatabase.DatabaseFilePath);
                _indexConnection.Open();
            }
            catch
            {
                _indexSearchUnavailable = true;
                _indexConnection?.Dispose();
                _indexConnection = null;
                _indexRawDatabase.Dispose();
            }
        }
    }

    public IReadOnlyList<AndroidMobileQQConversation> GetConversations()
    {
        EnsureConnection();
        EnsureContactCaches();

        var allTables = GetAllMessageTables();
        var conversations = LoadRecentConversations(allTables);
        AddMissingContactConversations(conversations, allTables);

        var result = conversations
            .GroupBy(static conversation => (conversation.ConversationType, conversation.PeerUin))
            .Select(static group => group
                .OrderByDescending(static conversation => conversation.LatestMessageTime)
                .First())
            .OrderByDescending(static conversation => conversation.LatestMessageTime)
            .ThenBy(static conversation => conversation.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        _conversationByTableName = result
            .GroupBy(static conversation => conversation.TableName, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        return result;
    }

    public IReadOnlyList<AndroidMobileQQMessageRecord> LoadLatestMessages(
        string tableName,
        int pageSize,
        MessageQueryFilter? filter = null)
    {
        EnsureConversationTableName(tableName);
        var whereClause = CreateFilterClause(filter);
        return QueryMergedMessages(
            tableName,
            $"""
            SELECT _id, hex(CAST(frienduin AS BLOB)), hex(CAST(senderuin AS BLOB)), time, msgtype, msgData, msgseq, shmsgseq, issend
            FROM {QuoteIdent(tableName)}
            {whereClause.Sql}
            ORDER BY time DESC, _id DESC
            LIMIT $limit
            """,
            command =>
            {
                whereClause.Bind(command);
                AddParameter(command, "$limit", pageSize);
            },
            descending: true,
            pageSize)
            .OrderBy(static message => message.MessageTime)
            .ThenBy(static message => message.RowId)
            .ToArray();
    }

    public IReadOnlyList<AndroidMobileQQMessageRecord> LoadEarliestMessages(
        string tableName,
        int pageSize,
        MessageQueryFilter? filter = null)
    {
        EnsureConversationTableName(tableName);
        var whereClause = CreateFilterClause(filter);
        return QueryMergedMessages(
            tableName,
            $"""
            SELECT _id, hex(CAST(frienduin AS BLOB)), hex(CAST(senderuin AS BLOB)), time, msgtype, msgData, msgseq, shmsgseq, issend
            FROM {QuoteIdent(tableName)}
            {whereClause.Sql}
            ORDER BY time ASC, _id ASC
            LIMIT $limit
            """,
            command =>
            {
                whereClause.Bind(command);
                AddParameter(command, "$limit", pageSize);
            },
            descending: false,
            pageSize);
    }

    public IReadOnlyList<AndroidMobileQQMessageRecord> LoadOlderMessages(
        string tableName,
        long messageTime,
        long rowId,
        int pageSize,
        MessageQueryFilter? filter = null)
    {
        EnsureConversationTableName(tableName);
        var whereClause = CreateFilterClause(filter);
        var predicate = string.IsNullOrWhiteSpace(whereClause.Predicate)
            ? "time < $time OR (time = $time AND _id < $rowid)"
            : $"{whereClause.Predicate} AND (time < $time OR (time = $time AND _id < $rowid))";
        return QueryMergedMessages(
            tableName,
            $"""
            SELECT _id, hex(CAST(frienduin AS BLOB)), hex(CAST(senderuin AS BLOB)), time, msgtype, msgData, msgseq, shmsgseq, issend
            FROM {QuoteIdent(tableName)}
            WHERE {predicate}
            ORDER BY time DESC, _id DESC
            LIMIT $limit
            """,
            command =>
            {
                whereClause.Bind(command);
                AddParameter(command, "$time", messageTime);
                AddParameter(command, "$rowid", rowId);
                AddParameter(command, "$limit", pageSize);
            },
            descending: true,
            pageSize)
            .OrderBy(static message => message.MessageTime)
            .ThenBy(static message => message.RowId)
            .ToArray();
    }

    public IReadOnlyList<AndroidMobileQQMessageRecord> LoadNewerMessages(
        string tableName,
        long messageTime,
        long rowId,
        int pageSize,
        MessageQueryFilter? filter = null)
    {
        EnsureConversationTableName(tableName);
        var whereClause = CreateFilterClause(filter);
        var predicate = string.IsNullOrWhiteSpace(whereClause.Predicate)
            ? "time > $time OR (time = $time AND _id > $rowid)"
            : $"{whereClause.Predicate} AND (time > $time OR (time = $time AND _id > $rowid))";
        return QueryMergedMessages(
            tableName,
            $"""
            SELECT _id, hex(CAST(frienduin AS BLOB)), hex(CAST(senderuin AS BLOB)), time, msgtype, msgData, msgseq, shmsgseq, issend
            FROM {QuoteIdent(tableName)}
            WHERE {predicate}
            ORDER BY time ASC, _id ASC
            LIMIT $limit
            """,
            command =>
            {
                whereClause.Bind(command);
                AddParameter(command, "$time", messageTime);
                AddParameter(command, "$rowid", rowId);
                AddParameter(command, "$limit", pageSize);
            },
            descending: false,
            pageSize);
    }

    public AndroidMobileQQMessageRecord? LoadMessage(string tableName, long messageTime, long rowId)
    {
        EnsureConversationTableName(tableName);
        return QueryMergedMessages(
            tableName,
            $"""
            SELECT _id, hex(CAST(frienduin AS BLOB)), hex(CAST(senderuin AS BLOB)), time, msgtype, msgData, msgseq, shmsgseq, issend
            FROM {QuoteIdent(tableName)}
            WHERE time = $time AND _id = $rowid
            LIMIT 1
            """,
            command =>
            {
                AddParameter(command, "$time", messageTime);
                AddParameter(command, "$rowid", rowId);
            },
            descending: false,
            pageSize: 1)
            .FirstOrDefault();
    }

    public AndroidMobileQQMessageSearchPage SearchMessages(
        string tableName,
        string query,
        int pageSize,
        AndroidMobileQQMessageSearchCursor? cursor = null)
    {
        EnsureConversationTableName(tableName);
        if (string.IsNullOrWhiteSpace(query) || pageSize <= 0)
            return new AndroidMobileQQMessageSearchPage([], null, false);

        if (TrySearchMessagesWithIndex(tableName, query, pageSize, cursor, out var indexedPage))
            return indexedPage;

        var results = new List<AndroidMobileQQMessageRecord>(pageSize);
        AndroidMobileQQMessageSearchCursor? nextCursor = cursor;
        var hasMore = true;
        while (results.Count < pageSize && hasMore)
        {
            var batch = LoadSearchBatch(tableName, nextCursor, 500);
            hasMore = batch.Count == 500;
            if (batch.Count == 0)
                break;

            var last = batch[^1];
            nextCursor = new AndroidMobileQQMessageSearchCursor(last.MessageTime, last.RowId);

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

        return new AndroidMobileQQMessageSearchPage(
            results,
            hasMore ? nextCursor : null,
            hasMore);
    }

    public AndroidMobileQQMessageSearchPage SearchAllMessages(
        string query,
        int pageSize,
        AndroidMobileQQMessageSearchCursor? cursor = null)
    {
        if (string.IsNullOrWhiteSpace(query) || pageSize <= 0)
            return new AndroidMobileQQMessageSearchPage([], null, false);

        if (TrySearchMessagesWithIndex(tableName: null, query, pageSize, cursor, out var indexedPage))
            return indexedPage;

        return new AndroidMobileQQMessageSearchPage([], null, false);
    }

    public IReadOnlyList<AndroidMobileQQMessageDate> LoadMessageDates(string tableName)
    {
        EnsureConversationTableName(tableName);
        if (_messageDates.TryGetValue(tableName, out var cachedDates))
            return cachedDates;

        var counts = new Dictionary<int, int>();
        foreach (var connection in GetConnectionsForTable(tableName))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT time
                FROM {QuoteIdent(tableName)}
                ORDER BY time ASC
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var messageTime = ReadInt64(reader, 0);
                if (messageTime <= 0)
                    continue;

                var dayStart = GetLocalDayStartUnixTime(messageTime);
                counts[dayStart] = counts.GetValueOrDefault(dayStart) + 1;
            }
        }

        var rows = counts
            .OrderBy(static item => item.Key)
            .Select(static item => new AndroidMobileQQMessageDate(item.Key, item.Value))
            .ToArray();
        _messageDates[tableName] = rows;
        return rows;
    }

    public IReadOnlyList<AndroidMobileQQSender> LoadSenders(string groupUin)
    {
        EnsureContactCaches();
        var members = LoadGroupMembers(groupUin);
        if (members.Count == 0)
            return [];

        return members
            .Select(static item => new AndroidMobileQQSender(
                TryParseUin(item.Key),
                item.Key,
                string.IsNullOrWhiteSpace(item.Value) ? item.Key : item.Value))
            .OrderBy(static item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static item => item.UinText, StringComparer.Ordinal)
            .ToArray();
    }

    public string? ResolveSenderName(AndroidMobileQQConversationType conversationType, string peerUin, string senderUin)
    {
        EnsureContactCaches();
        if (string.IsNullOrWhiteSpace(senderUin))
            return null;

        if (senderUin == SelfUin)
            return "我";

        if (conversationType == AndroidMobileQQConversationType.Group &&
            LoadGroupMembers(peerUin).TryGetValue(senderUin, out var memberName) &&
            !string.IsNullOrWhiteSpace(memberName))
        {
            return memberName;
        }

        return _friendNames!.GetValueOrDefault(senderUin);
    }

    public static string CreateMessageTableName(AndroidMobileQQConversationType conversationType, string uin)
    {
        var prefix = conversationType == AndroidMobileQQConversationType.Group
            ? "mr_troop_"
            : "mr_friend_";
        return prefix + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(uin))).ToUpperInvariant() + "_New";
    }

    private IReadOnlyList<AndroidMobileQQMessageRecord> QueryMergedMessages(
        string tableName,
        string sql,
        Action<IDbCommand> bind,
        bool descending,
        int pageSize)
    {
        var messages = new List<AndroidMobileQQMessageRecord>();
        foreach (var connection in GetConnectionsForTable(tableName))
        {
            messages.AddRange(QueryMessages(connection, tableName, sql, bind));
        }

        var ordered = descending
            ? messages.OrderByDescending(static message => message.MessageTime).ThenByDescending(static message => message.RowId)
            : messages.OrderBy(static message => message.MessageTime).ThenBy(static message => message.RowId);

        return ordered
            .DistinctBy(static message => (message.MessageTime, message.SenderUin, message.MsgSeq, message.ShMsgSeq, message.PreviewText))
            .Take(pageSize)
            .ToArray();
    }

    private IReadOnlyList<AndroidMobileQQMessageRecord> LoadSearchBatch(
        string tableName,
        AndroidMobileQQMessageSearchCursor? cursor,
        int batchSize)
    {
        var cursorPredicate = cursor is null
            ? string.Empty
            : "WHERE time < $time OR (time = $time AND _id < $rowid)";
        return QueryMergedMessages(
            tableName,
            $"""
            SELECT _id, hex(CAST(frienduin AS BLOB)), hex(CAST(senderuin AS BLOB)), time, msgtype, msgData, msgseq, shmsgseq, issend
            FROM {QuoteIdent(tableName)}
            {cursorPredicate}
            ORDER BY time DESC, _id DESC
            LIMIT $limit
            """,
            command =>
            {
                if (cursor is not null)
                {
                    AddParameter(command, "$time", cursor.MessageTime);
                    AddParameter(command, "$rowid", cursor.RowId);
                }

                AddParameter(command, "$limit", batchSize);
            },
            descending: true,
            batchSize);
    }

    private bool TrySearchMessagesWithIndex(
        string? tableName,
        string query,
        int pageSize,
        AndroidMobileQQMessageSearchCursor? cursor,
        out AndroidMobileQQMessageSearchPage page)
    {
        page = new AndroidMobileQQMessageSearchPage([], null, false);
        if (_indexConnection is null || _indexSearchUnavailable)
            return false;

        var matchQuery = CreateIndexMatchQuery(query);
        if (string.IsNullOrWhiteSpace(matchQuery))
            return false;

        AndroidMobileQQConversation? targetConversation = null;
        if (!string.IsNullOrWhiteSpace(tableName) &&
            !TryResolveConversation(tableName, out targetConversation))
        {
            return false;
        }

        var maxIndexRowId = cursor?.IndexRowId ?? long.MaxValue;
        var results = new List<AndroidMobileQQMessageRecord>(pageSize);
        AndroidMobileQQMessageSearchCursor? nextCursor = null;
        var exhausted = false;

        try
        {
            // A keyword can be common globally while rare in one conversation. Keep
            // advancing the FTS cursor inside this call so UI search pages still
            // make visible progress without scanning message tables.
            for (var round = 0; round < 64 && results.Count < pageSize; round++)
            {
                var candidates = LoadIndexSearchCandidates(matchQuery, maxIndexRowId, 1024);
                if (candidates.Count == 0)
                {
                    exhausted = true;
                    break;
                }

                maxIndexRowId = candidates[^1].IndexRowId;
                foreach (var candidate in candidates)
                {
                    if (targetConversation is not null &&
                        !string.Equals(candidate.ConversationKey, CreateIndexConversationKey(targetConversation), StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (targetConversation is null &&
                        !TryResolveIndexedConversation(candidate, out targetConversation))
                    {
                        targetConversation = null;
                        continue;
                    }

                    var message = LoadIndexedMessage(targetConversation.TableName, candidate);
                    if (message is null || !PreviewMatchesQuery(message.PreviewText, query))
                    {
                        if (targetConversation is not null && tableName is null)
                            targetConversation = null;
                        continue;
                    }

                    results.Add(message);
                    if (targetConversation is not null && tableName is null)
                        targetConversation = null;

                    if (results.Count >= pageSize)
                        break;
                }

                nextCursor = new AndroidMobileQQMessageSearchCursor(0, 0, maxIndexRowId);
            }
        }
        catch
        {
            _indexSearchUnavailable = true;
            return false;
        }

        var hasMore = !exhausted && nextCursor is not null;
        page = new AndroidMobileQQMessageSearchPage(
            results
                .DistinctBy(static message => (message.TableName, message.RowId, message.MessageTime))
                .OrderByDescending(static message => message.MessageTime)
                .ThenByDescending(static message => message.RowId)
                .Take(pageSize)
                .ToArray(),
            hasMore ? nextCursor : null,
            hasMore);
        return true;
    }

    private IReadOnlyList<AndroidMobileQQIndexSearchCandidate> LoadIndexSearchCandidates(
        string matchQuery,
        long beforeIndexRowId,
        int limit)
    {
        using var command = _indexConnection!.CreateCommand();
        command.CommandText = """
            SELECT rowid, content, exts, oid, ext1, ext2, ext3
            FROM IndexContent
            WHERE IndexContent MATCH $query
              AND rowid < $beforeRowId
            ORDER BY rowid DESC
            LIMIT $limit
            """;
        AddParameter(command, "$query", matchQuery);
        AddParameter(command, "$beforeRowId", beforeIndexRowId);
        AddParameter(command, "$limit", limit);

        var candidates = new List<AndroidMobileQQIndexSearchCandidate>(limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var indexRowId = ReadInt64(reader, 0);
            var content = DecodeBase64Text(ReadIndexText(reader, 1));
            var location = DecodeIndexLocation(ReadIndexText(reader, 2));
            var rowId = TryParseInt64(DecodeBase64Text(ReadIndexText(reader, 3)));
            var conversationKey = DecodeBase64Text(ReadIndexText(reader, 4));
            var senderUin = DecodeBase64Text(ReadIndexText(reader, 5));
            var msgType = TryParseInt64(DecodeBase64Text(ReadIndexText(reader, 6)));

            if (indexRowId <= 0 ||
                rowId <= 0 ||
                location is null ||
                string.IsNullOrWhiteSpace(conversationKey))
            {
                continue;
            }

            candidates.Add(new AndroidMobileQQIndexSearchCandidate(
                indexRowId,
                rowId,
                location.MessageTime,
                location.ShMsgSeq,
                conversationKey,
                senderUin,
                (int)msgType,
                content));
        }

        return candidates;
    }

    private AndroidMobileQQMessageRecord? LoadIndexedMessage(
        string tableName,
        AndroidMobileQQIndexSearchCandidate candidate)
    {
        var message = QueryMergedMessages(
            tableName,
            $"""
            SELECT _id, hex(CAST(frienduin AS BLOB)), hex(CAST(senderuin AS BLOB)), time, msgtype, msgData, msgseq, shmsgseq, issend
            FROM {QuoteIdent(tableName)}
            WHERE _id = $rowid
              AND time = $time
              AND shmsgseq = $shmsgseq
            LIMIT 1
            """,
            command =>
            {
                AddParameter(command, "$rowid", candidate.RowId);
                AddParameter(command, "$time", candidate.MessageTime);
                AddParameter(command, "$shmsgseq", candidate.ShMsgSeq);
            },
            descending: false,
            pageSize: 1)
            .FirstOrDefault();

        if (message is not null)
            return message;

        // Some migrated rows have a trustworthy FTS location but a changed local
        // rowid. Fall back to the indexed shmsgseq path, which the old Android QQ
        // tables normally index.
        return QueryMergedMessages(
            tableName,
            $"""
            SELECT _id, hex(CAST(frienduin AS BLOB)), hex(CAST(senderuin AS BLOB)), time, msgtype, msgData, msgseq, shmsgseq, issend
            FROM {QuoteIdent(tableName)}
            WHERE shmsgseq = $shmsgseq
              AND time = $time
            ORDER BY _id DESC
            LIMIT 4
            """,
            command =>
            {
                AddParameter(command, "$shmsgseq", candidate.ShMsgSeq);
                AddParameter(command, "$time", candidate.MessageTime);
            },
            descending: false,
            pageSize: 4)
            .FirstOrDefault(message =>
                string.IsNullOrWhiteSpace(candidate.Content) ||
                message.PreviewText.Contains(candidate.Content, StringComparison.CurrentCultureIgnoreCase) ||
                candidate.Content.Contains(message.PreviewText, StringComparison.CurrentCultureIgnoreCase));
    }

    private bool TryResolveConversation(string tableName, out AndroidMobileQQConversation conversation)
    {
        if (_conversationByTableName is null)
            _ = GetConversations();

        if (_conversationByTableName is not null &&
            _conversationByTableName.TryGetValue(tableName, out conversation!))
        {
            return true;
        }

        conversation = null!;
        return false;
    }

    private bool TryResolveIndexedConversation(
        AndroidMobileQQIndexSearchCandidate candidate,
        out AndroidMobileQQConversation conversation)
    {
        conversation = null!;
        if (!TryParseIndexConversationKey(candidate.ConversationKey, out var conversationType, out var peerUin))
            return false;

        var tableName = CreateMessageTableName(conversationType, peerUin);
        if (!GetAllMessageTables().Contains(tableName))
            return false;

        conversation = new AndroidMobileQQConversation(
            conversationType,
            peerUin,
            tableName,
            ResolveConversationDisplayName(conversationType, peerUin),
            candidate.MessageTime,
            candidate.Content,
            candidate.SenderUin,
            ResolveSenderName(conversationType, peerUin, candidate.SenderUin));
        return true;
    }

    private static string CreateIndexConversationKey(AndroidMobileQQConversation conversation)
    {
        return conversation.PeerUin + (conversation.ConversationType == AndroidMobileQQConversationType.Group ? "ZzZ1" : "ZzZ0");
    }

    private static bool TryParseIndexConversationKey(
        string value,
        out AndroidMobileQQConversationType conversationType,
        out string peerUin)
    {
        conversationType = AndroidMobileQQConversationType.Private;
        peerUin = string.Empty;
        if (value.EndsWith("ZzZ1", StringComparison.Ordinal))
        {
            conversationType = AndroidMobileQQConversationType.Group;
            peerUin = value[..^4];
            return !string.IsNullOrWhiteSpace(peerUin);
        }

        if (value.EndsWith("ZzZ0", StringComparison.Ordinal))
        {
            conversationType = AndroidMobileQQConversationType.Private;
            peerUin = value[..^4];
            return !string.IsNullOrWhiteSpace(peerUin);
        }

        return false;
    }

    private static string CreateIndexMatchQuery(string query)
    {
        var tokens = CreateSearchTokens(query)
            .Select(static token => "\"" + token.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();

        return string.Join(' ', tokens);
    }

    private static IEnumerable<string> CreateSearchTokens(string query)
    {
        foreach (Match match in Regex.Matches(query, @"[\p{L}\p{N}_@#.+:/\\-]+", RegexOptions.CultureInvariant))
        {
            var token = match.Value.Trim();
            if (token.Length == 0)
                continue;

            if (ContainsCjk(token) && token.Length > 2)
            {
                yield return token[..2];
                yield return token[^2..];
            }
            else
            {
                yield return token;
            }
        }
    }

    private static bool ContainsCjk(string value)
    {
        foreach (var item in value)
        {
            if (item is >= '\u3400' and <= '\u9FFF')
                return true;
        }

        return false;
    }

    private static string DecodeBase64Text(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch
        {
            return value;
        }
    }

    private static string? ReadIndexText(IDataRecord reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        return reader.GetValue(ordinal) switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string text => text,
            var value => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }

    private static bool PreviewMatchesQuery(string previewText, string query)
    {
        if (previewText.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            return true;

        var tokens = CreateSearchTokens(query)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return tokens.Length > 0 &&
            tokens.All(token => previewText.Contains(token, StringComparison.CurrentCultureIgnoreCase));
    }

    private static AndroidMobileQQIndexLocation? DecodeIndexLocation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length < 16)
                return null;

            var messageTime = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(0, 8));
            var shMsgSeq = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(8, 8));
            return messageTime > 0
                ? new AndroidMobileQQIndexLocation(messageTime, shMsgSeq)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static long TryParseInt64(string? value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;
    }

    private static void RegisterIndexCompressionFunction(sqlite3 database, string name)
    {
        raw.sqlite3_create_function(
            database,
            name,
            1,
            raw.SQLITE_UTF8,
            null!,
            static (context, _, arguments) =>
            {
                if (arguments.Length == 0 || raw.sqlite3_value_type(arguments[0]) == raw.SQLITE_NULL)
                {
                    raw.sqlite3_result_null(context);
                    return;
                }

                raw.sqlite3_result_blob(context, raw.sqlite3_value_blob(arguments[0]));
            });
    }

    private IReadOnlyList<AndroidMobileQQMessageRecord> QueryMessages(
        QQNTDbConnection connection,
        string tableName,
        string sql,
        Action<IDbCommand> bind)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);

        var conversationType = tableName.StartsWith("mr_troop_", StringComparison.Ordinal)
            ? AndroidMobileQQConversationType.Group
            : AndroidMobileQQConversationType.Private;
        var messages = new List<AndroidMobileQQMessageRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var msgType = (int)ReadInt64(reader, 4);
            var content = DecodeContent(ReadBlob(reader, 5), msgType);
            var peerUin = DecodeString(ReadHexBlob(reader, 1));
            var senderUin = DecodeString(ReadHexBlob(reader, 2));
            var senderName = ResolveSenderName(conversationType, peerUin, senderUin);
            var previewText = AndroidMobileQQMessageContent.CreatePreviewText(content);

            messages.Add(new AndroidMobileQQMessageRecord(
                tableName,
                conversationType,
                peerUin,
                reader.GetInt64(0),
                ReadInt64(reader, 3),
                msgType,
                (int)ReadInt64(reader, 6),
                ReadInt64(reader, 7),
                (int)ReadInt64(reader, 8),
                senderUin,
                senderName,
                content,
                previewText));
        }

        return messages;
    }

    private IReadOnlyList<QQNTDbConnection> GetConnectionsForTable(string tableName)
    {
        EnsureConnection();
        var connections = new List<QQNTDbConnection>();
        if (_slowConnection is not null && GetSlowTables().Contains(tableName))
            connections.Add(_slowConnection);
        if (GetMainTables().Contains(tableName))
            connections.Add(_connection!);
        return connections;
    }

    private void EnsureContactCaches()
    {
        _mainTables ??= LoadMessageTables(_connection ?? throw new InvalidOperationException("Android QQ database is not initialized."));
        if (_slowConnection is not null)
            _slowTables ??= LoadMessageTables(_slowConnection);

        _friendNames ??= LoadFriendNames();
        _groupNames ??= LoadGroupNames();
    }

    private IReadOnlySet<string> GetMainTables()
    {
        EnsureConnection();
        return _mainTables ??= LoadMessageTables(_connection!);
    }

    private IReadOnlySet<string> GetSlowTables()
    {
        if (_slowConnection is null)
            return new HashSet<string>(StringComparer.Ordinal);

        return _slowTables ??= LoadMessageTables(_slowConnection);
    }

    private IReadOnlySet<string> GetAllMessageTables()
    {
        return GetMainTables()
            .Concat(GetSlowTables())
            .ToHashSet(StringComparer.Ordinal);
    }

    private List<AndroidMobileQQConversation> LoadRecentConversations(IReadOnlySet<string> allTables)
    {
        var conversations = new List<AndroidMobileQQConversation>();
        using var command = _connection!.CreateCommand();
        command.CommandText = """
            SELECT type,
                   hex(CAST(uin AS BLOB)),
                   hex(CAST(troopUin AS BLOB)),
                   msgType,
                   msgData,
                   lastmsgtime
            FROM recent
            WHERE uin IS NOT NULL
              AND lastmsgtime IS NOT NULL
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var recentType = (int)ReadInt64(reader, 0);
            var uin = DecodeString(ReadHexBlob(reader, 1));
            var troopUin = DecodeString(ReadHexBlob(reader, 2));
            var peerUin = FirstNonEmpty(troopUin, uin);
            if (string.IsNullOrWhiteSpace(peerUin))
                continue;

            var conversationType = ResolveRecentConversationType(recentType, peerUin, allTables);
            if (conversationType is null)
                continue;

            var tableName = CreateMessageTableName(conversationType.Value, peerUin);
            if (!allTables.Contains(tableName))
                continue;

            var msgType = (int)ReadInt64(reader, 3);
            var previewText = AndroidMobileQQMessageContent.CreatePreviewText(DecodeContent(ReadBlob(reader, 4), msgType));
            conversations.Add(new AndroidMobileQQConversation(
                conversationType.Value,
                peerUin,
                tableName,
                ResolveConversationDisplayName(conversationType.Value, peerUin),
                ReadInt64(reader, 5),
                previewText,
                string.Empty,
                null));
        }

        return conversations;
    }

    private void AddMissingContactConversations(
        List<AndroidMobileQQConversation> conversations,
        IReadOnlySet<string> allTables)
    {
        var existing = conversations
            .Select(static conversation => (conversation.ConversationType, conversation.PeerUin))
            .ToHashSet();

        foreach (var (uin, displayName) in _friendNames!)
        {
            var tableName = CreateMessageTableName(AndroidMobileQQConversationType.Private, uin);
            var key = (AndroidMobileQQConversationType.Private, uin);
            if (existing.Contains(key) || !allTables.Contains(tableName))
                continue;

            conversations.Add(new AndroidMobileQQConversation(
                AndroidMobileQQConversationType.Private,
                uin,
                tableName,
                FirstNonEmpty(displayName, uin),
                0,
                string.Empty,
                string.Empty,
                null));
        }

        foreach (var (uin, displayName) in _groupNames!)
        {
            var tableName = CreateMessageTableName(AndroidMobileQQConversationType.Group, uin);
            var key = (AndroidMobileQQConversationType.Group, uin);
            if (existing.Contains(key) || !allTables.Contains(tableName))
                continue;

            conversations.Add(new AndroidMobileQQConversation(
                AndroidMobileQQConversationType.Group,
                uin,
                tableName,
                FirstNonEmpty(displayName, uin),
                0,
                string.Empty,
                string.Empty,
                null));
        }
    }

    private AndroidMobileQQConversationType? ResolveRecentConversationType(
        int recentType,
        string peerUin,
        IReadOnlySet<string> allTables)
    {
        if (recentType == 1)
            return AndroidMobileQQConversationType.Group;
        if (recentType == 0)
            return AndroidMobileQQConversationType.Private;

        var groupTableName = CreateMessageTableName(AndroidMobileQQConversationType.Group, peerUin);
        if (allTables.Contains(groupTableName))
            return AndroidMobileQQConversationType.Group;

        var privateTableName = CreateMessageTableName(AndroidMobileQQConversationType.Private, peerUin);
        return allTables.Contains(privateTableName)
            ? AndroidMobileQQConversationType.Private
            : null;
    }

    private string ResolveConversationDisplayName(AndroidMobileQQConversationType conversationType, string peerUin)
    {
        var names = conversationType == AndroidMobileQQConversationType.Group
            ? _groupNames!
            : _friendNames!;
        return FirstNonEmpty(names.GetValueOrDefault(peerUin), peerUin);
    }

    private static IReadOnlySet<string> LoadMessageTables(QQNTDbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND (name LIKE 'mr_friend_%_New' OR name LIKE 'mr_troop_%_New')
            """;

        var tables = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var tableName = reader.GetString(0);
            if (MessageTableNameRegex.IsMatch(tableName))
                tables.Add(tableName);
        }

        return tables;
    }

    private Dictionary<string, string> LoadFriendNames()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in QueryTextRows(_connection!, "SELECT uin, remark, name FROM Friends"))
        {
            var uin = DecodeText(row.ElementAtOrDefault(0));
            if (string.IsNullOrWhiteSpace(uin))
                continue;

            result[uin] = FirstNonEmpty(
                DecodeText(row.ElementAtOrDefault(1)),
                DecodeText(row.ElementAtOrDefault(2)),
                uin);
        }

        return result;
    }

    private Dictionary<string, string> LoadGroupNames()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in QueryTextRows(_connection!, "SELECT troopuin, troopname, newTroopName, oldTroopName, troopRemark FROM TroopInfoV2"))
        {
            var uin = DecodeText(row.ElementAtOrDefault(0));
            if (string.IsNullOrWhiteSpace(uin))
                continue;

            result[uin] = FirstNonEmpty(
                DecodeText(row.ElementAtOrDefault(4)),
                DecodeText(row.ElementAtOrDefault(2)),
                DecodeText(row.ElementAtOrDefault(1)),
                DecodeText(row.ElementAtOrDefault(3)),
                uin);
        }

        foreach (var row in QueryPlainRows(_connection!, "SELECT group_id, group_name FROM Groups"))
        {
            var uin = row.ElementAtOrDefault(0);
            if (string.IsNullOrWhiteSpace(uin))
                continue;

            result[uin] = FirstNonEmpty(row.ElementAtOrDefault(1), result.GetValueOrDefault(uin), uin);
        }

        return result;
    }

    private Dictionary<string, string> LoadGroupMembers(string groupUin)
    {
        if (string.IsNullOrWhiteSpace(groupUin))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        if (_groupMemberNames.TryGetValue(groupUin, out var cachedMembers))
            return cachedMembers;

        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        _groupMemberNames[groupUin] = members;

        using var command = _connection!.CreateCommand();
        command.CommandText = """
            SELECT memberuin,
                   friendnick,
                   troopnick
            FROM TroopMemberInfo
            WHERE troopuin = $troopuin
            """;
        AddParameter(command, "$troopuin", EncodeText(groupUin));

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var memberUin = DecodeText(ReadText(reader, 0));
            if (string.IsNullOrWhiteSpace(memberUin))
                continue;

            members[memberUin] = FirstNonEmpty(
                DecodeText(ReadText(reader, 2)),
                DecodeText(ReadText(reader, 1)),
                memberUin);
        }

        return members;
    }

    private static IEnumerable<string[]> QueryPlainRows(QQNTDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = reader.IsDBNull(i)
                    ? string.Empty
                    : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty;
            }

            yield return values;
        }
    }

    private static IEnumerable<string?[]> QueryTextRows(QQNTDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var values = new string?[reader.FieldCount];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = ReadText(reader, i);
            }

            yield return values;
        }
    }

    private AndroidMobileQQMessageContent DecodeContent(byte[]? data, int msgType)
    {
        if (data is null || data.Length == 0)
            return AndroidMobileQQMessageContent.Empty(msgType);

        var decoded = Xor(data);
        return msgType switch
        {
            -1000 or -1049 or -1051 => AndroidMobileQQMessageContent.Text(msgType, DecodeUtf8(decoded)),
            -2000 => AndroidMobileQQMessageContent.Image(msgType, "[图片]", TryDecodeImageMd5(decoded)),
            -1035 => AndroidMobileQQMessageContent.Unsupported(msgType, "[混合消息]"),
            -5008 => AndroidMobileQQMessageContent.Unsupported(msgType, "[分享卡片]"),
            -5012 or -5018 => AndroidMobileQQMessageContent.Unsupported(msgType, "[戳一戳]"),
            _ => AndroidMobileQQMessageContent.Unsupported(msgType, $"[旧版AndroidQQ消息:{msgType}]"),
        };
    }

    private static string? TryDecodeImageMd5(byte[] data)
    {
        try
        {
            var input = new CodedInputStream(data);
            while (!input.IsAtEnd)
            {
                var tag = input.ReadTag();
                if (tag == 0)
                    break;

                var fieldNumber = WireFormat.GetTagFieldNumber(tag);
                var wireType = WireFormat.GetTagWireType(tag);
                if (fieldNumber == 6 && wireType == WireFormat.WireType.LengthDelimited)
                {
                    var md5 = input.ReadString();
                    return IsHexMd5(md5) ? md5 : null;
                }

                input.SkipLastField();
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool IsHexMd5(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Length == 32 &&
               value.All(char.IsAsciiHexDigit);
    }

    private string DecodeString(byte[]? data)
    {
        if (data is null || data.Length == 0)
            return string.Empty;

        return DecodeUtf8(Xor(data));
    }

    private string DecodeText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return XorText(value);
    }

    private string EncodeText(string value)
    {
        return XorText(value);
    }

    private string XorText(string value)
    {
        var chars = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            chars[i] = (char)(value[i] ^ _fieldKey[i % _fieldKey.Length]);
        }

        return new string(chars);
    }

    private byte[] Xor(byte[] data)
    {
        var result = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            result[i] = (byte)(data[i] ^ _fieldKey[i % _fieldKey.Length]);
        }

        return result;
    }

    private static string DecodeUtf8(byte[] data)
    {
        try
        {
            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            return string.Empty;
        }
    }

    private AndroidMobileQQFilterClause CreateFilterClause(MessageQueryFilter? filter)
    {
        if (filter is null || filter.IsEmpty)
            return new AndroidMobileQQFilterClause(string.Empty, string.Empty, _ => { });

        var predicates = new List<string>();
        if (filter.StartTime is not null)
            predicates.Add("time >= $filterStartTime");
        if (filter.EndTimeExclusive is not null)
            predicates.Add("time < $filterEndTime");

        var selectedDayStartTimes = filter.SelectedDayStartTimes
            .Where(static dayStartTime => dayStartTime > 0)
            .Distinct()
            .OrderBy(static dayStartTime => dayStartTime)
            .ToArray();
        if (selectedDayStartTimes.Length > 0)
        {
            predicates.Add("(" + string.Join(
                " OR ",
                selectedDayStartTimes.Select((_, index) => $"(time >= $dayStart{index} AND time < $dayEnd{index})")) + ")");
        }

        var senderIds = filter.SenderIds
            .Where(static senderId => senderId != 0)
            .Distinct()
            .ToArray();
        if (senderIds.Length > 0)
        {
            predicates.Add($"senderuin IN ({string.Join(", ", senderIds.Select((_, index) => "$sender" + index))})");
        }

        var predicate = string.Join(" AND ", predicates);
        var sql = string.IsNullOrWhiteSpace(predicate) ? string.Empty : $"WHERE {predicate}";
        return new AndroidMobileQQFilterClause(
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
                    var senderText = senderIds[i].ToString(CultureInfo.InvariantCulture);
                    AddParameter(command, "$sender" + i, EncodeText(senderText));
                }
            });
    }

    private void EnsureConnection()
    {
        if (_connection is null)
            throw new InvalidOperationException("Android QQ database is not initialized.");
    }

    private static byte[]? ReadBlob(IDataRecord reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        return reader.GetValue(ordinal) is byte[] bytes ? bytes : null;
    }

    private static string? ReadText(IDataRecord reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        return Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static byte[]? ReadHexBlob(IDataRecord reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        var hex = Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(hex))
            return null;

        try
        {
            return Convert.FromHexString(hex);
        }
        catch
        {
            return null;
        }
    }

    private static long ReadInt64(IDataRecord reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return 0;

        var value = reader.GetValue(ordinal);
        return value switch
        {
            long number => number,
            int number => number,
            short number => number,
            byte number => number,
            string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        };
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
        if (!MessageTableNameRegex.IsMatch(tableName))
            throw new ArgumentException("Invalid Android QQ conversation table name.", nameof(tableName));
    }

    private static uint TryParseUin(string? value)
    {
        return uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var uin)
            ? uin
            : 0;
    }

    private static int GetLocalDayStartUnixTime(long unixTime)
    {
        var localDate = DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime.Date;
        return (int)new DateTimeOffset(localDate).ToUnixTimeSeconds();
    }

    private static string ResolveChildDirectory(string rootPath, string primaryName, string fallbackName)
    {
        var primaryPath = Path.Combine(rootPath, primaryName);
        if (Directory.Exists(primaryPath))
            return primaryPath;

        var fallbackPath = Path.Combine(rootPath, fallbackName);
        return Directory.Exists(fallbackPath) ? fallbackPath : primaryPath;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }

    private static string QuoteIdent(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    public void Dispose()
    {
        _indexConnection?.Dispose();
        _slowConnection?.Dispose();
        _connection?.Dispose();
        _indexRawDatabase?.Dispose();
        _slowRawDatabase?.Dispose();
        RawDatabase.Dispose();
    }
}

public sealed record AndroidMobileQQConversation(
    AndroidMobileQQConversationType ConversationType,
    string PeerUin,
    string TableName,
    string DisplayName,
    long LatestMessageTime,
    string LatestMessageText,
    string LatestMessageSenderUin,
    string? LatestMessageSenderName);

public sealed record AndroidMobileQQMessageRecord(
    string TableName,
    AndroidMobileQQConversationType ConversationType,
    string PeerUin,
    long RowId,
    long MessageTime,
    int MsgType,
    int MsgSeq,
    long ShMsgSeq,
    int IsSend,
    string SenderUin,
    string? SenderName,
    AndroidMobileQQMessageContent Content,
    string PreviewText);

public sealed record AndroidMobileQQMessageContent(
    int MsgType,
    IReadOnlyList<AndroidMobileQQMessagePart> Parts)
{
    public static AndroidMobileQQMessageContent Empty(int msgType) => new(msgType, []);

    public static AndroidMobileQQMessageContent Text(int msgType, string text) =>
        new(msgType, [new AndroidMobileQQMessagePart(AndroidMobileQQMessagePartType.Text, text, null, null, null, null)]);

    public static AndroidMobileQQMessageContent Image(int msgType, string displayText, string? imageMd5 = null) =>
        new(msgType, [new AndroidMobileQQMessagePart(AndroidMobileQQMessagePartType.Image, displayText, null, null, imageMd5, null)]);

    public static AndroidMobileQQMessageContent Unsupported(int msgType, string displayText) =>
        new(msgType, [new AndroidMobileQQMessagePart(AndroidMobileQQMessagePartType.Unsupported, displayText, null, null, null, null)]);

    public static string CreatePreviewText(AndroidMobileQQMessageContent content)
    {
        if (content.Parts.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var part in content.Parts)
        {
            builder.Append(part.Type switch
            {
                AndroidMobileQQMessagePartType.Text => part.Text,
                AndroidMobileQQMessagePartType.Face => string.IsNullOrWhiteSpace(part.FaceName) ? $"[QQ表情:{part.FaceId}]" : $"[{part.FaceName}]",
                AndroidMobileQQMessagePartType.Image => "[图片]",
                _ => part.Text,
            });
        }

        return builder.ToString();
    }
}

public sealed record AndroidMobileQQMessagePart(
    AndroidMobileQQMessagePartType Type,
    string Text,
    int? FaceId,
    string? FaceName,
    string? ImageMd5,
    string? ImageLocalPath);

public sealed record AndroidMobileQQMessageDate(int DayStartTime, int MessageCount);

public sealed record AndroidMobileQQSender(uint Uin, string UinText, string DisplayName);

public sealed record AndroidMobileQQMessageSearchCursor(long MessageTime, long RowId, long? IndexRowId = null);

public sealed record AndroidMobileQQMessageSearchPage(
    IReadOnlyList<AndroidMobileQQMessageRecord> Messages,
    AndroidMobileQQMessageSearchCursor? NextCursor,
    bool HasMore);

public enum AndroidMobileQQConversationType
{
    Private,
    Group,
}

public enum AndroidMobileQQMessagePartType
{
    Text,
    Face,
    Image,
    Unsupported,
}

internal sealed record AndroidMobileQQLatestMessage(
    long MessageTime,
    string PreviewText,
    string SenderUin,
    string? SenderName);

internal sealed record AndroidMobileQQFilterClause(string Sql, string Predicate, Action<IDbCommand> Bind);

internal sealed record AndroidMobileQQIndexSearchCandidate(
    long IndexRowId,
    long RowId,
    long MessageTime,
    long ShMsgSeq,
    string ConversationKey,
    string SenderUin,
    int MsgType,
    string Content);

internal sealed record AndroidMobileQQIndexLocation(long MessageTime, long ShMsgSeq);
