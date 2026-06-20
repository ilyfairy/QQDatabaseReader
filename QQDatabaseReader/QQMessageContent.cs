using QQDatabaseReader.Database;

namespace QQDatabaseReader;

public class QQMessageContent
{
    public QQMessageSegment FirstSegment { get; set; }
    public IReadOnlyList<QQMessageSegment> Segments { get; set; }

    public QQMessageContent(IReadOnlyList<QQMessageSegment> segments)
    {
        Segments = segments;
        FirstSegment = segments[0];
    }

    public QQMessageContent(QQMessageSegment firstSegment)
    {
        FirstSegment = firstSegment;
        Segments = [firstSegment];
    }

    public string GetText()
    {
        return string.Concat(Segments.Select(v => v.GetDisplayText()));
    }

    public string GetText(MessageType messageType, SubMessageType subMessageType)
    {
        return QQMessageDisplayText.CreateText(this, messageType, subMessageType);
    }
}

public class QQForwardedMessage
{
    public long MessageId { get; set; }
    public long MessageSeq { get; set; }
    public ChatType ChatType { get; set; }
    public MessageType MessageType { get; set; }
    public SubMessageType SubMessageType { get; set; }
    public string SenderUid { get; set; } = string.Empty;
    public string PeerUid { get; set; } = string.Empty;
    public uint SavedGroupId { get; set; }
    public uint SenderId { get; set; }
    public int MessageTime { get; set; }
    public string? SendMemberName { get; set; }
    public string? SendNickName { get; set; }
    public string? AvatarUrl { get; set; }
    public IReadOnlyList<QQMessageSegment> Segments { get; set; } = [];
    public IReadOnlyList<QQForwardedMessage> NestedForwardedMessages { get; set; } = [];
}

public class QQReplyMessage
{
    public long ReplySegmentId { get; set; }
    public long MessageSeq { get; set; }
    public long MessageSeq2 { get; set; }
    public uint SenderId { get; set; }

    /// <summary>
    /// 47411，回复来源会话的数字 ID。它可能是群号，也可能是私聊对象 QQ 号，
    /// 不能脱离 47410.2.1 或 47412 直接当群号使用。
    /// </summary>
    public uint PeerId { get; set; }

    public int MessageTime { get; set; }
    public int Flag { get; set; }
    public long InternalMessageId { get; set; }
    public long MessageId { get; set; }
    public long MessageRandom { get; set; }
    public string? PreviewText { get; set; }

    internal const int GroupTargetConversationType = 82;

    /// <summary>
    /// 47410.2.1，被回复消息所在会话类型。已知 82 表示群聊来源，
    /// 9 + 47410.2.2/47410.2.3 为 11 表示私聊来源。
    /// </summary>
    public int? TargetConversationType { get; set; }

    /// <summary>
    /// 47410.2.2，被回复消息会话类型的子类型。私聊回复样本中通常为 11。
    /// </summary>
    public int? TargetConversationSubType1 { get; set; }

    /// <summary>
    /// 47410.2.3，被回复消息会话类型的子类型。私聊回复样本中通常为 11。
    /// </summary>
    public int? TargetConversationSubType2 { get; set; }

    /// <summary>
    /// 47410 里的原消息发送者昵称。跨群回复时，当前群的成员名缓存里通常找不到这个人。
    /// </summary>
    public string? SourceSenderName { get; set; }

    /// <summary>
    /// 根据 47410.2.1/47412 判定出的原消息所在群号。不要直接从 47411 赋值，
    /// 因为 QQ 号和群号可能相同，单看数字无法区分会话类型。
    /// </summary>
    public uint SourceGroupId { get; set; }

    /// <summary>
    /// 47412，跨聊天回复时表示原消息所在群名。
    /// </summary>
    public string? SourceGroupName { get; set; }

    public IReadOnlyList<QQMessageSegment> Segments { get; set; } = [];
}

public sealed class QQSystemHintMessage
{
    private readonly Dictionary<string, string> _properties = new(StringComparer.Ordinal);

    public List<QQSystemHintParticipant> Participants { get; } = [];
    public IReadOnlyDictionary<string, string> Properties => _properties;
    public string? Xml { get; set; }
    public string? Json { get; set; }

    public string? Action => GetProperty("action_str");
    public string? Suffix => GetProperty("suffix_str");
    public string? ActionImageUrl => GetProperty("action_img_url");
    public string? DisplayText => GetProperty("display_text");
    public bool IsSingleActor => string.Equals(GetProperty("single_actor"), "1", StringComparison.Ordinal);
    public string? SourceName => GetProperty("source_name");
    public bool SourceIsUser => !string.Equals(GetProperty("source_is_user"), "0", StringComparison.Ordinal);
    public string? TargetName => GetProperty("target_name");
    public long TargetMessageSeq => long.TryParse(GetProperty("msg_seq"), out var value) ? value : 0;
    public int FaceId => int.TryParse(GetProperty("face_id"), out var value) ? value : 0;
    public string? RecalledMessageText => GetProperty("recalled_text");
    public byte[]? RecalledOriginalMessageContent { get; set; }

    public void SetProperty(string name, string value)
    {
        _properties[name] = value;
    }

    public string? GetProperty(string name)
    {
        return _properties.TryGetValue(name, out var value) ? value : null;
    }
}

public sealed record QQSystemHintParticipant(string Uid, string Nickname);

public class QQMessageSegment
{
    /// <summary>
    /// 45001
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// 45002
    /// </summary>
    public MessageSegmentType Type { get; set; }

    public string? SenderUid { get; set; }

    public string? PeerUid { get; set; }

    /// <summary>
    /// 45101
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// 45102. Text element subtype. 2 means an @ mention in QQNT group messages.
    /// </summary>
    public int? TextElementType { get; set; }

    /// <summary>
    /// 45105. Mention target UID for QQNT @ text segments.
    /// </summary>
    public string? MentionUid { get; set; }

    /// <summary>
    /// 45402
    /// </summary>
    public string? ImageFileName { get; set; }

    /// <summary>
    /// 45422. 视频封面文件名。
    /// </summary>
    public string? VideoCoverFileName { get; set; }

    /// <summary>
    /// 45406
    /// </summary>
    public byte[]? ImageMd5 { get; set; }

    /// <summary>
    /// 45405. 语音消息里是本地 Ptt 文件大小。
    /// </summary>
    public long? MediaFileSize { get; set; }

    /// <summary>
    /// 45503
    /// </summary>
    public string? ImageRKey { get; set; }

    /// <summary>
    /// 45802
    /// </summary>
    public string? ImageThumbnailPath { get; set; }

    /// <summary>
    /// 45803
    /// </summary>
    public string? ImageLargePath { get; set; }

    /// <summary>
    /// 45804
    /// </summary>
    public string? ImageOriginalPath { get; set; }

    /// <summary>
    /// 45812
    /// </summary>
    public string? ImageLocalPath { get; set; }

    /// <summary>
    /// 45816
    /// </summary>
    public string? ImageHost { get; set; }

    /// <summary>
    /// 45815
    /// </summary>
    public string? AltText { get; set; }

    /// <summary>
    /// 45411
    /// </summary>
    public int? ImageWidth { get; set; }

    /// <summary>
    /// 45412
    /// </summary>
    public int? ImageHeight { get; set; }

    /// <summary>
    /// 45415. 视频时长，单位是毫秒。
    /// </summary>
    public int? VideoDurationMilliseconds { get; set; }

    /// <summary>
    /// 45003
    /// </summary>
    public int? FaceType { get; set; }

    /// <summary>
    /// 47601
    /// </summary>
    public int? FaceId { get; set; }

    /// <summary>
    /// 80810，QQNT 商城表情包 ID。本地文件位于
    /// nt_data/Emoji/marketface/&lt;表情包 ID&gt;/。
    /// </summary>
    public int? MarketFacePackageId { get; set; }

    /// <summary>
    /// 80900，商城表情显示名称，通常是“[吃饭]”这种文本。
    /// </summary>
    public string? MarketFaceName { get; set; }

    /// <summary>
    /// 80903，商城表情图片 ID 的 16 字节原始值。这里必须保留原始字节，
    /// 因为无 schema 的 protobuf 解码器容易把同一段字节误判成嵌套字段。
    /// 查找本地文件时再把它转成十六进制字符串。
    /// </summary>
    public byte[]? MarketFaceImageIdBytes { get; set; }

    /// <summary>
    /// 80909，QQNT 声明的商城表情显示宽度。
    /// </summary>
    public int? MarketFaceWidth { get; set; }

    /// <summary>
    /// 80910，QQNT 声明的商城表情显示高度。
    /// </summary>
    public int? MarketFaceHeight { get; set; }

    /// <summary>
    /// 47901, JSON payload for app/multimsg cards.
    /// </summary>
    public string? AppJson { get; set; }

    /// <summary>
    /// 47902
    /// </summary>
    public string? AppResid { get; set; }

    /// <summary>
    /// 47904
    /// </summary>
    public string? AppUniseq { get; set; }

    /// <summary>
    /// 48601
    /// </summary>
    public string? XmlResid { get; set; }

    /// <summary>
    /// 48602, XML payload for app/multimsg cards.
    /// </summary>
    public string? Xml { get; set; }

    /// <summary>
    /// 48603
    /// </summary>
    public string? XmlFileName { get; set; }

    public string? ParseError { get; set; }

    public QQReplyMessage? Reply { get; set; }

    public QQSystemHintMessage? SystemHint { get; set; }

    public string? MarketFaceImageId => MarketFaceImageIdBytes is { Length: > 0 }
        ? Convert.ToHexString(MarketFaceImageIdBytes).ToLowerInvariant()
        : null;

    public bool IsQQFace => Type == MessageSegmentType.Emoji && FaceId is not null;
    public bool IsMarketFace => Type == MessageSegmentType.MarketFace;
    public bool IsImage => Type is MessageSegmentType.Image;
    public bool IsVoice => Type == MessageSegmentType.Record;
    public bool IsVideo => Type == MessageSegmentType.Video;
    public bool IsMention => Type == MessageSegmentType.Text &&
                             TextElementType == 2 &&
                             !string.IsNullOrWhiteSpace(Text) &&
                             Text.StartsWith('@');
    public string? VoiceFileName => IsVoice ? ImageFileName : null;
    public byte[]? VoiceMd5 => IsVoice ? ImageMd5 : null;
    public string? VideoFileName => IsVideo ? ImageFileName : null;
    public byte[]? VideoMd5 => IsVideo ? ImageMd5 : null;

    public void AppendAltText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        if (string.IsNullOrEmpty(AltText))
        {
            AltText = text;
            return;
        }

        if (!AltText.Contains(text, StringComparison.Ordinal))
        {
            AltText += text;
        }
    }

    public string GetDisplayText()
    {
        if (!string.IsNullOrEmpty(Text))
            return Text;

        if (!string.IsNullOrEmpty(AltText))
            return AltText;

        if (FaceId is { } faceId)
            return $"[QQ表情:{faceId}]";

        if (!string.IsNullOrWhiteSpace(MarketFaceName))
            return MarketFaceName;

        if (SystemHint is { } systemHint && TryCreateSystemHintDisplayText(systemHint, out var systemHintText))
            return systemHintText;

        return Type switch
        {
            MessageSegmentType.Image => "[图片]",
            MessageSegmentType.File => "[文件]",
            MessageSegmentType.Record => "[语音]",
            MessageSegmentType.Video => "[视频]",
            MessageSegmentType.Reply => "[回复]",
            MessageSegmentType.System => "[系统消息]",
            MessageSegmentType.App => "[应用消息]",
            MessageSegmentType.RichMedia => "[应用卡片]",
            MessageSegmentType.MarketFace => "[商城表情]",
            MessageSegmentType.Xml => "[XML消息]",
            MessageSegmentType.Call => "[通话]",
            MessageSegmentType.Dynamic => "[动态消息]",
            _ => string.Empty,
        };
    }

    private static bool TryCreateSystemHintDisplayText(QQSystemHintMessage systemHint, out string text)
    {
        text = string.Empty;
        if (!string.IsNullOrWhiteSpace(systemHint.DisplayText))
        {
            text = systemHint.DisplayText;
            return true;
        }

        if (string.IsNullOrWhiteSpace(systemHint.Action))
            return false;

        var sourceName = FirstNonEmptyDisplayValue(
            systemHint.SourceName,
            systemHint.Participants.FirstOrDefault()?.Nickname);
        var targetName = systemHint.Participants.Count >= 2
            ? systemHint.Participants[1].Nickname
            : string.Empty;
        if (string.IsNullOrWhiteSpace(targetName))
            targetName = systemHint.TargetName ?? string.Empty;

        if (string.IsNullOrWhiteSpace(sourceName))
            return false;

        text = $"{sourceName}{systemHint.Action}{targetName}{systemHint.Suffix}";
        return true;
    }

    private static string FirstNonEmptyDisplayValue(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}

public enum MessageSegmentType : int
{
    Text = 1,
    Image = 2,
    File = 3,
    Record = 4,
    Video = 5,
    Emoji = 6,
    Reply = 7,
    System = 8,
    App = 9,
    RichMedia = 10,
    MarketFace = 11,
    Xml = 16,
    Call = 21,
    Dynamic = 26,
}
