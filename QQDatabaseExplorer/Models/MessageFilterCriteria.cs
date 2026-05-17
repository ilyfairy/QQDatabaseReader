using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.Models;

public sealed record MessageFilterCriteria(
    int? StartTime,
    int? EndTimeExclusive,
    IReadOnlyList<int> SelectedDayStartTimes,
    IReadOnlyList<uint> SenderIds)
{
    public static MessageFilterCriteria Empty { get; } = new(null, null, Array.Empty<int>(), Array.Empty<uint>());

    public bool IsEmpty =>
        StartTime is null &&
        EndTimeExclusive is null &&
        SelectedDayStartTimes.Count == 0 &&
        SenderIds.Count == 0;

    public static MessageFilterCriteria Create(
        int? startTime,
        int? endTimeExclusive,
        IEnumerable<int>? selectedDayStartTimes,
        IEnumerable<uint>? senderIds)
    {
        var normalizedDayStartTimes = selectedDayStartTimes?
            .Where(dayStartTime => dayStartTime > 0)
            .Distinct()
            .OrderBy(dayStartTime => dayStartTime)
            .ToArray() ?? [];
        var normalizedSenderIds = senderIds?
            .Where(senderId => senderId != 0)
            .Distinct()
            .OrderBy(senderId => senderId)
            .ToArray() ?? [];

        return new MessageFilterCriteria(startTime, endTimeExclusive, normalizedDayStartTimes, normalizedSenderIds);
    }

    public static MessageFilterCriteria CreateForSelectedDays(
        IEnumerable<int>? selectedDayStartTimes,
        IEnumerable<uint>? senderIds)
    {
        return Create(null, null, selectedDayStartTimes, senderIds);
    }

    public MessageQueryFilter ToQueryFilter()
    {
        return IsEmpty
            ? MessageQueryFilter.Empty
            : new MessageQueryFilter(StartTime, EndTimeExclusive, SelectedDayStartTimes, SenderIds);
    }
}

public sealed record MessageDateFilterOption(
    int DayStartTime,
    int MessageCount);

public sealed record MessageDateFilterCell(
    int? DayStartTime,
    int Day,
    bool IsCurrentMonth,
    bool HasMessages,
    bool IsSelected,
    int MessageCount)
{
    public bool IsEmpty => DayStartTime is null;

    public bool CanSelect => DayStartTime is not null && IsCurrentMonth && HasMessages;

    public bool IsDisabled => !CanSelect && !IsEmpty;

    public string DayText => Day <= 0 ? string.Empty : Day.ToString();

    public string MessageCountText => MessageCount > 0 ? MessageCount.ToString() : string.Empty;
}

public sealed record MessageSenderFilterOption(
    uint SenderId,
    string DisplayName,
    string? NtUid = null)
{
    public string DisplayText => SenderId == 0
        ? DisplayName
        : string.IsNullOrWhiteSpace(DisplayName) || DisplayName == SenderId.ToString()
            ? SenderId.ToString()
            : $"{DisplayName} ({SenderId})";

    public string? AvatarUrl => SenderId == 0
        ? null
        : $"http://q1.qlogo.cn/g?b=qq&nk={SenderId}&s=100";
}

public sealed record MessageFilterDialogRequest(
    AvaConversationType ConversationType,
    MessageFilterCriteria CurrentFilter,
    IReadOnlyList<MessageDateFilterOption> DateOptions,
    IReadOnlyList<MessageSenderFilterOption> SenderCandidates)
{
    public bool IsGroupConversation =>
        ConversationType is AvaConversationType.Group or AvaConversationType.PCQQGroup;
}
