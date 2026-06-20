using System.Collections.Generic;
using System.Linq;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal static class QQNtRecentMessageMatchRepository
{
    public static IReadOnlyDictionary<RecentGroupMessageKey, MessageRecord> ReadGroupMessageMatches(
        QQMessageReader messageDatabase,
        IReadOnlyCollection<RecentGroupMessageKey> messageKeys)
    {
        if (messageKeys.Count == 0)
            return new Dictionary<RecentGroupMessageKey, MessageRecord>();

        var result = new Dictionary<RecentGroupMessageKey, MessageRecord>();
        foreach (var key in messageKeys.Distinct())
        {
            var message = FindGroupMessageCandidate(messageDatabase, key);
            if (message is { } matchedMessage)
            {
                result[key] = matchedMessage;
            }
        }

        return result;
    }

    public static IReadOnlyDictionary<RecentPrivateMessageKey, RecentPrivateMessageMatch> ReadPrivateMessageMatches(
        QQMessageReader messageDatabase,
        IReadOnlyCollection<RecentPrivateMessageKey> messageKeys)
    {
        if (messageKeys.Count == 0)
            return new Dictionary<RecentPrivateMessageKey, RecentPrivateMessageMatch>();

        var result = new Dictionary<RecentPrivateMessageKey, RecentPrivateMessageMatch>();
        foreach (var key in messageKeys.Distinct())
        {
            var latestMessage = FindPrivateMessageCandidate(messageDatabase, key);
            var conversationMessage = latestMessage ?? FindLatestPrivateConversationMessage(messageDatabase, key.PeerUid);
            if (conversationMessage is not { } message || message.PrivateConversationId == 0)
                continue;

            result[key] = new RecentPrivateMessageMatch(
                message.PrivateConversationId,
                message.PeerUin,
                message.MessageTime,
                latestMessage);
        }

        return result;
    }

    private static MessageRecord? FindGroupMessageCandidate(QQMessageReader messageDatabase, RecentGroupMessageKey key)
    {
        if (key.GroupId == 0 || key.MessageId == 0 && key.MessageSeq == 0 && key.MessageRandom == 0)
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

        if (key.MessageSeq != 0 && key.MessageRandom != 0)
        {
            var exactMessage = query
                .Where(message => message.MessageSeq == key.MessageSeq &&
                                  message.MessageRandom == key.MessageRandom)
                .OrderByDescending(message => message.MessageSeq)
                .ThenByDescending(message => message.MessageId)
                .Take(1)
                .AsEnumerable()
                .Select(message => (MessageRecord?)MessageRecordFactory.FromQQNtGroup(message))
                .FirstOrDefault();
            if (exactMessage is not null)
                return exactMessage;
        }

        if (key.MessageRandom != 0)
        {
            var randomMessage = query
                .Where(message => message.MessageRandom == key.MessageRandom)
                .OrderByDescending(message => message.MessageSeq)
                .ThenByDescending(message => message.MessageId)
                .Take(1)
                .AsEnumerable()
                .Select(message => (MessageRecord?)MessageRecordFactory.FromQQNtGroup(message))
                .FirstOrDefault();
            if (randomMessage is not null)
                return randomMessage;
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

    private static MessageRecord? FindPrivateMessageCandidate(
        QQMessageReader messageDatabase,
        RecentPrivateMessageKey key)
    {
        if (string.IsNullOrWhiteSpace(key.PeerUid) ||
            key.MessageId == 0 && key.MessageSeq == 0 && key.MessageRandom == 0)
        {
            return null;
        }

        var query = messageDatabase.DbContext.PrivateMessages
            .Where(message => message.PeerUid == key.PeerUid)
            .Where(message => message.MessageType != MessageType.Empty);

        if (key.MessageId != 0)
        {
            var idMessage = query
                .Where(message => message.MessageId == key.MessageId)
                .Take(1)
                .AsEnumerable()
                .Select(message => (MessageRecord?)MessageRecordFactory.FromQQNtPrivate(message))
                .FirstOrDefault();
            if (idMessage is not null)
                return idMessage;
        }

        if (key.MessageSeq != 0 && key.MessageRandom != 0)
        {
            var exactMessage = query
                .Where(message => message.MessageSeq == key.MessageSeq &&
                                  message.MessageRandom == key.MessageRandom)
                .OrderByDescending(message => message.MessageSeq)
                .ThenByDescending(message => message.MessageId)
                .Take(1)
                .AsEnumerable()
                .Select(message => (MessageRecord?)MessageRecordFactory.FromQQNtPrivate(message))
                .FirstOrDefault();
            if (exactMessage is not null)
                return exactMessage;
        }

        if (key.MessageRandom != 0)
        {
            var randomMessage = query
                .Where(message => message.MessageRandom == key.MessageRandom)
                .OrderByDescending(message => message.MessageSeq)
                .ThenByDescending(message => message.MessageId)
                .Take(1)
                .AsEnumerable()
                .Select(message => (MessageRecord?)MessageRecordFactory.FromQQNtPrivate(message))
                .FirstOrDefault();
            if (randomMessage is not null)
                return randomMessage;
        }

        if (key.MessageSeq == 0)
            return null;

        return query
            .Where(message => message.MessageSeq == key.MessageSeq)
            .OrderByDescending(message => message.MessageId)
            .Take(1)
            .AsEnumerable()
            .Select(message => (MessageRecord?)MessageRecordFactory.FromQQNtPrivate(message))
            .FirstOrDefault();
    }

    private static MessageRecord? FindLatestPrivateConversationMessage(
        QQMessageReader messageDatabase,
        string peerUid)
    {
        if (string.IsNullOrWhiteSpace(peerUid))
            return null;

        return messageDatabase.DbContext.PrivateMessages
            .Where(message => message.PeerUid == peerUid)
            .Where(message => message.ConversationId != 0)
            .Where(message => message.MessageType != MessageType.Empty)
            .OrderByDescending(message => message.MessageSeq)
            .ThenByDescending(message => message.MessageId)
            .Take(1)
            .AsEnumerable()
            .Select(message => (MessageRecord?)MessageRecordFactory.FromQQNtPrivate(message))
            .FirstOrDefault();
    }
}
