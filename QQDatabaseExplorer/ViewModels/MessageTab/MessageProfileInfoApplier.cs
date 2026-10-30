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
        AvaQQGroup? selectedConversation,
        IReadOnlyDictionary<ProfileInfoLookupRequest, string>? resolvedNames = null)
    {
        var conversationNameChanged = ApplyPrivateConversationNames(conversations, resolvedNames);
        RefreshVisiblePrivateMessageSenderNames(selectedConversation, visibleMessages, resolvedNames);
        return conversationNameChanged;
    }

    public IReadOnlyList<ProfileInfoLookupRequest> CreateLookupRequests(
        IEnumerable<AvaQQGroup> conversations,
        IEnumerable<AvaQQMessage> visibleMessages,
        AvaQQGroup? selectedConversation)
    {
        var requests = conversations
            .Where(static conversation => conversation.ConversationType == AvaConversationType.Private)
            .Select(static conversation => new ProfileInfoLookupRequest(
                conversation.PrivateUin,
                conversation.PrivateUid))
            .ToList();

        if (selectedConversation?.ConversationType == AvaConversationType.Private)
        {
            requests.AddRange(visibleMessages
                .Where(message => message.ConversationType == AvaConversationType.Private &&
                                  !message.IsSystemHint &&
                                  message.SenderId != 0 &&
                                  !MessageParticipantResolver.IsPrivatePeerMessage(message, selectedConversation))
                .Select(static message => new ProfileInfoLookupRequest(message.SenderId, null)));
        }

        return requests
            .Where(static request => request.Uin != 0 || !string.IsNullOrWhiteSpace(request.NtUid))
            .Distinct()
            .ToArray();
    }

    private bool ApplyPrivateConversationNames(
        IEnumerable<AvaQQGroup> conversations,
        IReadOnlyDictionary<ProfileInfoLookupRequest, string>? resolvedNames)
    {
        var changed = false;
        foreach (var conversation in conversations.Where(static group => group.ConversationType == AvaConversationType.Private))
        {
            var request = new ProfileInfoLookupRequest(conversation.PrivateUin, conversation.PrivateUid);
            var profileName = ResolveProfileDisplayName(request, resolvedNames);
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
        IEnumerable<AvaQQMessage> visibleMessages,
        IReadOnlyDictionary<ProfileInfoLookupRequest, string>? resolvedNames)
    {
        if (selectedConversation?.ConversationType != AvaConversationType.Private)
            return;

        foreach (var message in visibleMessages)
        {
            if (message.ConversationType != AvaConversationType.Private ||
                message.IsSystemHint ||
                message.SenderId == 0 ||
                MessageParticipantResolver.IsPrivatePeerMessage(message, selectedConversation))
            {
                continue;
            }

            var name = ResolveProfileDisplayName(
                new ProfileInfoLookupRequest(message.SenderId, null),
                resolvedNames);
            if (string.IsNullOrWhiteSpace(name) ||
                string.Equals(message.Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            message.Name = name;
            _senderCache.SetSenderName(selectedConversation.ConversationKey, message.SenderId, name);
        }
    }

    private string ResolveProfileDisplayName(
        ProfileInfoLookupRequest request,
        IReadOnlyDictionary<ProfileInfoLookupRequest, string>? resolvedNames)
    {
        if (resolvedNames is not null)
        {
            return resolvedNames.GetValueOrDefault(request) ?? string.Empty;
        }

        return _participantResolver.ResolveProfileDisplayName(
            request.Uin,
            request.NtUid,
            fallback: string.Empty);
    }
}
