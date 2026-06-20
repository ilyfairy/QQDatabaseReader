using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QQDatabaseReader;

namespace QQDatabaseExplorer.Services;

internal static class IcalinguaMessageMerger
{
    public static IReadOnlyList<IcalinguaMessageRecord> MergeLatest(
        IEnumerable<IcalinguaMessageRecord> messages,
        int pageSize)
    {
        return Deduplicate(messages
                .OrderByDescending(static message => message.MessageSeq)
                .ThenByDescending(static message => message.MessageId))
            .Take(Math.Max(1, pageSize))
            .OrderBy(static message => message.MessageSeq)
            .ThenBy(static message => message.MessageId)
            .ToArray();
    }

    public static IReadOnlyList<IcalinguaMessageRecord> MergeOlder(
        IEnumerable<IcalinguaMessageRecord> messages,
        int pageSize)
    {
        return Deduplicate(messages
                .OrderByDescending(static message => message.MessageSeq)
                .ThenByDescending(static message => message.MessageId))
            .Take(Math.Max(1, pageSize))
            .OrderBy(static message => message.MessageSeq)
            .ThenBy(static message => message.MessageId)
            .ToArray();
    }

    public static IReadOnlyList<IcalinguaMessageRecord> Merge(
        IEnumerable<IcalinguaMessageRecord> messages,
        bool descending,
        int pageSize)
    {
        var deduplicated = Deduplicate(messages);
        var ordered = descending
            ? deduplicated
                .OrderByDescending(static message => message.MessageSeq)
                .ThenByDescending(static message => message.MessageId)
            : deduplicated
                .OrderBy(static message => message.MessageSeq)
                .ThenBy(static message => message.MessageId);

        return ordered
            .Take(Math.Max(1, pageSize))
            .ToArray();
    }

    public static bool IsBeforeCursor(
        IcalinguaMessageRecord message,
        long cursorSortTime,
        long cursorMessageId)
    {
        return message.MessageSeq < cursorSortTime ||
               message.MessageSeq == cursorSortTime && message.MessageId < cursorMessageId;
    }

    public static bool IsAfterCursor(
        IcalinguaMessageRecord message,
        long cursorSortTime,
        long cursorMessageId)
    {
        return message.MessageSeq > cursorSortTime ||
               message.MessageSeq == cursorSortTime && message.MessageId > cursorMessageId;
    }

    private static IEnumerable<IcalinguaMessageRecord> Deduplicate(IEnumerable<IcalinguaMessageRecord> messages)
    {
        var seenRawIds = new HashSet<string>(StringComparer.Ordinal);
        var seenRows = new HashSet<(long RoomId, int MessageTime, uint SenderId, string Content)>();

        foreach (var message in messages)
        {
            if (!string.IsNullOrWhiteSpace(message.RawId) && !IsSyntheticRawId(message.RawId))
            {
                var rawIdKey = message.RoomId.ToString(CultureInfo.InvariantCulture) + ":" + message.RawId;
                if (!seenRawIds.Add(rawIdKey))
                    continue;

                yield return message;
                continue;
            }

            var rowKey = (message.RoomId, message.MessageTime, message.SenderId, message.Content);
            if (!seenRows.Add(rowKey))
                continue;

            yield return message;
        }
    }

    private static bool IsSyntheticRawId(string rawId)
    {
        return rawId.Contains(':', StringComparison.Ordinal);
    }
}
