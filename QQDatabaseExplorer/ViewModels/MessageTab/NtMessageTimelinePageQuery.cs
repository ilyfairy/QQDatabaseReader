using System.Collections.Generic;
using System.Linq;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal static class NtMessageTimelinePageQuery
{
    public static List<MessageRecord> LoadLatestMessages(IQueryable<GroupMessage> query, int pageSize)
    {
        return query
            .OrderByDescending(message => message.MessageSeq)
            .ThenByDescending(message => message.MessageId)
            .Take(pageSize)
            .OrderBy(message => message.MessageSeq)
            .ThenBy(message => message.MessageId)
            .Select(message => MessageRecordFactory.FromQQNtGroup(message))
            .ToList();
    }

    public static List<MessageRecord> LoadLatestMessages(IQueryable<PrivateMessage> query, int pageSize)
    {
        return query
            .OrderByDescending(message => message.MessageSeq)
            .ThenByDescending(message => message.MessageId)
            .Take(pageSize)
            .OrderBy(message => message.MessageSeq)
            .ThenBy(message => message.MessageId)
            .Select(message => MessageRecordFactory.FromQQNtPrivate(message))
            .ToList();
    }

    public static List<MessageRecord> LoadEarliestMessages(IQueryable<GroupMessage> query, int pageSize)
    {
        return query
            .OrderBy(message => message.MessageSeq)
            .ThenBy(message => message.MessageId)
            .Take(pageSize)
            .Select(message => MessageRecordFactory.FromQQNtGroup(message))
            .ToList();
    }

    public static List<MessageRecord> LoadEarliestMessages(IQueryable<PrivateMessage> query, int pageSize)
    {
        return query
            .OrderBy(message => message.MessageSeq)
            .ThenBy(message => message.MessageId)
            .Take(pageSize)
            .Select(message => MessageRecordFactory.FromQQNtPrivate(message))
            .ToList();
    }

    public static List<MessageRecord> LoadOlderMessages(
        IQueryable<GroupMessage> query,
        long messageSeq,
        long messageId,
        int pageSize)
    {
        return query
            .Where(message => message.MessageSeq < messageSeq ||
                              message.MessageSeq == messageSeq && message.MessageId < messageId)
            .OrderByDescending(message => message.MessageSeq)
            .ThenByDescending(message => message.MessageId)
            .Take(pageSize)
            .Select(message => MessageRecordFactory.FromQQNtGroup(message))
            .ToList();
    }

    public static List<MessageRecord> LoadOlderMessages(
        IQueryable<PrivateMessage> query,
        long messageSeq,
        long messageId,
        int pageSize)
    {
        return query
            .Where(message => message.MessageSeq < messageSeq ||
                              message.MessageSeq == messageSeq && message.MessageId < messageId)
            .OrderByDescending(message => message.MessageSeq)
            .ThenByDescending(message => message.MessageId)
            .Take(pageSize)
            .Select(message => MessageRecordFactory.FromQQNtPrivate(message))
            .ToList();
    }

    public static List<MessageRecord> LoadNewerMessages(
        IQueryable<GroupMessage> query,
        long messageSeq,
        long messageId,
        int pageSize)
    {
        return query
            .Where(message => message.MessageSeq > messageSeq ||
                              message.MessageSeq == messageSeq && message.MessageId > messageId)
            .OrderBy(message => message.MessageSeq)
            .ThenBy(message => message.MessageId)
            .Take(pageSize)
            .Select(message => MessageRecordFactory.FromQQNtGroup(message))
            .ToList();
    }

    public static List<MessageRecord> LoadNewerMessages(
        IQueryable<PrivateMessage> query,
        long messageSeq,
        long messageId,
        int pageSize)
    {
        return query
            .Where(message => message.MessageSeq > messageSeq ||
                              message.MessageSeq == messageSeq && message.MessageId > messageId)
            .OrderBy(message => message.MessageSeq)
            .ThenBy(message => message.MessageId)
            .Take(pageSize)
            .Select(message => MessageRecordFactory.FromQQNtPrivate(message))
            .ToList();
    }

    public static List<MessageRecord> LoadNewerOrEqualMessages(
        IQueryable<GroupMessage> query,
        long messageSeq,
        long messageId,
        int pageSize)
    {
        return query
            .Where(message => message.MessageSeq > messageSeq ||
                              message.MessageSeq == messageSeq && message.MessageId >= messageId)
            .OrderBy(message => message.MessageSeq)
            .ThenBy(message => message.MessageId)
            .Take(pageSize)
            .Select(message => MessageRecordFactory.FromQQNtGroup(message))
            .ToList();
    }

    public static List<MessageRecord> LoadNewerOrEqualMessages(
        IQueryable<PrivateMessage> query,
        long messageSeq,
        long messageId,
        int pageSize)
    {
        return query
            .Where(message => message.MessageSeq > messageSeq ||
                              message.MessageSeq == messageSeq && message.MessageId >= messageId)
            .OrderBy(message => message.MessageSeq)
            .ThenBy(message => message.MessageId)
            .Take(pageSize)
            .Select(message => MessageRecordFactory.FromQQNtPrivate(message))
            .ToList();
    }

    public static MessageRecord? LoadMessage(IQueryable<GroupMessage> query, long messageId)
    {
        return query
            .Where(message => message.MessageId == messageId)
            .Select(message => MessageRecordFactory.FromQQNtGroup(message))
            .FirstOrDefault();
    }

    public static MessageRecord? LoadMessage(IQueryable<PrivateMessage> query, long messageId)
    {
        return query
            .Where(message => message.MessageId == messageId)
            .Select(message => MessageRecordFactory.FromQQNtPrivate(message))
            .FirstOrDefault();
    }
}
