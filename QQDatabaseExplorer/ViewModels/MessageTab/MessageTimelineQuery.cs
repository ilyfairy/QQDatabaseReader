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
            new IcalinguaMessageTimelineProvider(() => databaseSource.IcalinguaMessageDatabases),
            new NtMessageTimelineProvider(
                () => databaseSource.MessageDatabase,
                () => databaseSource.AndroidMessageDatabase),
        ];
    }

    public List<MessageRecord> LoadInitialMessages(
        AvaQQGroup conversation,
        int pageSize,
        MessageFilterCriteria filter)
    {
        return GetProvider(conversation).LoadLatestMessages(conversation, pageSize, filter);
    }

    public List<MessageRecord> LoadEarliestMessages(
        AvaQQGroup conversation,
        int pageSize,
        MessageFilterCriteria filter)
    {
        return GetProvider(conversation).LoadEarliestMessages(conversation, pageSize, filter);
    }

    public List<MessageRecord> LoadOlderMessages(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        int pageSize,
        MessageFilterCriteria filter)
    {
        return GetProvider(conversation).LoadOlderMessages(conversation, messageSeq, messageId, pageSize, filter);
    }

    public List<MessageRecord> LoadNewerMessages(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        int pageSize,
        MessageFilterCriteria filter)
    {
        return GetProvider(conversation).LoadNewerMessages(conversation, messageSeq, messageId, pageSize, filter);
    }

    public List<MessageRecord> LoadNewerOrEqualMessages(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        int pageSize,
        MessageFilterCriteria filter)
    {
        return GetProvider(conversation).LoadNewerOrEqualMessages(conversation, messageSeq, messageId, pageSize, filter);
    }

    public MessageRecord? LoadMessageById(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        MessageFilterCriteria filter)
    {
        return GetProvider(conversation).LoadMessage(conversation, messageSeq, messageId, filter);
    }

    private IMessageTimelineProvider GetProvider(AvaQQGroup conversation)
    {
        return _providers.First(provider => provider.CanLoad(conversation));
    }
}

