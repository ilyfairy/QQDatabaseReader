using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class IcalinguaMessageFilterOptionSource(QQDatabaseService databaseService) : IMessageFilterOptionSource
{
    public bool CanLoadDateOptions(AvaQQGroup conversation)
    {
        return ConversationTypeClassifier.IsIcalingua(conversation);
    }

    public IReadOnlyList<MessageDateFilterOption> LoadDateOptions(AvaQQGroup conversation)
    {
        if (databaseService.IcalinguaMessageDatabases is not { } database ||
            conversation.IcalinguaRoomId == 0)
        {
            return [];
        }

        try
        {
            return database
                .LoadMessageDates(conversation.IcalinguaRoomId)
                .Select(day => new MessageDateFilterOption(day.DayStartTime, day.MessageCount))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public bool CanLoadSenderOptions(AvaQQGroup conversation)
    {
        return conversation.ConversationType == AvaConversationType.Icalingua;
    }

    public IReadOnlyList<MessageSenderFilterOption>? TryLoadSenderOptions(AvaQQGroup conversation)
    {
        if (databaseService.IcalinguaMessageDatabases is not { } database ||
            conversation.IcalinguaRoomId == 0)
        {
            return null;
        }

        try
        {
            var candidates = database
                .LoadSenders(conversation.IcalinguaRoomId)
                .Select(sender => new MessageSenderFilterOption(
                    sender.SenderId,
                    MessageFilterOptionText.FirstNonEmpty(sender.DisplayName, sender.SenderId.ToString())))
                .OrderBy(sender => sender.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(sender => sender.SenderId)
                .ToArray();
            return candidates.Length == 0 ? null : candidates;
        }
        catch
        {
            return null;
        }
    }
}
