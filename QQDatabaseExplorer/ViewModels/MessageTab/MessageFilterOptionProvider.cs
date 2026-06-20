using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class MessageFilterOptionProvider
{
    private readonly IReadOnlyList<IMessageFilterOptionSource> _sources;

    public MessageFilterOptionProvider(QQDatabaseService databaseService)
    {
        _sources =
        [
            new PCQQMessageFilterOptionSource(databaseService),
            new IcalinguaMessageFilterOptionSource(databaseService),
            new NtMessageFilterOptionSource(databaseService),
        ];
    }

    public MessageFilterDialogRequest CreateDialogRequest(
        AvaQQGroup conversation,
        MessageFilterCriteria currentFilter,
        IReadOnlyList<MessageSenderFilterOption> fallbackSenderCandidates)
    {
        return new MessageFilterDialogRequest(
            conversation.ConversationType,
            currentFilter,
            LoadDateOptions(conversation),
            TryLoadSenderOptions(conversation) ?? fallbackSenderCandidates);
    }

    public IReadOnlyList<MessageDateFilterOption> LoadDateOptions(AvaQQGroup conversation)
    {
        return _sources.FirstOrDefault(source => source.CanLoadDateOptions(conversation))
            ?.LoadDateOptions(conversation) ?? [];
    }

    public IReadOnlyList<MessageSenderFilterOption>? TryLoadSenderOptions(AvaQQGroup conversation)
    {
        return _sources.FirstOrDefault(source => source.CanLoadSenderOptions(conversation))
            ?.TryLoadSenderOptions(conversation) ?? [];
    }
}
