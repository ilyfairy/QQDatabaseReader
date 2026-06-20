using System.Collections.Generic;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class QqNtReplySenderNameResolver
{
    private readonly MessageParticipantResolver _participantResolver;

    public QqNtReplySenderNameResolver(MessageParticipantResolver participantResolver)
    {
        _participantResolver = participantResolver;
    }

    public string Resolve(ReplySenderNameRequest request)
    {
        var currentSenderName = request.CurrentSenderName;
        var currentSenderId = request.CurrentSenderId;
        var conversation = request.Conversation;
        var reply = request.Reply;
        var groupSenderNames = request.GroupSenderNames;
        var messageSenderInfos = request.MessageSenderInfos;

        if (conversation.ConversationType == AvaConversationType.Group &&
            reply.SourceGroupId != 0 &&
            reply.SourceGroupId != conversation.GroupId &&
            !string.IsNullOrWhiteSpace(reply.SourceSenderName))
        {
            return reply.SourceSenderName;
        }

        if (reply.SenderId != 0 && reply.SenderId == currentSenderId)
            return currentSenderName ?? reply.SenderId.ToString();

        if (conversation.ConversationType == AvaConversationType.Private &&
            !string.IsNullOrWhiteSpace(reply.SourceSenderName))
        {
            return reply.SourceSenderName;
        }

        if (conversation.ConversationType == AvaConversationType.Private &&
            TryResolvePrivateReplySenderName(reply, messageSenderInfos, out var loadedSenderName))
        {
            return loadedSenderName;
        }

        if (reply.SenderId == 0)
            return string.Empty;

        if (conversation.ConversationType == AvaConversationType.Private)
        {
            if (conversation.PrivateUin != 0 &&
                reply.SenderId == conversation.PrivateUin)
            {
                return conversation.DisplayName;
            }

            return !string.IsNullOrWhiteSpace(reply.SourceSenderName)
                ? reply.SourceSenderName
                : _participantResolver.ResolveProfileDisplayName(reply.SenderId, null, fallback: "我");
        }

        return groupSenderNames.TryGetValue(reply.SenderId, out var senderName) &&
               !string.IsNullOrWhiteSpace(senderName)
            ? senderName
            : !string.IsNullOrWhiteSpace(reply.SourceSenderName)
                ? reply.SourceSenderName
                : _participantResolver.ResolveProfileDisplayName(reply.SenderId, null, fallback: reply.SenderId.ToString());
    }

    private static bool TryResolvePrivateReplySenderName(
        QQReplyMessage reply,
        IReadOnlyDictionary<long, MessageSenderInfo> messageSenderInfos,
        out string senderName)
    {
        senderName = string.Empty;

        if (reply.MessageSeq2 <= 0)
            return false;

        if (!messageSenderInfos.TryGetValue(reply.MessageSeq2, out var senderInfo) ||
            string.IsNullOrWhiteSpace(senderInfo.Name))
        {
            return false;
        }

        senderName = senderInfo.Name;
        return true;
    }
}
