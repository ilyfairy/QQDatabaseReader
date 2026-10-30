using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal static class BoundedMessageTableConversationCatalogReader
{
    internal const int MaxRecoveredConversationsPerTable = 8192;

    public static IReadOnlyList<MessageConversationCatalogItem>? ReadQQNtFallbackIfNeeded(
        QQMessageReader messageDatabase)
    {
        return WithOpenConnection(
            messageDatabase.DbContext,
            connection => HasRecentContactRows(connection, messageDatabase.Schema.ResolveTable<RecentContact>())
                ? null
                : ReadFallbackConversations(connection, messageDatabase.Schema));
    }

    public static IReadOnlyList<MessageConversationCatalogItem>? ReadAndroidQQNtFallbackIfNeeded(
        QQAndroidMessageReader messageDatabase)
    {
        return WithOpenConnection(
            messageDatabase.DbContext,
            connection => HasRecentContactRows(connection, messageDatabase.Schema.ResolveTable<AndroidRecentContact>())
                ? null
                : ReadFallbackConversations(connection, messageDatabase.Schema));
    }

    private static IReadOnlyList<MessageConversationCatalogItem> ReadFallbackConversations(
        DbConnection connection,
        DatabaseSchema schema)
    {
        var result = new List<MessageConversationCatalogItem>();
        result.AddRange(ReadGroupConversations(connection));
        result.AddRange(ReadPrivateConversations(connection, schema));
        return result;
    }

    private static IEnumerable<MessageConversationCatalogItem> ReadGroupConversations(DbConnection connection)
    {
        const string tableName = "group_msg_table";
        if (!HasAnyRow(connection, tableName))
            yield break;

        var indexName = FindConversationIndex(connection, tableName, requireSequenceColumn: false);
        foreach (var storedGroupId in ReadDistinctConversationIds(connection, tableName, indexName))
        {
            var groupId = unchecked((uint)storedGroupId);
            if (groupId != 0)
                yield return MessageConversationCatalogItem.CreateGroup(groupId);
        }
    }

    private static IEnumerable<MessageConversationCatalogItem> ReadPrivateConversations(
        DbConnection connection,
        DatabaseSchema schema)
    {
        const string tableName = "c2c_msg_table";
        if (!HasAnyRow(connection, tableName))
            yield break;

        var indexName = FindConversationIndex(connection, tableName, requireSequenceColumn: true);
        var quotedTable = QuoteIdentifier(tableName);
        var quotedIndex = QuoteIdentifier(indexName);
        // 旧版 QQNT 没有 40030 (对方 QQ 号), 由 40021 换算
        var sql = $"""
            SELECT {schema.ColumnSql<PrivateMessage>("40030")}, [40021], [40050]
            FROM {quotedTable} INDEXED BY {quotedIndex}
            WHERE [40027] = $conversationId
            ORDER BY [40003] DESC
            LIMIT 1
            """;

        VerifyQueryUsesIndex(connection, sql, tableName, indexName, ("$conversationId", 1L));

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var conversationIdParameter = AddParameter(command, "$conversationId", 0L);
        foreach (var conversationId in ReadDistinctConversationIds(connection, tableName, indexName))
        {
            if (conversationId == 0)
                continue;

            conversationIdParameter.Value = conversationId;
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                continue;

            var privateUin = reader.IsDBNull(0)
                ? 0
                : unchecked((uint)reader.GetInt64(0));
            var privateUid = reader.IsDBNull(1) ? null : reader.GetString(1);
            var lastTime = reader.IsDBNull(2)
                ? 0
                : MessageConversationTime.ClampUnixTime(reader.GetInt64(2));
            yield return MessageConversationCatalogItem.CreatePrivate(
                conversationId,
                privateUin,
                privateUid,
                lastTime);
        }
    }

    private static IReadOnlyList<long> ReadDistinctConversationIds(
        DbConnection connection,
        string tableName,
        string indexName)
    {
        var quotedTable = QuoteIdentifier(tableName);
        var quotedIndex = QuoteIdentifier(indexName);
        var sql = $"""
            WITH RECURSIVE conversation_ids(value, seek_count) AS
            (
                SELECT
                (
                    SELECT [40027]
                    FROM {quotedTable} INDEXED BY {quotedIndex}
                    WHERE [40027] >= $minimumId
                    ORDER BY [40027]
                    LIMIT 1
                ),
                1
                UNION ALL
                SELECT
                (
                    SELECT [40027]
                    FROM {quotedTable} INDEXED BY {quotedIndex}
                    WHERE [40027] > conversation_ids.value
                    ORDER BY [40027]
                    LIMIT 1
                ),
                conversation_ids.seek_count + 1
                FROM conversation_ids
                WHERE conversation_ids.value IS NOT NULL AND conversation_ids.seek_count < $seekLimit
            )
            SELECT value
            FROM conversation_ids
            WHERE value IS NOT NULL AND value <> 0
            LIMIT $limit
            """;

        VerifyQueryUsesIndex(
            connection,
            sql,
            tableName,
            indexName,
            ("$minimumId", long.MinValue),
            ("$seekLimit", MaxRecoveredConversationsPerTable + 1),
            ("$limit", MaxRecoveredConversationsPerTable));

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "$minimumId", long.MinValue);
        AddParameter(command, "$seekLimit", MaxRecoveredConversationsPerTable + 1);
        AddParameter(command, "$limit", MaxRecoveredConversationsPerTable);
        using var reader = command.ExecuteReader();
        var result = new List<long>();
        while (reader.Read())
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    private static string FindConversationIndex(
        DbConnection connection,
        string tableName,
        bool requireSequenceColumn)
    {
        var candidates = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA index_list({QuoteIdentifier(tableName)})";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(1))
                    candidates.Add(reader.GetString(1));
            }
        }

        string? prefixMatch = null;
        foreach (var indexName in candidates)
        {
            var columns = ReadIndexColumns(connection, indexName);
            if (columns.Count == 0 || !string.Equals(columns[0], "40027", StringComparison.Ordinal))
                continue;

            prefixMatch ??= indexName;
            if (!requireSequenceColumn ||
                columns.Count > 1 && string.Equals(columns[1], "40003", StringComparison.Ordinal))
            {
                return indexName;
            }
        }

        if (!requireSequenceColumn && prefixMatch is not null)
            return prefixMatch;

        var expectedColumns = requireSequenceColumn ? "40027, 40003" : "40027";
        throw new InvalidOperationException(
            $"The legacy QQNT backup has no recent-contact data and {tableName} has no index beginning with {expectedColumns}; " +
            "the conversation catalog was not recovered to avoid a full-table scan.");
    }

    private static IReadOnlyList<string> ReadIndexColumns(DbConnection connection, string indexName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_info({QuoteIdentifier(indexName)})";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            if (!reader.IsDBNull(2))
                columns.Add(reader.GetString(2));
        }

        return columns;
    }

    /// <summary>recent_contact_v3_table, 旧版 QQNT 是 recent_contact_table</summary>
    private static bool HasRecentContactRows(DbConnection connection, string? tableName)
    {
        return tableName is not null && HasAnyRow(connection, tableName);
    }

    private static bool HasAnyRow(DbConnection connection, string tableName)
    {
        using (var tableCommand = connection.CreateCommand())
        {
            tableCommand.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $tableName LIMIT 1";
            AddParameter(tableCommand, "$tableName", tableName);
            if (tableCommand.ExecuteScalar() is null)
                return false;
        }

        using var rowCommand = connection.CreateCommand();
        rowCommand.CommandText = $"SELECT 1 FROM {QuoteIdentifier(tableName)} LIMIT 1";
        return rowCommand.ExecuteScalar() is not null;
    }

    private static void VerifyQueryUsesIndex(
        DbConnection connection,
        string sql,
        string tableName,
        string indexName,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var (name, value) in parameters)
        {
            AddParameter(command, name, value);
        }

        using var reader = command.ExecuteReader();
        var usesExpectedIndex = false;
        while (reader.Read())
        {
            var detail = reader.GetString(3);
            if (detail.Contains(indexName, StringComparison.Ordinal))
                usesExpectedIndex = true;
            if (detail.Contains("USE TEMP B-TREE", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The bounded legacy conversation query for {tableName} requires an unindexed sort.");
            }
        }

        if (!usesExpectedIndex)
        {
            throw new InvalidOperationException(
                $"The bounded legacy conversation query for {tableName} did not use the verified index {indexName}.");
        }
    }

    private static DbParameter AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
        return parameter;
    }

    private static T WithOpenConnection<T>(DbContext dbContext, Func<DbConnection, T> action)
    {
        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            connection.Open();

        try
        {
            return action(connection);
        }
        finally
        {
            if (openedHere)
                connection.Close();
        }
    }

    private static string QuoteIdentifier(string value)
    {
        return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }
}
