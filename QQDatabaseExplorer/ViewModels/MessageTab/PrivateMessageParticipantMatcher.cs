using System;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal static class PrivateMessageParticipantMatcher
{
    public static bool IsPrivatePeerMessage(MessageRecord item, AvaQQGroup conversation)
    {
        if (!IsPrivateConversation(conversation))
            return false;

        if (MatchesPeerUin(item.SenderId, conversation.PrivateUin, item.PeerUin))
            return true;

        return MatchesPeerUid(item.SenderUid, conversation.PrivateUid, item.PeerUid);
    }

    public static bool IsPrivatePeerMessage(AvaQQMessage item, AvaQQGroup conversation)
    {
        if (!IsPrivateConversation(conversation))
            return false;

        if (item.SenderId != 0)
        {
            if (conversation.PrivateUin != 0 && item.SenderId == conversation.PrivateUin)
                return true;

            if (item.PrivateUin != 0 && item.SenderId == item.PrivateUin)
                return true;
        }

        return !string.IsNullOrWhiteSpace(item.PeerUid) &&
               !string.IsNullOrWhiteSpace(conversation.PrivateUid) &&
               string.Equals(item.PeerUid, conversation.PrivateUid, StringComparison.Ordinal) &&
               item.SenderId == conversation.PrivateUin;
    }

    private static bool IsPrivateConversation(AvaQQGroup conversation)
    {
        return conversation.ConversationType is AvaConversationType.Private or AvaConversationType.PCQQPrivate;
    }

    private static bool MatchesPeerUin(uint senderId, uint conversationPrivateUin, uint messagePeerUin)
    {
        if (senderId == 0)
            return false;

        if (conversationPrivateUin != 0 && senderId == conversationPrivateUin)
            return true;

        return messagePeerUin != 0 && senderId == messagePeerUin;
    }

    private static bool MatchesPeerUid(string? senderUid, string? conversationPrivateUid, string? messagePeerUid)
    {
        if (string.IsNullOrWhiteSpace(senderUid))
            return false;

        if (!string.IsNullOrWhiteSpace(conversationPrivateUid) &&
            string.Equals(senderUid, conversationPrivateUid, StringComparison.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(messagePeerUid) &&
               string.Equals(senderUid, messagePeerUid, StringComparison.Ordinal);
    }
}
