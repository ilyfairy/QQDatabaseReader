using System.Collections.Generic;
using System.Linq;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class NtMessageJumpTargetRepository(IMessageDatabaseSource databaseSource)
{
    public IReadOnlyList<MessageRecord> FindGroupCandidates(
        uint groupId,
        MessageJumpTargetCandidate candidate,
        int limit)
    {
        if (CreateGroupQuery(groupId) is not { } query)
            return [];

        query = ApplyCandidate(query, candidate);
        return query
            .Take(limit)
            .AsEnumerable()
            .Select(MessageRecordFactory.FromQQNtGroup)
            .ToArray();
    }

    public IReadOnlyList<MessageRecord> FindPrivateCandidates(
        long conversationId,
        MessageJumpTargetCandidate candidate,
        int limit)
    {
        if (CreatePrivateQuery(conversationId) is not { } query)
            return [];

        query = ApplyCandidate(query, candidate);
        return query
            .Take(limit)
            .AsEnumerable()
            .Select(MessageRecordFactory.FromQQNtPrivate)
            .ToArray();
    }

    public IReadOnlyList<MessageRecord> FindPrivateMessagesBySeqs(
        long conversationId,
        IReadOnlyCollection<long> messageSeqs)
    {
        if (messageSeqs.Count == 0 ||
            CreatePrivateQuery(conversationId) is not { } query)
        {
            return [];
        }

        return query
            .Where(message => messageSeqs.Contains(message.MessageSeq))
            .AsEnumerable()
            .Select(MessageRecordFactory.FromQQNtPrivate)
            .ToArray();
    }

    private IQueryable<GroupMessage>? CreateGroupQuery(uint groupId)
    {
        IQueryable<GroupMessage>? query = null;
        if (databaseSource.MessageDatabase is { } messageDatabase)
        {
            query = messageDatabase.DbContext.GroupMessages;
        }
        else if (databaseSource.AndroidMessageDatabase is { } androidMessageDatabase)
        {
            query = androidMessageDatabase.DbContext.GroupMessages;
        }

        return query?
            .Where(message => message.GroupId == groupId)
            .Where(message => message.MessageType != MessageType.Empty);
    }

    private IQueryable<PrivateMessage>? CreatePrivateQuery(long conversationId)
    {
        IQueryable<PrivateMessage>? query = null;
        if (databaseSource.MessageDatabase is { } messageDatabase)
        {
            query = messageDatabase.DbContext.PrivateMessages;
        }
        else if (databaseSource.AndroidMessageDatabase is { } androidMessageDatabase)
        {
            query = androidMessageDatabase.DbContext.PrivateMessages;
        }

        return query?
            .Where(message => message.ConversationId == conversationId)
            .Where(message => message.MessageType != MessageType.Empty);
    }

    private static IQueryable<GroupMessage> ApplyCandidate(
        IQueryable<GroupMessage> query,
        MessageJumpTargetCandidate candidate)
    {
        if (candidate.MessageRandom is { } messageRandom)
            query = query.Where(message => message.MessageRandom == messageRandom);
        if (candidate.MessageSeq is { } messageSeq)
            query = query.Where(message => message.MessageSeq == messageSeq);
        if (candidate.MessageId is { } messageId)
            query = query.Where(message => message.MessageId == messageId);

        return query;
    }

    private static IQueryable<PrivateMessage> ApplyCandidate(
        IQueryable<PrivateMessage> query,
        MessageJumpTargetCandidate candidate)
    {
        if (candidate.MessageRandom is { } messageRandom)
            query = query.Where(message => message.MessageRandom == messageRandom);
        if (candidate.MessageSeq is { } messageSeq)
            query = query.Where(message => message.MessageSeq == messageSeq);
        if (candidate.MessageId is { } messageId)
            query = query.Where(message => message.MessageId == messageId);

        return query;
    }
}

internal readonly record struct MessageJumpTargetCandidate(
    long? MessageRandom = null,
    long? MessageSeq = null,
    long? MessageId = null);
