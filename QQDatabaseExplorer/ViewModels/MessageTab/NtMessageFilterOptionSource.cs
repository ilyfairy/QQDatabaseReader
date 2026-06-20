using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class NtMessageFilterOptionSource(QQDatabaseService databaseService) : IMessageFilterOptionSource
{
    public bool CanLoadDateOptions(AvaQQGroup conversation)
    {
        return conversation.ConversationType is AvaConversationType.Group or AvaConversationType.Private;
    }

    public IReadOnlyList<MessageDateFilterOption> LoadDateOptions(AvaQQGroup conversation)
    {
        try
        {
            return LoadNtMessageDateFilterOptions(conversation);
        }
        catch
        {
            return [];
        }
    }

    public bool CanLoadSenderOptions(AvaQQGroup conversation)
    {
        return conversation.ConversationType == AvaConversationType.Group;
    }

    public IReadOnlyList<MessageSenderFilterOption>? TryLoadSenderOptions(AvaQQGroup conversation)
    {
        return TryLoadGroupSenderFilterCandidates(conversation.GroupId);
    }

    private IReadOnlyList<MessageDateFilterOption> LoadNtMessageDateFilterOptions(AvaQQGroup conversation)
    {
        var connection = (conversation.ConversationType, databaseService.MessageDatabase, databaseService.AndroidMessageDatabase) switch
        {
            (AvaConversationType.Group, { } database, _) => database.DbContext.Database.GetDbConnection(),
            (AvaConversationType.Group, _, { } database) => database.DbContext.Database.GetDbConnection(),
            (AvaConversationType.Private, { } database, _) => database.DbContext.Database.GetDbConnection(),
            (AvaConversationType.Private, _, { } database) => database.DbContext.Database.GetDbConnection(),
            _ => null,
        };

        if (connection is null)
            return [];

        if (connection.State != ConnectionState.Open)
            connection.Open();

        var (tableName, indexName, conversationColumn, conversationId) = conversation.ConversationType switch
        {
            AvaConversationType.Group => ("group_msg_table", "group_msg_table_idx40027_40058", "40027", unchecked((int)conversation.GroupId)),
            AvaConversationType.Private => ("c2c_msg_table", "c2c_msg_table_idx40027_40058", "40027", conversation.PrivateConversationId),
            _ => (null, null, null, 0L),
        };

        if (string.IsNullOrWhiteSpace(tableName) ||
            string.IsNullOrWhiteSpace(indexName) ||
            string.IsNullOrWhiteSpace(conversationColumn) ||
            conversationId == 0)
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT [40058], COUNT(*)
            FROM {tableName} INDEXED BY {indexName}
            WHERE [{conversationColumn}] = $conversationId
              AND [40058] > 0
            GROUP BY [40058]
            ORDER BY [40058]
            """;
        AddDbParameter(command, "$conversationId", conversationId);

        using var reader = command.ExecuteReader();
        var result = new List<MessageDateFilterOption>();
        while (reader.Read())
        {
            result.Add(new MessageDateFilterOption(
                reader.GetInt32(0),
                reader.GetInt32(1)));
        }

        return result;
    }

    private IReadOnlyList<MessageSenderFilterOption>? TryLoadGroupSenderFilterCandidates(uint groupId)
    {
        if (groupId == 0 ||
            databaseService.GroupInfoDatabase is not { } groupInfoDatabase)
        {
            return null;
        }

        try
        {
            var candidates = groupInfoDatabase.DbContext.GroupMembers
                .Where(member => member.GroupId == groupId)
                .Where(member => member.Uin != 0)
                .Select(member => new
                {
                    member.Uin,
                    member.NtUid,
                    member.MemberName,
                    member.NickName,
                })
                .ToList()
                .Select(member => new MessageSenderFilterOption(
                    member.Uin,
                    MessageFilterOptionText.FirstNonEmpty(member.MemberName, member.NickName, member.Uin.ToString()),
                    member.NtUid))
                .DistinctBy(member => member.SenderId)
                .OrderBy(member => member.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(member => member.SenderId)
                .ToArray();
            return candidates.Length > 0 ? candidates : null;
        }
        catch
        {
            return null;
        }
    }

    private static void AddDbParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
