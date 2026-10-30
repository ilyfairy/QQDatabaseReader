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

        var keys = messageKeys
            .Where(key => key.MessageId != 0 && !string.IsNullOrWhiteSpace(key.PeerUid))
            .Distinct()
            .ToArray();
        if (keys.Length == 0)
            return new Dictionary<RecentPrivateMessageKey, RecentPrivateMessageMatch>();

        var messages = new Dictionary<long, MessageRecord>();
        foreach (var messageIdBatch in keys
                     .Select(key => key.MessageId)
                     .Distinct()
                     .Chunk(500))
        {
            foreach (var message in messageDatabase.DbContext.PrivateMessages
                         .Where(message => messageIdBatch.Contains(message.MessageId))
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
                message.PeerUid != key.PeerUid)
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

}
