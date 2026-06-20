using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class IcalinguaMessageTimelineProvider : IMessageTimelineProvider
{
    private readonly Func<IcalinguaMessageDatabaseSet?> _getMessageDatabases;

    public IcalinguaMessageTimelineProvider(Func<IcalinguaMessageDatabaseSet?> getMessageDatabases)
    {
        _getMessageDatabases = getMessageDatabases;
    }

    public bool CanLoad(AvaQQGroup conversation)
    {
        return ConversationTypeClassifier.IsIcalingua(conversation);
    }

    public MessageRecord? LoadMessage(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        MessageFilterCriteria filter)
    {
        if (_getMessageDatabases() is not { } database ||
            conversation.IcalinguaRoomId == 0)
        {
            return null;
        }

        var message = database.LoadMessage(conversation.IcalinguaRoomId, messageSeq, messageId);
        return message is null
            ? null
            : MessageRecordFactory.FromIcalingua(message, conversation);
    }

    public List<MessageRecord> LoadLatestMessages(
        AvaQQGroup conversation,
        int pageSize,
        MessageFilterCriteria filter)
    {
        if (_getMessageDatabases() is not { } database ||
            conversation.IcalinguaRoomId == 0)
        {
            return [];
        }

        return database
            .LoadLatestMessages(conversation.IcalinguaRoomId, pageSize, filter.ToQueryFilter())
            .Select(message => MessageRecordFactory.FromIcalingua(message, conversation))
            .ToList();
    }

    public List<MessageRecord> LoadEarliestMessages(
        AvaQQGroup conversation,
        int pageSize,
        MessageFilterCriteria filter)
    {
        if (_getMessageDatabases() is not { } database ||
            conversation.IcalinguaRoomId == 0)
        {
            return [];
        }

        return database
            .LoadEarliestMessages(conversation.IcalinguaRoomId, pageSize, filter.ToQueryFilter())
            .Select(message => MessageRecordFactory.FromIcalingua(message, conversation))
            .ToList();
    }

    public List<MessageRecord> LoadOlderMessages(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        int pageSize,
        MessageFilterCriteria filter)
    {
        if (_getMessageDatabases() is not { } database ||
            conversation.IcalinguaRoomId == 0)
        {
            return [];
        }

        return database
            .LoadOlderMessages(conversation.IcalinguaRoomId, messageSeq, messageId, pageSize, filter.ToQueryFilter())
            .Select(message => MessageRecordFactory.FromIcalingua(message, conversation))
            .ToList();
    }

    public List<MessageRecord> LoadNewerMessages(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        int pageSize,
        MessageFilterCriteria filter)
    {
        if (_getMessageDatabases() is not { } database ||
            conversation.IcalinguaRoomId == 0)
        {
            return [];
        }

        return database
            .LoadNewerMessages(conversation.IcalinguaRoomId, messageSeq, messageId, pageSize, filter.ToQueryFilter())
            .Select(message => MessageRecordFactory.FromIcalingua(message, conversation))
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
