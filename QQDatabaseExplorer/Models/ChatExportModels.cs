using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace QQDatabaseExplorer.Models;

public sealed record ChatExportDocument(
    string SchemaVersion,
    ChatExportMetadata Metadata,
    ChatExportConversation Conversation,
    IReadOnlyList<ChatExportParticipant> Participants,
    IReadOnlyList<ChatExportMessage>? Messages,
    IReadOnlyList<ChatExportMessageChunk>? MessageChunks = null,
    IReadOnlyList<ChatExportTimelineDate>? TimelineDates = null,
    IReadOnlyList<ChatExportMessageIndex>? MessageIndex = null);

public sealed record ChatExportMessageChunk(
    int Index,
    int Start,
    int Count,
    string Path);

public sealed record ChatExportTimelineDate(
    int RowIndex,
    int MessageIndex,
    string Label);

public sealed record ChatExportMessageIndex(
    int Index,
    string Key,
    long MessageId,
    long MessageRandom,
    long MessageSeq);

public sealed record ChatExportMetadata(
    DateTimeOffset ExportedAt,
    string Exporter,
    string Viewer,
    int MessageCount);

public sealed record ChatExportOptions(
    ChatExportFormat Format,
    ChatExportContentOptions Content)
{
    public static ChatExportOptions Default { get; } = new(
        ChatExportFormat.Html,
        ChatExportContentOptions.All);
}

public sealed record ChatExportContentOptions(
    bool IncludeAvatars,
    bool IncludeImages,
    bool IncludeVoice,
    bool IncludeVideos,
    bool IncludeFiles,
    bool IncludeFaceAssets)
{
    public static ChatExportContentOptions All { get; } = new(
        IncludeAvatars: true,
        IncludeImages: true,
        IncludeVoice: true,
        IncludeVideos: true,
        IncludeFiles: true,
        IncludeFaceAssets: true);

    public static ChatExportContentOptions None { get; } = new(
        IncludeAvatars: false,
        IncludeImages: false,
        IncludeVoice: false,
        IncludeVideos: false,
        IncludeFiles: false,
        IncludeFaceAssets: false);
}

public enum ChatExportFormat
{
    Html,
    Mhtml,
    Json,
}

public sealed record ChatExportProgress(
    string Stage,
    string Detail,
    int Current,
    int Total);

public sealed record ChatExportConversation(
    string Key,
    AvaConversationType Type,
    string Title,
    string? AvatarUrl,
    string? AvatarPath,
    string LogicalType,
    string LogicalId,
    IReadOnlyList<ChatExportConversationSource> Sources,
    uint GroupId,
    long PrivateConversationId,
    uint PrivateUin,
    string? PrivateUid,
    string? AndroidMobileQQPeerUin,
    long IcalinguaRoomId);

public sealed record ChatExportConversationSource(
    string Key,
    AvaConversationType Type,
    string Title,
    uint GroupId,
    long PrivateConversationId,
    uint PrivateUin,
    string? PrivateUid,
    string? AndroidMobileQQPeerUin,
    long IcalinguaRoomId);

public sealed record ChatExportParticipant(
    string Key,
    uint Uin,
    string? Uid,
    string DisplayName,
    string? AvatarUrl,
    string? AvatarPath);

public sealed record ChatExportMessage(
    string Key,
    long MessageId,
    long MessageRandom,
    long MessageSeq,
    long PCQQMessageSeq,
    int MessageTime,
    string LocalTime,
    ChatExportParticipantRef Sender,
    bool IsSystemHint,
    bool IsRecalled,
    string DisplayText,
    ChatExportReply? Reply,
    IReadOnlyList<ChatExportSegment> Segments,
    IReadOnlyList<ChatExportReaction> Reactions,
    IReadOnlyList<ChatExportMessage> ForwardedMessages,
    ChatExportSystemHint? SystemHint,
    ChatExportRawMessage Raw);

public sealed record ChatExportParticipantRef(
    string Key,
    uint Uin,
    string? Uid,
    string DisplayName,
    string? AvatarUrl,
    string? AvatarPath);

public sealed record ChatExportReply(
    long MessageId,
    long InternalMessageId,
    long MessageRandom,
    string RawMessageId,
    long MessageSeq,
    long AlternateMessageSeq,
    uint SenderId,
    string SenderName,
    int MessageTime,
    uint SourceGroupId,
    string SourceGroupName,
    string PreviewText,
    IReadOnlyList<ChatExportSegment> Segments);

public sealed record ChatExportReaction(
    string FaceId,
    int Count,
    string DisplayText,
    string? FaceAssetPath);

public sealed record ChatExportSystemHint(
    string SourceName,
    string SourceUin,
    bool SourceIsUser,
    string TargetName,
    string TargetUin,
    bool TargetIsUser,
    string Action,
    string Suffix,
    string? ActionImageUrl,
    long TargetMessageSeq,
    int FaceId,
    string? FaceAssetPath);

public sealed record ChatExportSegment(
    AvaQQMessageSegmentType Type,
    AvaQQMessageSegmentTone Tone,
    string Text,
    string DisplayText,
    string? LinkUrl,
    bool IsMention,
    string? MentionUid,
    int? FaceId,
    string? FaceName,
    string? FaceAssetPath,
    ChatExportMedia? Media,
    ChatExportForwardedCard? ForwardedMessage,
    ChatExportSharedContactCard? SharedContact,
    ChatExportMiniAppCard? MiniApp);

public sealed record ChatExportMedia(
    ChatExportMediaKind Kind,
    bool IsAvailable,
    string? FileName,
    string? RelativePath,
    string? CoverRelativePath,
    int? Width,
    int? Height,
    int? MaxWidth,
    int? MaxHeight,
    int? DurationMilliseconds,
    long? FileSize,
    string DisplayText);

public enum ChatExportMediaKind
{
    Image,
    Voice,
    Video,
    File,
}

public sealed record ChatExportForwardedCard(
    string Title,
    string Footer,
    IReadOnlyList<string> PreviewLines,
    string? Resid,
    string? Uniseq,
    string? FileName,
    int? MessageCount,
    string RawPayload);

public sealed record ChatExportSharedContactCard(
    SharedContactCardKind Kind,
    string Title,
    string Subtitle,
    string Tag,
    string? AvatarUrl,
    string? JumpUrl,
    string RawPayload);

public sealed record ChatExportMiniAppCard(
    MiniAppCardKind Kind,
    string AppName,
    string Title,
    string? HostName,
    string? IconUrl,
    string? PreviewUrl,
    string? JumpUrl,
    string RawPayload);

public sealed record ChatExportRawMessage(
    AvaConversationType ConversationType,
    string ConversationKey,
    string? PayloadBase64,
    string? Source,
    int MessageType,
    int SubMessageType,
    int SendType,
    string SenderUid,
    string PeerUid,
    uint PeerUin,
    uint GroupId,
    long PrivateConversationId,
    long ReplyToMessageSeq,
    string? ContentBase64,
    string? SubContentBase64,
    string? MessageReactionsBase64);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ChatExportDocument))]
[JsonSerializable(typeof(IReadOnlyList<ChatExportMessage>))]
public sealed partial class ChatExportJsonContext : JsonSerializerContext;
