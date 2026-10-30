using System.Data;

namespace QQDatabaseReader;

internal sealed record IcalinguaDateRow(
    string RawId,
    int MessageTime,
    uint SenderId,
    string Content);

internal sealed record IcalinguaMessageSource(
    string TableName,
    long RoomId,
    bool HasRoomIdColumn,
    HashSet<string> Columns,
    int SourceIndex);

internal sealed record IcalinguaMessageSearchSource(
    IcalinguaMessageSource Source,
    IReadOnlySet<long>? AllowedRoomIds);

internal sealed record IcalinguaMessageSearchScanRow(
    long SourceRowId,
    IcalinguaMessageRecord Message);

internal sealed class IcalinguaMessageSearchSourceState(
    IcalinguaMessageSearchSource searchSource,
    IcalinguaMessageSearchSourceCursor cursor)
{
    public IcalinguaMessageSearchSource SearchSource { get; } = searchSource;

    public long? BeforeRowId { get; set; } = cursor.BeforeRowId;

    public bool IsExhausted { get; set; } = cursor.IsExhausted;

    public IcalinguaMessageSearchScanRow? Candidate { get; set; }

    public long? PendingRowId { get; set; } = cursor.PendingRowId;

    public bool IsExhaustedAfterCandidate { get; set; }
}

internal sealed record IcalinguaRoomRow(
    long RoomId,
    string? RoomName,
    long UpdateTime,
    string? LastMessageJson,
    string? DownloadPath);

internal sealed record IcalinguaMessagePosition(
    IcalinguaMessagePositionKind Kind,
    long MessageSortTime,
    long MessageId);

internal enum IcalinguaMessagePositionKind
{
    Older,
    Newer,
}

internal sealed record IcalinguaFilterClause(string Predicate, Action<IDbCommand> Bind)
{
    public static IcalinguaFilterClause Empty { get; } = new(string.Empty, _ => { });
}
