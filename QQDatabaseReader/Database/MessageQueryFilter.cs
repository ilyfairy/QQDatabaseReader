namespace QQDatabaseReader.Database;

public sealed record MessageQueryFilter(
    int? StartTime,
    int? EndTimeExclusive,
    IReadOnlyList<int> SelectedDayStartTimes,
    IReadOnlyList<uint> SenderIds)
{
    public static MessageQueryFilter Empty { get; } = new(null, null, Array.Empty<int>(), Array.Empty<uint>());

    public bool IsEmpty =>
        StartTime is null &&
        EndTimeExclusive is null &&
        SelectedDayStartTimes.Count == 0 &&
        SenderIds.Count == 0;
}
