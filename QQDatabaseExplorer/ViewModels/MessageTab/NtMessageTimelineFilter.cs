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

        if (conversation.ConversationType is AvaConversationType.Group or AvaConversationType.PCQQGroup &&
            filter.SenderIds.Count > 0)
        {
            query = query.Where(message => filter.SenderIds.Contains(message.SenderId));
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
