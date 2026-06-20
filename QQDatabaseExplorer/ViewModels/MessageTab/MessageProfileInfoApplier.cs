using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class MessageProfileInfoApplier
{
    private readonly MessageParticipantResolver _participantResolver;
    private readonly MessageSenderCache _senderCache;

    public MessageProfileInfoApplier(
        MessageParticipantResolver participantResolver,
        MessageSenderCache senderCache)
    {
        _participantResolver = participantResolver;
        _senderCache = senderCache;
    }

    public bool Apply(
        IEnumerable<AvaQQGroup> conversations,
        IEnumerable<AvaQQMessage> visibleMessages,
        AvaQQGroup? selectedConversation)
    {
        var conversationNameChanged = ApplyPrivateConversationNames(conversations);
        RefreshVisiblePrivateMessageSenderNames(selectedConversation, visibleMessages);
        return conversationNameChanged;
    }

    private bool ApplyPrivateConversationNames(IEnumerable<AvaQQGroup> conversations)
    {
        var changed = false;
        foreach (var conversation in conversations.Where(static group => group.ConversationType == AvaConversationType.Private))
        {
            var profileName = _participantResolver.ResolveProfileDisplayName(
                conversation.PrivateUin,
                conversation.PrivateUid,
                fallback: string.Empty);
            if (string.IsNullOrWhiteSpace(profileName) ||
                string.Equals(conversation.GroupName, profileName, StringComparison.Ordinal))
            {
                continue;
            }

            conversation.GroupName = profileName;
            changed = true;
        }

        return changed;
    }

    private void RefreshVisiblePrivateMessageSenderNames(
        AvaQQGroup? selectedConversation,
        IEnumerable<AvaQQMessage> visibleMessages)
    {
        if (selectedConversation?.ConversationType != AvaConversationType.Private)
            return;

        var senderNames = _senderCache.GetSenderNameCache(selectedConversation.ConversationKey);
        foreach (var message in visibleMessages)
        {
            if (message.ConversationType != AvaConversationType.Private ||
                message.IsSystemHint ||
                message.SenderId == 0 ||
                MessageParticipantResolver.IsPrivatePeerMessage(message, selectedConversation))
            {
                continue;
            }

            var name = _participantResolver.ResolveProfileDisplayName(message.SenderId, null, fallback: string.Empty);
            if (string.IsNullOrWhiteSpace(name) ||
                string.Equals(message.Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            message.Name = name;
            senderNames[message.SenderId] = name;
        }
    }
}
