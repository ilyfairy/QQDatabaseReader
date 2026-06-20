using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class NtMessageTimelineProvider : IMessageTimelineProvider
{
    private readonly Func<QQMessageReader?> _getMessageDatabase;
    private readonly Func<QQAndroidMessageReader?> _getAndroidMessageDatabase;

    public NtMessageTimelineProvider(
        Func<QQMessageReader?> getMessageDatabase,
        Func<QQAndroidMessageReader?> getAndroidMessageDatabase)
    {
        _getMessageDatabase = getMessageDatabase;
        _getAndroidMessageDatabase = getAndroidMessageDatabase;
    }

    public bool CanLoad(AvaQQGroup conversation)
    {
        return !ConversationTypeClassifier.IsPCQQ(conversation) &&
               !ConversationTypeClassifier.IsIcalingua(conversation);
    }

    public List<MessageRecord> LoadLatestMessages(
        AvaQQGroup conversation,
        int pageSize,
        MessageFilterCriteria filter)
    {
        if (CreateGroupQuery(conversation, filter) is { } groupQuery)
        {
            return NtMessageTimelinePageQuery.LoadLatestMessages(groupQuery, pageSize);
        }

        if (CreatePrivateQuery(conversation, filter) is { } privateQuery)
        {
            return NtMessageTimelinePageQuery.LoadLatestMessages(privateQuery, pageSize);
        }

        return [];
    }

    public List<MessageRecord> LoadEarliestMessages(
        AvaQQGroup conversation,
        int pageSize,
        MessageFilterCriteria filter)
    {
        if (CreateGroupQuery(conversation, filter) is { } groupQuery)
        {
            return NtMessageTimelinePageQuery.LoadEarliestMessages(groupQuery, pageSize);
        }

        if (CreatePrivateQuery(conversation, filter) is { } privateQuery)
        {
            return NtMessageTimelinePageQuery.LoadEarliestMessages(privateQuery, pageSize);
        }

        return [];
    }

    public List<MessageRecord> LoadOlderMessages(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        int pageSize,
        MessageFilterCriteria filter)
    {
        if (CreateGroupQuery(conversation, filter) is { } groupQuery)
        {
            return NtMessageTimelinePageQuery.LoadOlderMessages(groupQuery, messageSeq, messageId, pageSize);
        }

        if (CreatePrivateQuery(conversation, filter) is { } privateQuery)
        {
            return NtMessageTimelinePageQuery.LoadOlderMessages(privateQuery, messageSeq, messageId, pageSize);
        }

        return [];
    }

    public List<MessageRecord> LoadNewerMessages(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        int pageSize,
        MessageFilterCriteria filter)
    {
        if (CreateGroupQuery(conversation, filter) is { } groupQuery)
        {
            return NtMessageTimelinePageQuery.LoadNewerMessages(groupQuery, messageSeq, messageId, pageSize);
        }

        if (CreatePrivateQuery(conversation, filter) is { } privateQuery)
        {
            return NtMessageTimelinePageQuery.LoadNewerMessages(privateQuery, messageSeq, messageId, pageSize);
        }

        return [];
    }

    public List<MessageRecord> LoadNewerOrEqualMessages(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        int pageSize,
        MessageFilterCriteria filter)
    {
        if (CreateGroupQuery(conversation, filter) is { } groupQuery)
        {
            return NtMessageTimelinePageQuery.LoadNewerOrEqualMessages(groupQuery, messageSeq, messageId, pageSize);
        }

        if (CreatePrivateQuery(conversation, filter) is { } privateQuery)
        {
            return NtMessageTimelinePageQuery.LoadNewerOrEqualMessages(privateQuery, messageSeq, messageId, pageSize);
        }

        return [];
    }

    public MessageRecord? LoadMessage(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        MessageFilterCriteria filter)
    {
        if (CreateGroupQuery(conversation, filter) is { } groupQuery)
        {
            return NtMessageTimelinePageQuery.LoadMessage(groupQuery, messageId);
        }

        if (CreatePrivateQuery(conversation, filter) is { } privateQuery)
        {
            return NtMessageTimelinePageQuery.LoadMessage(privateQuery, messageId);
        }

        return null;
    }

    private IQueryable<GroupMessage>? CreateGroupQuery(AvaQQGroup conversation, MessageFilterCriteria filter)
    {
        if (conversation.ConversationType != AvaConversationType.Group)
            return null;

        var query = _getMessageDatabase()?.DbContext.GroupMessages
                    ?? _getAndroidMessageDatabase()?.DbContext.GroupMessages;
        if (query is null)
            return null;

        return NtMessageTimelineFilter.Apply(query
                .Where(message => message.GroupId == conversation.GroupId)
                .Where(message => message.MessageType != MessageType.Empty),
            conversation,
            filter);
    }

    private IQueryable<PrivateMessage>? CreatePrivateQuery(AvaQQGroup conversation, MessageFilterCriteria filter)
    {
        if (conversation.ConversationType != AvaConversationType.Private)
            return null;

        var query = _getMessageDatabase()?.DbContext.PrivateMessages
                    ?? _getAndroidMessageDatabase()?.DbContext.PrivateMessages;
        if (query is null)
            return null;

        return NtMessageTimelineFilter.Apply(query
                .Where(message => message.ConversationId == conversation.PrivateConversationId)
                .Where(message => message.MessageType != MessageType.Empty),
            filter);
    }
}
