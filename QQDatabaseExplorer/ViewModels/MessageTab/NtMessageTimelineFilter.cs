using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal static class NtMessageTimelineFilter
{
    public static IQueryable<GroupMessage> Apply(
        IQueryable<GroupMessage> query,
        AvaQQGroup conversation,
        MessageFilterCriteria filter)
    {
        if (filter.StartTime is { } startTime)
            query = query.Where(message => message.MessageTime >= startTime);
        if (filter.EndTimeExclusive is { } endTime)
            query = query.Where(message => message.MessageTime < endTime);
        if (filter.SelectedDayStartTimes.Count > 0)
            query = query.Where(message => filter.SelectedDayStartTimes.Contains(message.DayTimestamp));

        if ((conversation.ConversationType is AvaConversationType.Group or AvaConversationType.PCQQGroup) &&
            filter.SenderIds.Count > 0)
        {
            // SQLite stores QQ numbers as signed INTEGER values.  EF Core's
            // uint parameter conversion narrows values above Int32.MaxValue to
            // a negative literal (for example 2306819382 becomes -1988147914),
            // so a normal uint Contains() silently returns no rows.  Cast the
            // column to Int64 and compare against Int64 values to preserve the
            // full UInt32 range while keeping the conversation predicate
            // indexable.
            var senderIds = filter.SenderIds
                .Select(static senderId => (long)senderId)
                .ToArray();
            query = query.Where(message => senderIds.Contains((long)message.SenderId));
        }

        return query;
    }

    public static IQueryable<PrivateMessage> Apply(
        IQueryable<PrivateMessage> query,
        MessageFilterCriteria filter)
    {
        if (filter.StartTime is { } startTime)
            query = query.Where(message => message.MessageTime >= startTime);
        if (filter.EndTimeExclusive is { } endTime)
            query = query.Where(message => message.MessageTime < endTime);
        if (filter.SelectedDayStartTimes.Count > 0)
            query = query.Where(message => filter.SelectedDayStartTimes.Contains(message.DayTimestamp));

        return query;
    }
}
