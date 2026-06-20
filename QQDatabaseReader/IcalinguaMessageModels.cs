namespace QQDatabaseReader;

public sealed record IcalinguaConversation(
    long RoomId,
    string DisplayName,
    long LatestMessageTime,
    string LatestMessageText,
    bool IsGroup,
    string? DownloadPath);

public sealed record IcalinguaMessageRecord(
    string RawId,
    long MessageId,
    long StableId,
    long RoomId,
    uint SenderId,
    string Username,
    string Content,
    string? FileJson,
    string? FilesJson,
    int MessageTime,
    long MessageSortTime,
    bool Deleted,
    bool Hide,
    bool Reveal,
    bool Flash,
    bool System,
    string? ReplyMessageJson,
    string? MiraiJson,
    string? Title,
    string? RecallInfo,
    string? Code,
    string? Role,
    string? AnonymousId,
    string? AnonymousFlag,
    string? BubbleId,
    string? SubId,
    string? HeadImage,
    string PreviewText)
{
    public long MessageSeq => MessageSortTime;
}

public sealed record IcalinguaMessageFile(
    string? Type,
    string? Url,
    string? Name,
    string? Fid,
    bool IsFace,
    int? Width,
    int? Height,
    long? Size);

public sealed record IcalinguaMessageSearchCursor(long MessageSortTime, long MessageId);

public sealed record IcalinguaMessageSearchGroupCount(long RoomId, long MatchCount, string DisplayName);

public sealed record IcalinguaReplyPreview(
    string RawId,
    uint SenderId,
    string? SenderName,
    int MessageTime,
    long MessageSortTime,
    string PreviewText,
    IReadOnlyList<IcalinguaMessageFile> Files);

public sealed record IcalinguaSender(uint SenderId, string DisplayName);

public sealed record IcalinguaMessageDate(int DayStartTime, int MessageCount);
