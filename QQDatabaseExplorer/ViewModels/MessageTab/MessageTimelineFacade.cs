using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class MessageTimelineFacade
{
    private readonly MessageTimelineQuery _query;
    private readonly MessageFilterState _filterState;
    private readonly int _jumpContextPageSize;

    public MessageTimelineFacade(
        MessageTimelineQuery query,
        MessageFilterState filterState,
        int jumpContextPageSize)
    {
        _query = query;
        _filterState = filterState;
        _jumpContextPageSize = jumpContextPageSize;
    }

    public List<MessageRecord> LoadInitialMessages(
        AvaQQGroup conversation,
        int pageSize,
        Func<bool>? shouldContinue = null)
    {
        return _query.LoadInitialMessages(conversation, pageSize, GetCurrentFilter(conversation), shouldContinue);
    }

    public List<MessageRecord> LoadEarliestMessages(
        AvaQQGroup conversation,
        int pageSize,
        Func<bool>? shouldContinue = null)
    {
        return _query.LoadEarliestMessages(conversation, pageSize, GetCurrentFilter(conversation), shouldContinue);
    }

    public List<MessageRecord> LoadOlderMessages(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        int pageSize,
        Func<bool>? shouldContinue = null)
    {
        return _query.LoadOlderMessages(conversation, messageSeq, messageId, pageSize, GetCurrentFilter(conversation), shouldContinue);
    }

    public List<MessageRecord> LoadNewerMessages(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        int pageSize,
        Func<bool>? shouldContinue = null)
    {
        return _query.LoadNewerMessages(conversation, messageSeq, messageId, pageSize, GetCurrentFilter(conversation), shouldContinue);
    }

    public List<MessageRecord> LoadTargetAndNewerMessages(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        Func<bool>? shouldContinue = null)
    {
        var messages = _query.LoadNewerOrEqualMessages(
            conversation,
            messageSeq,
            messageId,
            _jumpContextPageSize + 1,
            GetCurrentFilter(conversation),
            shouldContinue);
        if (messages.Any(message => message.MessageId == messageId))
            return messages;

        var targetMessage = _query.LoadMessageById(
            conversation,
            messageSeq,
            messageId,
            GetCurrentFilter(conversation));
        if (targetMessage is null)
            return messages;

        return messages
            .Append(targetMessage)
            .DistinctBy(message => message.MessageId)
            .OrderBy(message => message.MessageSeq)
            .ThenBy(message => message.MessageId)
            .ToList();
    }

    public IReadOnlyList<MessageRecord> LoadAllMessages(AvaQQGroup conversation, int pageSize)
    {
        return _query.LoadAllMessages(conversation, pageSize);
    }

    private MessageFilterCriteria GetCurrentFilter(AvaQQGroup conversation)
    {
        return _filterState.Get(conversation);
    }
}
