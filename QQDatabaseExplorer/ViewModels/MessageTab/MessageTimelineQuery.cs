using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class MessageTimelineQuery
{
    private readonly IReadOnlyList<IMessageTimelineProvider> _providers;

    public MessageTimelineQuery(IMessageDatabaseSource databaseSource)
    {
        _providers =
        [
            new PCQQMessageTimelineProvider(() => databaseSource.PCQQMessageDatabase),
            new AndroidMobileQQMessageTimelineProvider(() => databaseSource.AndroidMobileQQMessageDatabase),
            new IcalinguaMessageTimelineProvider(() => databaseSource.IcalinguaMessageDatabases),
            new NtMessageTimelineProvider(
                () => databaseSource.MessageDatabase,
                () => databaseSource.AndroidMessageDatabase),
        ];
    }

    public List<MessageRecord> LoadInitialMessages(
        AvaQQGroup conversation,
        int pageSize,
        MessageFilterCriteria filter,
        Func<bool>? shouldContinue = null)
    {
        var provider = GetProvider(conversation);
        return filter.HasContentFilter
            ? LoadLatestWithContentFilter(provider, conversation, pageSize, filter, shouldContinue)
            : provider.LoadLatestMessages(conversation, pageSize, filter);
    }

    public List<MessageRecord> LoadEarliestMessages(
        AvaQQGroup conversation,
        int pageSize,
        MessageFilterCriteria filter,
        Func<bool>? shouldContinue = null)
    {
        var provider = GetProvider(conversation);
        return filter.HasContentFilter
            ? LoadEarliestWithContentFilter(provider, conversation, pageSize, filter, shouldContinue)
            : provider.LoadEarliestMessages(conversation, pageSize, filter);
    }

    public List<MessageRecord> LoadOlderMessages(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        int pageSize,
        MessageFilterCriteria filter,
        Func<bool>? shouldContinue = null)
    {
        var provider = GetProvider(conversation);
        return filter.HasContentFilter
            ? LoadOlderWithContentFilter(provider, conversation, messageSeq, messageId, pageSize, filter, shouldContinue)
            : provider.LoadOlderMessages(conversation, messageSeq, messageId, pageSize, filter);
    }

    public List<MessageRecord> LoadNewerMessages(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        int pageSize,
        MessageFilterCriteria filter,
        Func<bool>? shouldContinue = null)
    {
        var provider = GetProvider(conversation);
        return filter.HasContentFilter
            ? LoadNewerWithContentFilter(provider, conversation, messageSeq, messageId, pageSize, filter, shouldContinue)
            : provider.LoadNewerMessages(conversation, messageSeq, messageId, pageSize, filter);
    }

    public List<MessageRecord> LoadNewerOrEqualMessages(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        int pageSize,
        MessageFilterCriteria filter,
        Func<bool>? shouldContinue = null)
    {
        var provider = GetProvider(conversation);
        return filter.HasContentFilter
            ? LoadNewerOrEqualWithContentFilter(provider, conversation, messageSeq, messageId, pageSize, filter, shouldContinue)
            : provider.LoadNewerOrEqualMessages(conversation, messageSeq, messageId, pageSize, filter);
    }

    public MessageRecord? LoadMessageById(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        MessageFilterCriteria filter)
    {
        var provider = GetProvider(conversation);
        var message = provider.LoadMessage(conversation, messageSeq, messageId, filter);
        return message is not null && MessageContentFilterMatcher.Matches(conversation, message, filter)
            ? message
            : null;
    }

    public IReadOnlyList<MessageRecord> LoadAllMessages(AvaQQGroup conversation, int pageSize)
    {
        return MessageTimelineExporter.LoadAll(GetProvider(conversation), conversation, pageSize);
    }

    private IMessageTimelineProvider GetProvider(AvaQQGroup conversation)
    {
        return _providers.First(provider => provider.CanLoad(conversation));
    }

    private static List<MessageRecord> LoadLatestWithContentFilter(
        IMessageTimelineProvider provider,
        AvaQQGroup conversation,
        int pageSize,
        MessageFilterCriteria filter,
        Func<bool>? shouldContinue)
    {
        return LoadWithContentFilter(
            conversation,
            pageSize,
            filter,
            () => provider.LoadLatestMessages(conversation, pageSize, filter),
            oldest => provider.LoadOlderMessages(conversation, oldest.MessageSeq, oldest.MessageId, pageSize, filter),
            SelectOldest,
            OrderAscending,
            shouldContinue);
    }

    private static List<MessageRecord> LoadEarliestWithContentFilter(
        IMessageTimelineProvider provider,
        AvaQQGroup conversation,
        int pageSize,
        MessageFilterCriteria filter,
        Func<bool>? shouldContinue)
    {
        return LoadWithContentFilter(
            conversation,
            pageSize,
            filter,
            () => provider.LoadEarliestMessages(conversation, pageSize, filter),
            newest => provider.LoadNewerMessages(conversation, newest.MessageSeq, newest.MessageId, pageSize, filter),
            SelectNewest,
            OrderAscending,
            shouldContinue);
    }

    private static List<MessageRecord> LoadOlderWithContentFilter(
        IMessageTimelineProvider provider,
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        int pageSize,
        MessageFilterCriteria filter,
        Func<bool>? shouldContinue)
    {
        return LoadWithContentFilter(
            conversation,
            pageSize,
            filter,
            () => provider.LoadOlderMessages(conversation, messageSeq, messageId, pageSize, filter),
            oldest => provider.LoadOlderMessages(conversation, oldest.MessageSeq, oldest.MessageId, pageSize, filter),
            SelectOldest,
            OrderAscending,
            shouldContinue);
    }

    private static List<MessageRecord> LoadNewerWithContentFilter(
        IMessageTimelineProvider provider,
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        int pageSize,
        MessageFilterCriteria filter,
        Func<bool>? shouldContinue)
    {
        return LoadWithContentFilter(
            conversation,
            pageSize,
            filter,
            () => provider.LoadNewerMessages(conversation, messageSeq, messageId, pageSize, filter),
            newest => provider.LoadNewerMessages(conversation, newest.MessageSeq, newest.MessageId, pageSize, filter),
            SelectNewest,
            OrderAscending,
            shouldContinue);
    }

    private static List<MessageRecord> LoadNewerOrEqualWithContentFilter(
        IMessageTimelineProvider provider,
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        int pageSize,
        MessageFilterCriteria filter,
        Func<bool>? shouldContinue)
    {
        return LoadWithContentFilter(
            conversation,
            pageSize,
            filter,
            () => provider.LoadNewerOrEqualMessages(conversation, messageSeq, messageId, pageSize, filter),
            newest => provider.LoadNewerMessages(conversation, newest.MessageSeq, newest.MessageId, pageSize, filter),
            SelectNewest,
            OrderAscending,
            shouldContinue);
    }

    private static List<MessageRecord> LoadWithContentFilter(
        AvaQQGroup conversation,
        int pageSize,
        MessageFilterCriteria filter,
        Func<List<MessageRecord>> loadFirstPage,
        Func<MessageRecord, List<MessageRecord>> loadNextPage,
        Func<IReadOnlyList<MessageRecord>, MessageRecord> selectAnchor,
        Func<IEnumerable<MessageRecord>, IOrderedEnumerable<MessageRecord>> orderResult,
        Func<bool>? shouldContinue)
    {
        var result = new List<MessageRecord>(pageSize);
        shouldContinue ??= static () => true;
        if (!shouldContinue())
            return result;

        var rawPage = loadFirstPage();
        var previousAnchorKey = string.Empty;

        while (rawPage.Count > 0)
        {
            AddMatchingMessages(result, conversation, rawPage, filter, pageSize);
            if (result.Count >= pageSize || rawPage.Count < pageSize || !shouldContinue())
                break;

            var anchor = selectAnchor(rawPage);
            var anchorKey = CreateAnchorKey(anchor);
            if (string.Equals(anchorKey, previousAnchorKey, StringComparison.Ordinal))
                break;

            previousAnchorKey = anchorKey;
            rawPage = loadNextPage(anchor);
        }

        return orderResult(result).ToList();
    }

    private static void AddMatchingMessages(
        List<MessageRecord> result,
        AvaQQGroup conversation,
        IEnumerable<MessageRecord> rawPage,
        MessageFilterCriteria filter,
        int pageSize)
    {
        foreach (var message in rawPage)
        {
            if (result.Count >= pageSize)
                break;

            if (MessageContentFilterMatcher.Matches(conversation, message, filter))
                result.Add(message);
        }
    }

    private static string CreateAnchorKey(MessageRecord message)
    {
        return string.Join(':', message.MessageSeq, message.MessageId, message.MessageRandom);
    }

    private static MessageRecord SelectOldest(IReadOnlyList<MessageRecord> messages)
    {
        return messages
            .OrderBy(static message => message.MessageSeq)
            .ThenBy(static message => message.MessageId)
            .First();
    }

    private static MessageRecord SelectNewest(IReadOnlyList<MessageRecord> messages)
    {
        return messages
            .OrderBy(static message => message.MessageSeq)
            .ThenBy(static message => message.MessageId)
            .Last();
    }

    private static IOrderedEnumerable<MessageRecord> OrderAscending(IEnumerable<MessageRecord> messages)
    {
        return messages
            .OrderBy(static message => message.MessageSeq)
            .ThenBy(static message => message.MessageId);
    }

    private static IOrderedEnumerable<MessageRecord> OrderDescending(IEnumerable<MessageRecord> messages)
    {
        return messages
            .OrderByDescending(static message => message.MessageSeq)
            .ThenByDescending(static message => message.MessageId);
    }
}

