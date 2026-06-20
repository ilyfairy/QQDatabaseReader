using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class PCQQMessageTimelineProvider : IMessageTimelineProvider
{
    private readonly Func<PCQQMessageReader?> _getMessageDatabase;

    public PCQQMessageTimelineProvider(Func<PCQQMessageReader?> getMessageDatabase)
    {
        _getMessageDatabase = getMessageDatabase;
    }

    public bool CanLoad(AvaQQGroup conversation)
    {
        return ConversationTypeClassifier.IsPCQQ(conversation);
    }

    public MessageRecord? LoadMessage(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        MessageFilterCriteria filter)
    {
        if (_getMessageDatabase() is not { } database ||
            string.IsNullOrWhiteSpace(conversation.PCQQTableName))
        {
            return null;
        }

        var message = database.LoadMessage(conversation.PCQQTableName, messageSeq, messageId);
        return message is null
            ? null
            : MessageRecordFactory.FromPCQQ(message, conversation);
    }

    public List<MessageRecord> LoadLatestMessages(
        AvaQQGroup conversation,
        int pageSize,
        MessageFilterCriteria filter)
    {
        if (_getMessageDatabase() is not { } database ||
            string.IsNullOrWhiteSpace(conversation.PCQQTableName))
        {
            return [];
        }

        return database
            .LoadLatestMessages(conversation.PCQQTableName, pageSize, filter.ToQueryFilter())
            .Select(message => MessageRecordFactory.FromPCQQ(message, conversation))
            .ToList();
    }

    public List<MessageRecord> LoadEarliestMessages(
        AvaQQGroup conversation,
        int pageSize,
        MessageFilterCriteria filter)
    {
        if (_getMessageDatabase() is not { } database ||
            string.IsNullOrWhiteSpace(conversation.PCQQTableName))
        {
            return [];
        }

        return database
            .LoadEarliestMessages(conversation.PCQQTableName, pageSize, filter.ToQueryFilter())
            .Select(message => MessageRecordFactory.FromPCQQ(message, conversation))
            .ToList();
    }

    public List<MessageRecord> LoadOlderMessages(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        int pageSize,
        MessageFilterCriteria filter)
    {
        if (_getMessageDatabase() is not { } database ||
            string.IsNullOrWhiteSpace(conversation.PCQQTableName))
        {
            return [];
        }

        return database
            .LoadOlderMessages(conversation.PCQQTableName, messageSeq, messageId, pageSize, filter.ToQueryFilter())
            .Select(message => MessageRecordFactory.FromPCQQ(message, conversation))
            .ToList();
    }

    public List<MessageRecord> LoadNewerMessages(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        int pageSize,
        MessageFilterCriteria filter)
    {
        if (_getMessageDatabase() is not { } database ||
            string.IsNullOrWhiteSpace(conversation.PCQQTableName))
        {
            return [];
        }

        return database
            .LoadNewerMessages(conversation.PCQQTableName, messageSeq, messageId, pageSize, filter.ToQueryFilter())
            .Select(message => MessageRecordFactory.FromPCQQ(message, conversation))
            .ToList();
    }

    public List<MessageRecord> LoadNewerOrEqualMessages(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        int pageSize,
        MessageFilterCriteria filter)
    {
        return LoadNewerMessages(conversation, messageSeq, messageId - 1, pageSize, filter)
            .Where(message => message.MessageSeq > messageSeq ||
                              message.MessageSeq == messageSeq && message.MessageId >= messageId)
            .ToList();
    }
}
