using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal static class AndroidQQNtRecentConversationCatalogReader
{
    public static IReadOnlyList<MessageConversationCatalogItem> Read(QQAndroidMessageReader messageDatabase)
    {
        try
        {
            var recentContacts = messageDatabase.DbContext.RecentContacts
                .Where(v =>
                    (v.RecentCategory == (int)ChatType.GroupMessage && v.ChatType == ChatType.GroupMessage) ||
                    (v.RecentCategory == (int)ChatType.PrivateMessage && v.ChatType == ChatType.PrivateMessage))
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
                    ? new RecentGroupMessageKey(groupId, contact.LastMessageId, contact.MessageSeq, 0)
                    : (RecentGroupMessageKey?)null)
                .Where(key => key is not null)
                .Select(key => key!.Value)
                .ToList();
            var recentGroupMessages = ReadRecentGroupMessageMatches(messageDatabase, recentGroupMessageKeys);

            var recentPrivateMessageKeys = recentContacts
                .Where(contact => contact.ChatType == ChatType.PrivateMessage)
                .Where(contact => !string.IsNullOrWhiteSpace(contact.PeerUin))
                .Select(contact => new RecentPrivateMessageKey(contact.PeerUin!, contact.LastMessageId, contact.MessageSeq, 0))
                .ToList();
            var recentPrivateMessages = ReadRecentPrivateMessageMatches(messageDatabase, recentPrivateMessageKeys);

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
                            0);
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
                            0);
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
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to read the Android QQNT recent-conversation catalog.", ex);
        }
    }

    private static IReadOnlyDictionary<RecentGroupMessageKey, MessageRecord> ReadRecentGroupMessageMatches(
        QQAndroidMessageReader messageDatabase,
        IReadOnlyCollection<RecentGroupMessageKey> messageKeys)
    {
        if (messageKeys.Count == 0)
            return new Dictionary<RecentGroupMessageKey, MessageRecord>();

        var result = new Dictionary<RecentGroupMessageKey, MessageRecord>();
        foreach (var key in messageKeys.Distinct())
        {
            var message = FindRecentGroupMessageCandidate(messageDatabase, key);
            if (message is { } matchedMessage)
            {
                result[key] = matchedMessage;
            }
        }

        return result;
    }

    private static MessageRecord? FindRecentGroupMessageCandidate(
        QQAndroidMessageReader messageDatabase,
        RecentGroupMessageKey key)
    {
        if (key.GroupId == 0 || key.MessageId == 0 && key.MessageSeq == 0)
            return null;

        var query = messageDatabase.DbContext.GroupMessages
            .Where(message => message.GroupId == key.GroupId)
            .Where(message => message.MessageType != MessageType.Empty);

        if (key.MessageId != 0)
        {
            var idMessage = query
                .Where(message => message.MessageId == key.MessageId)
                .Take(1)
                .AsEnumerable()
                .Select(message => (MessageRecord?)MessageRecordFactory.FromQQNtGroup(message))
                .FirstOrDefault();
            if (idMessage is not null)
                return idMessage;
        }

        if (key.MessageSeq == 0)
            return null;

        return query
            .Where(message => message.MessageSeq == key.MessageSeq)
            .OrderByDescending(message => message.MessageId)
            .Take(1)
            .AsEnumerable()
            .Select(message => (MessageRecord?)MessageRecordFactory.FromQQNtGroup(message))
            .FirstOrDefault();
    }

    private static IReadOnlyDictionary<RecentPrivateMessageKey, RecentPrivateMessageMatch> ReadRecentPrivateMessageMatches(
        QQAndroidMessageReader messageDatabase,
        IReadOnlyCollection<RecentPrivateMessageKey> messageKeys)
    {
        if (messageKeys.Count == 0)
            return new Dictionary<RecentPrivateMessageKey, RecentPrivateMessageMatch>();

        var keys = messageKeys
            .Where(key => key.MessageId != 0 && !string.IsNullOrWhiteSpace(key.PeerUid))
            .Distinct()
            .ToArray();
        if (keys.Length == 0)
            return new Dictionary<RecentPrivateMessageKey, RecentPrivateMessageMatch>();

        var messages = new Dictionary<long, MessageRecord>();
        foreach (var batch in keys.Select(key => key.MessageId).Distinct().Chunk(500))
        {
            foreach (var message in messageDatabase.DbContext.PrivateMessages
                         .Where(message => batch.Contains(message.MessageId))
                         .Where(message => message.MessageType != MessageType.Empty)
                         .AsEnumerable()
                         .Select(MessageRecordFactory.FromQQNtPrivate))
            {
                messages[message.MessageId] = message;
            }
        }

        var result = new Dictionary<RecentPrivateMessageKey, RecentPrivateMessageMatch>();
        foreach (var key in keys)
        {
            if (!messages.TryGetValue(key.MessageId, out var message) ||
                message.PrivateConversationId == 0 ||
                !string.Equals(message.PeerUid, key.PeerUid, StringComparison.Ordinal))
            {
                continue;
            }

            result[key] = new RecentPrivateMessageMatch(
                message.PrivateConversationId,
                message.PeerUin,
                message.MessageTime,
                message);
        }

        return result;
    }
}
