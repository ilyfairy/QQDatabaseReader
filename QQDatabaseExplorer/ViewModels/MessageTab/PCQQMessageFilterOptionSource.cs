using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class PCQQMessageFilterOptionSource(QQDatabaseService databaseService) : IMessageFilterOptionSource
{
    public bool CanLoadDateOptions(AvaQQGroup conversation)
    {
        return ConversationTypeClassifier.IsPCQQ(conversation);
    }

    public IReadOnlyList<MessageDateFilterOption> LoadDateOptions(AvaQQGroup conversation)
    {
        if (databaseService.PCQQMessageDatabase is not { } pcqqDatabase ||
            string.IsNullOrWhiteSpace(conversation.PCQQTableName))
        {
            return [];
        }

        try
        {
            return pcqqDatabase
                .LoadMessageDates(conversation.PCQQTableName)
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
        return conversation.ConversationType == AvaConversationType.PCQQGroup;
    }

    public IReadOnlyList<MessageSenderFilterOption>? TryLoadSenderOptions(AvaQQGroup conversation)
    {
        var infoDatabase = databaseService.PCQQMessageDatabase?.InfoDatabase;
        if (infoDatabase is null)
            return null;

        try
        {
            return infoDatabase.GetContacts()
                .Values
                .Where(contact => contact.Uin != 0)
                .Select(contact => new MessageSenderFilterOption(
                    contact.Uin,
                    MessageFilterOptionText.FirstNonEmpty(contact.RemarkName, contact.Nickname, contact.Uin.ToString())))
                .OrderBy(contact => contact.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(contact => contact.SenderId)
                .ToArray();
        }
        catch
        {
            return null;
        }
    }
}
