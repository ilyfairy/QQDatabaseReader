using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class AndroidMobileQQMessageFilterOptionSource(QQDatabaseService databaseService) : IMessageFilterOptionSource
{
    public bool CanLoadDateOptions(AvaQQGroup conversation)
    {
        return ConversationTypeClassifier.IsAndroidMobileQQ(conversation);
    }

    public IReadOnlyList<MessageDateFilterOption> LoadDateOptions(AvaQQGroup conversation)
    {
        if (databaseService.AndroidMobileQQMessageDatabase is not { } database ||
            string.IsNullOrWhiteSpace(conversation.AndroidMobileQQTableName))
        {
            return [];
        }

        try
        {
            return database
                .LoadMessageDates(conversation.AndroidMobileQQTableName)
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
        return conversation.ConversationType == AvaConversationType.AndroidMobileQQGroup;
    }

    public IReadOnlyList<MessageSenderFilterOption>? TryLoadSenderOptions(AvaQQGroup conversation)
    {
        if (databaseService.AndroidMobileQQMessageDatabase is not { } database ||
            string.IsNullOrWhiteSpace(conversation.AndroidMobileQQPeerUin))
        {
            return null;
        }

        try
        {
            return database
                .LoadSenders(conversation.AndroidMobileQQPeerUin)
                .Where(sender => sender.Uin != 0)
                .Select(sender => new MessageSenderFilterOption(sender.Uin, sender.DisplayName))
                .OrderBy(sender => sender.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(sender => sender.SenderId)
                .ToArray();
        }
        catch
        {
            return null;
        }
    }
}
