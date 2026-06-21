using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class AndroidMobileQQMessageTimelineProvider : IMessageTimelineProvider
{
    private readonly Func<AndroidMobileQQMessageReader?> _getMessageDatabase;

    public AndroidMobileQQMessageTimelineProvider(Func<AndroidMobileQQMessageReader?> getMessageDatabase)
    {
        _getMessageDatabase = getMessageDatabase;
    }

    public bool CanLoad(AvaQQGroup conversation)
    {
        return ConversationTypeClassifier.IsAndroidMobileQQ(conversation);
    }

    public MessageRecord? LoadMessage(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        MessageFilterCriteria filter)
    {
        if (_getMessageDatabase() is not { } database ||
            string.IsNullOrWhiteSpace(conversation.AndroidMobileQQTableName))
        {
            return null;
        }

        var message = database.LoadMessage(conversation.AndroidMobileQQTableName, messageSeq, messageId);
        return message is null
            ? null
            : MessageRecordFactory.FromAndroidMobileQQ(message, conversation);
    }

    public List<MessageRecord> LoadLatestMessages(
        AvaQQGroup conversation,
        int pageSize,
        MessageFilterCriteria filter)
    {
        if (_getMessageDatabase() is not { } database ||
            string.IsNullOrWhiteSpace(conversation.AndroidMobileQQTableName))
        {
            return [];
        }

        return database
            .LoadLatestMessages(conversation.AndroidMobileQQTableName, pageSize, filter.ToQueryFilter())
            .Select(message => MessageRecordFactory.FromAndroidMobileQQ(message, conversation))
            .ToList();
    }

    public List<MessageRecord> LoadEarliestMessages(
        AvaQQGroup conversation,
        int pageSize,
        MessageFilterCriteria filter)
    {
        if (_getMessageDatabase() is not { } database ||
            string.IsNullOrWhiteSpace(conversation.AndroidMobileQQTableName))
        {
            return [];
        }

        return database
            .LoadEarliestMessages(conversation.AndroidMobileQQTableName, pageSize, filter.ToQueryFilter())
            .Select(message => MessageRecordFactory.FromAndroidMobileQQ(message, conversation))
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
            string.IsNullOrWhiteSpace(conversation.AndroidMobileQQTableName))
        {
            return [];
        }

        return database
            .LoadOlderMessages(conversation.AndroidMobileQQTableName, messageSeq, messageId, pageSize, filter.ToQueryFilter())
            .Select(message => MessageRecordFactory.FromAndroidMobileQQ(message, conversation))
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
            string.IsNullOrWhiteSpace(conversation.AndroidMobileQQTableName))
        {
            return [];
        }

        return database
            .LoadNewerMessages(conversation.AndroidMobileQQTableName, messageSeq, messageId, pageSize, filter.ToQueryFilter())
            .Select(message => MessageRecordFactory.FromAndroidMobileQQ(message, conversation))
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
