using System.Collections.Generic;

namespace QQDatabaseExplorer.Models;

public sealed class AvaReplyMessage
{
    public long MessageId { get; init; }
    public long InternalMessageId { get; init; }
    public long MessageRandom { get; init; }
    public long MessageSeq { get; init; }
    public long AlternateMessageSeq { get; init; }
    public uint SenderId { get; init; }
    public string SenderName { get; init; } = string.Empty;
    public int MessageTime { get; init; }
    public uint SourceGroupId { get; init; }
    public string SourceGroupName { get; init; } = string.Empty;
    public bool HasSourceDescription => SourceGroupId != 0;
    public string SourceDescription => SourceGroupId == 0
        ? string.Empty
        : $"其它群聊：{(!string.IsNullOrWhiteSpace(SourceGroupName) ? SourceGroupName : SourceGroupId.ToString())}";
    public IReadOnlyList<AvaQQMessageSegment> Segments { get; init; } = [];
    public IReadOnlyList<AvaQQMessageSegment> PreviewSegments =>
        Controls.MessageInlineRenderer.CreateCompactPreviewSegments(Segments);
    public string PreviewText { get; init; } = string.Empty;
}
