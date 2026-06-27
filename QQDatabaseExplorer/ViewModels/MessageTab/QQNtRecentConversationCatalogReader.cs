using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal static class QQNtRecentConversationCatalogReader
{
    public static IReadOnlyList<MessageConversationCatalogItem> Read(QQMessageReader messageDatabase)
    {
        try
        {
            var recentContacts = messageDatabase.DbContext.RecentContacts
                .Where(v => v.ChatType == ChatType.GroupMessage || v.ChatType == ChatType.PrivateMessage)
                .OrderByDescending(v => v.SortTime == 0 ? v.LastTime : v.SortTime)
                .Select(v => new
                {
                    v.LastMessageId,
                    v.ChatType,
                    v.PeerUin,
                    v.Uin,
                    v.LastMessageType,
                    v.LastMessage,
                    v.LastTime,
                    v.MessageSeq,
                    v.MessageRandom,
                    v.SortTime,
                    v.Source,
                    v.SendremarkName,
                    v.SendMemberName,
                    v.SendNickName,
                    v.Uin2,
                    v.NtUid,
                    v.GroupAvatar,
                })
                .ToList();

            var recentGroupMessageKeys = recentContacts
                .Where(contact => contact.ChatType == ChatType.GroupMessage)
                .Select(contact => ConversationCatalogValueHelpers.TryParseRecentGroupId(contact.PeerUin, out var groupId)
                    ? new RecentGroupMessageKey(groupId, contact.LastMessageId, contact.MessageSeq, contact.MessageRandom)
                    : (RecentGroupMessageKey?)null)
                .Where(key => key is not null)
                .Select(key => key!.Value)
                .ToList();
            var recentGroupMessages = QQNtRecentMessageMatchRepository.ReadGroupMessageMatches(
                messageDatabase,
                recentGroupMessageKeys);

            var recentPrivateMessageKeys = recentContacts
                .Where(contact => contact.ChatType == ChatType.PrivateMessage)
                .Where(contact => !string.IsNullOrWhiteSpace(contact.PeerUin))
                .Select(contact => new RecentPrivateMessageKey(contact.PeerUin!, contact.LastMessageId, contact.MessageSeq, contact.MessageRandom))
                .ToList();
            var recentPrivateMessages = QQNtRecentMessageMatchRepository.ReadPrivateMessageMatches(
                messageDatabase,
                recentPrivateMessageKeys);

            return recentContacts
                .Select(contact =>
                {
                    if (contact.ChatType == ChatType.GroupMessage)
                    {
                        var groupId = ConversationCatalogValueHelpers.TryParseRecentGroupId(contact.PeerUin, out var parsedGroupId)
                            ? parsedGroupId
                            : 0;
                        var messageKey = new RecentGroupMessageKey(
                            groupId,
                            contact.LastMessageId,
                            contact.MessageSeq,
                            contact.MessageRandom);
                        var latestMessage = recentGroupMessages.TryGetValue(messageKey, out var matchedMessage)
                            ? matchedMessage
                            : (MessageRecord?)null;

                        return new MessageConversationCatalogItem(
                            AvaConversationType.Group,
                            groupId,
                            0,
                            0,
                            null,
                            contact.Source,
                            contact.LastMessage,
                            contact.LastMessageType,
                            contact.LastTime,
                            contact.Uin2,
                            contact.NtUid,
                            contact.SendremarkName,
                            contact.SendMemberName,
                            contact.SendNickName,
                            contact.GroupAvatar,
                            latestMessage);
                    }

                    if (!string.IsNullOrWhiteSpace(contact.PeerUin))
                    {
                        var messageKey = new RecentPrivateMessageKey(
                            contact.PeerUin,
                            contact.LastMessageId,
                            contact.MessageSeq,
                            contact.MessageRandom);
                        if (!recentPrivateMessages.TryGetValue(messageKey, out var privateMessage))
                            return null;

                        return new MessageConversationCatalogItem(
                            AvaConversationType.Private,
                            0,
                            privateMessage.ConversationId,
                            privateMessage.PrivateUin != 0 ? privateMessage.PrivateUin : contact.Uin,
                            contact.PeerUin,
                            ConversationCatalogValueHelpers.FirstNonEmpty(contact.SendremarkName, contact.Source, contact.SendNickName, contact.SendMemberName),
                            contact.LastMessage,
                            contact.LastMessageType,
                            contact.LastTime != 0 ? contact.LastTime : privateMessage.LastTime,
                            contact.Uin2,
                            contact.NtUid,
                            contact.SendremarkName,
                            contact.SendMemberName,
                            contact.SendNickName,
                            contact.GroupAvatar,
                            privateMessage.LatestMessage);
                    }

                    return null;
                })
                .Where(contact => contact is not null)
                .Select(contact => contact!)
                .Where(contact =>
                    contact.ConversationType == AvaConversationType.Group && contact.GroupId != 0 ||
                    contact.ConversationType == AvaConversationType.Private && contact.PrivateConversationId != 0)
                .DistinctBy(contact => contact.ConversationKey)
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
