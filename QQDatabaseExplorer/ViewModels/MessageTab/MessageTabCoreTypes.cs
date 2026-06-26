using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

// MessageTab 内部统一使用 MessageRecord 屏蔽 QQNT、PCQQ、Icalingua 的底层消息差异。
internal sealed record MessageRecord(
    long MessageId,
    long MessageRandom,
    long MessageSeq,
    MessageType MessageType,
    SubMessageType SubMessageType,
    int SendType,
    string SenderUid,
    string PeerUid,
    uint GroupId,
    long PrivateConversationId,
    uint PeerUin,
    int MessageTime,
    string? SendMemberName,
    string? SendNickName,
    byte[]? Content,
    byte[]? MessageReactions,
    byte[]? SubContent,
    long ReplyToMessageSeq,
    uint SenderId);

internal static class MessageRecordFactory
{
    public static MessageRecord FromQQNtGroup(GroupMessage message)
    {
        return new MessageRecord(
            message.MessageId,
            message.MessageRandom,
            message.MessageSeq,
            message.MessageType,
            message.SubMessageType,
            message.SendType,
            message.SenderUid,
            message.PeerUid,
            message.GroupId,
            0,
            0,
            message.MessageTime,
            message.SendMemberName,
            message.SendNickName,
            message.Content,
            message.MessageReactions,
            message.SubContent,
            message.ReplyToMessageSeq,
            message.SenderId);
    }

    public static MessageRecord FromQQNtPrivate(PrivateMessage message)
    {
        return new MessageRecord(
            message.MessageId,
            message.MessageRandom,
            message.MessageSeq,
            message.MessageType,
            message.SubMessageType,
            message.SendType,
            message.SenderUid,
            message.PeerUid,
            0,
            message.ConversationId,
            message.PeerUin,
            message.MessageTime,
            message.SendMemberName,
            message.SendNickName,
            message.Content,
            message.MessageReactions,
            message.SubContent,
            message.ReplyToMessageSeq,
            message.SenderId);
    }

    public static MessageRecord FromPCQQ(PCQQMessageRecord message, AvaQQGroup conversation)
    {
        return new MessageRecord(
            message.MessageRandom,
            message.MessageRandom,
            message.MessageTime,
            MessageType.Text,
            SubMessageType.Text,
            0,
            string.Empty,
            conversation.ConversationType == AvaConversationType.PCQQPrivate
                ? conversation.PrivateUin.ToString()
                : string.Empty,
            conversation.ConversationType == AvaConversationType.PCQQGroup ? conversation.GroupId : 0,
            conversation.ConversationType == AvaConversationType.PCQQPrivate ? conversation.PrivateUin : 0,
            conversation.ConversationType == AvaConversationType.PCQQPrivate ? conversation.PrivateUin : 0,
            MessageConversationTime.ClampUnixTime(message.MessageTime),
            null,
            message.SenderNickname,
            message.Content,
            null,
            message.Info,
            0,
            message.SenderUin);
    }

    public static MessageRecord FromIcalingua(IcalinguaMessageRecord message, AvaQQGroup conversation)
    {
        return new MessageRecord(
            message.MessageId,
            message.StableId,
            message.MessageSeq,
            MessageType.Text,
            SubMessageType.Text,
            0,
            message.SenderId == 0 ? string.Empty : message.SenderId.ToString(),
            string.Empty,
            0,
            conversation.IcalinguaRoomId,
            0,
            message.MessageTime,
            null,
            message.Username,
            IcalinguaMessagePayload.ToContentBytes(message),
            null,
            null,
            0,
            message.SenderId);
    }

    public static MessageRecord FromAndroidMobileQQ(AndroidMobileQQMessageRecord message, AvaQQGroup conversation)
    {
        var senderId = ParseUin(message.SenderUin);
        return new MessageRecord(
            message.RowId,
            message.RowId,
            message.MessageTime,
            MessageType.Text,
            SubMessageType.Text,
            message.IsSend,
            message.SenderUin,
            message.PeerUin,
            conversation.ConversationType == AvaConversationType.AndroidMobileQQGroup ? ParseUin(message.PeerUin) : 0,
            conversation.ConversationType == AvaConversationType.AndroidMobileQQPrivate ? message.RowId : 0,
            conversation.ConversationType == AvaConversationType.AndroidMobileQQPrivate ? ParseUin(message.PeerUin) : 0,
            MessageConversationTime.ClampUnixTime(message.MessageTime),
            message.SenderName,
            message.SenderName,
            AndroidMobileQQMessagePayload.ToContentBytes(message),
            null,
            null,
            0,
            senderId);
    }

    private static uint ParseUin(string? value)
    {
        return uint.TryParse(value, out var uin) ? uin : 0;
    }
}

internal static class MessageRecordRawDataFactory
{
    public static AvaRawMessageData CreateRawData(this MessageRecord record, string source)
    {
        return AvaRawMessageData.Create(
            source,
            (int)record.MessageType,
            (int)record.SubMessageType,
            record.SendType,
            record.SenderUid,
            record.PeerUid,
            record.PeerUin,
            record.GroupId,
            record.PrivateConversationId,
            record.ReplyToMessageSeq,
            record.Content,
            record.SubContent,
            record.MessageReactions);
    }
}

// 下面是消息显示、回复、撤回、媒体解析之间传递的小型数据结构。
internal readonly record struct MessageSenderInfo(
    uint SenderId,
    string SenderUid,
    string Name);

internal readonly record struct ReplyTargetConversation(
    AvaConversationType ConversationType,
    uint GroupId,
    long PrivateConversationId);

internal readonly record struct ReplyTargetKey(
    AvaConversationType ConversationType,
    uint GroupId,
    long PrivateConversationId,
    long MessageId,
    long InternalMessageId,
    long MessageRandom,
    long MessageSeq,
    long AlternateMessageSeq);

internal readonly record struct SystemHintDisplay(
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
    string? FaceAssetPath,
    string DisplayText);

internal readonly record struct RecalledOriginalMessage(
    QQForwardedMessage ForwardedMessage,
    List<AvaQQMessageSegment> Segments);

internal readonly record struct ThumbnailCandidate(
    string Path,
    int Spec,
    bool MatchesPreferredExtension);

internal readonly record struct ResolvedVideoMediaPath(
    string? VideoPath,
    string? CoverPath,
    bool IsVideoAvailable,
    bool IsCoverAvailable)
{
    public static ResolvedVideoMediaPath Missing { get; } = new(null, null, false, false);
}

internal sealed record LocalMediaContext(
    DatabasePlatformType PlatformType,
    string? NtDataPath,
    string? MobileQQPath,
    string? ChatPicPath);

internal sealed class LocalMediaContextFactory
{
    private readonly QQDatabaseService _databaseService;

    public LocalMediaContextFactory(QQDatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    public LocalMediaContext Create()
    {
        return _databaseService.AndroidMessageDatabase is not null &&
               _databaseService.MessageDatabase is null
            ? new LocalMediaContext(
                DatabasePlatformType.AndroidQQNT,
                null,
                _databaseService.AndroidMobileQQPath,
                _databaseService.AndroidQQNtChatPicPath)
            : new LocalMediaContext(
                DatabasePlatformType.QQNT,
                _databaseService.NtDataPath,
                null,
                null);
    }
}

internal sealed record ConversationLoadItem(
    AvaConversationType ConversationType,
    uint GroupId,
    long PrivateConversationId,
    uint PrivateUin,
    string? PrivateUid,
    string? DisplayName,
    string LatestMessageText,
    string? AvatarLocalPath,
    int LastTime);

internal sealed record GroupInfoLoadItem(uint GroupId, string? GroupName);

// 数据库增删后，MessageTab 需要按不同数据库类型清理不同缓存和选择状态。
internal readonly record struct ConversationDatabaseRemovalResult(
    bool Handled,
    bool ClearMessages,
    bool ClearMessageSelection,
    bool ClearSenderCache,
    bool ClearAvatarPathCaches,
    bool ClearGroupSelection,
    bool RefreshFilteredGroups);

internal readonly record struct ConversationDatabaseRemovalContext(
    bool HasGroupInfoDatabase,
    bool HasNtMessageDatabase);

internal static class ConversationDatabaseRemovalResults
{
    public static ConversationDatabaseRemovalResult ClearMessageDatabase { get; } =
        new(
            Handled: true,
            ClearMessages: true,
            ClearMessageSelection: true,
            ClearSenderCache: true,
            ClearAvatarPathCaches: true,
            ClearGroupSelection: true,
            RefreshFilteredGroups: true);
}

internal sealed record QqNtDisplaySenderContext(
    IReadOnlyDictionary<uint, string> SenderNames,
    IReadOnlyDictionary<long, MessageSenderInfo> MessageSenderInfos,
    string SenderName);

internal sealed record QqNtMainMessageDisplayContent(
    QQMessageContent? Content,
    List<AvaQQMessageSegment> Segments,
    RecalledOriginalMessage? RecalledMessage,
    string SenderName);

internal sealed record QqNtDisplayMessageAssembly(
    List<AvaQQMessageSegment> Segments,
    IReadOnlyList<AvaQQMessage> ForwardedMessages,
    AvaReplyMessage? Reply,
    IReadOnlyList<AvaMessageReaction> Reactions,
    RecalledOriginalMessage? RecalledMessage,
    SystemHintDisplay? SystemHint,
    string SenderName,
    string? CachedAvatarLocalPath);

internal sealed record AndroidMobileQQMessagePayload(
    string DisplayText,
    int MsgType,
    IReadOnlyList<AndroidMobileQQMessagePart> Parts)
{
    public static byte[] ToContentBytes(AndroidMobileQQMessageRecord message)
    {
        var payload = new AndroidMobileQQMessagePayload(
            message.PreviewText,
            message.MsgType,
            message.Content.Parts);
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    public static AndroidMobileQQMessagePayload? FromContent(byte[]? content)
    {
        if (content is null || content.Length == 0)
            return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<AndroidMobileQQMessagePayload>(content);
        }
        catch
        {
            var text = Encoding.UTF8.GetString(content);
            return string.IsNullOrWhiteSpace(text)
                ? null
                : new AndroidMobileQQMessagePayload(text, 0, []);
        }
    }
}

// MessageTab 内部的轻量工具和平台判断，保持在同一文件里避免小文件过多。
internal static class MessageFilterOptionText
{
    public static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }
}

internal static class MessageConversationTime
{
    public static int ClampUnixTime(long timestamp)
    {
        if (timestamp > int.MaxValue)
            return int.MaxValue;

        if (timestamp < int.MinValue)
            return int.MinValue;

        return (int)timestamp;
    }
}

internal static class ConversationTypeClassifier
{
    public static bool IsPCQQ(AvaQQGroup conversation)
    {
        return IsPCQQ(conversation.ConversationType);
    }

    public static bool IsPCQQ(AvaConversationType conversationType)
    {
        return conversationType is AvaConversationType.PCQQGroup or AvaConversationType.PCQQPrivate;
    }

    public static bool IsIcalingua(AvaQQGroup conversation)
    {
        return conversation.ConversationType == AvaConversationType.Icalingua;
    }

    public static bool IsAndroidMobileQQ(AvaQQGroup conversation)
    {
        return conversation.ConversationType is AvaConversationType.AndroidMobileQQGroup or AvaConversationType.AndroidMobileQQPrivate;
    }

    public static bool IsAndroidMobileQQ(AvaConversationType conversationType)
    {
        return conversationType is AvaConversationType.AndroidMobileQQGroup or AvaConversationType.AndroidMobileQQPrivate;
    }
}

internal static class ConversationCatalogValueHelpers
{
    public static bool TryParseRecentGroupId(string? peerUin, out uint groupId)
    {
        groupId = 0;
        return !string.IsNullOrWhiteSpace(peerUin) &&
               uint.TryParse(peerUin, out groupId) &&
               groupId != 0;
    }

    public static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }
}

internal static class QqNtMessageContentParser
{
    public static QQMessageContent? TryParse(byte[]? content)
    {
        if (content is null)
            return null;

        try
        {
            return QQMessageReader.ParseMessage(content);
        }
        catch
        {
            return null;
        }
    }
}

internal interface IMessageDatabaseSource
{
    QQMessageReader? MessageDatabase { get; }

    QQAndroidMessageReader? AndroidMessageDatabase { get; }

    PCQQMessageReader? PCQQMessageDatabase { get; }

    AndroidMobileQQMessageReader? AndroidMobileQQMessageDatabase { get; }

    IcalinguaMessageDatabaseSet? IcalinguaMessageDatabases { get; }

    bool HasNtMessageDatabase { get; }
}

internal sealed class QQDatabaseServiceMessageDatabaseSource(QQDatabaseService databaseService) : IMessageDatabaseSource
{
    public QQMessageReader? MessageDatabase => databaseService.MessageDatabase;

    public QQAndroidMessageReader? AndroidMessageDatabase => databaseService.AndroidMessageDatabase;

    public PCQQMessageReader? PCQQMessageDatabase => databaseService.PCQQMessageDatabase;

    public AndroidMobileQQMessageReader? AndroidMobileQQMessageDatabase => databaseService.AndroidMobileQQMessageDatabase;

    public IcalinguaMessageDatabaseSet? IcalinguaMessageDatabases => databaseService.IcalinguaMessageDatabases;

    public bool HasNtMessageDatabase => MessageDatabase is not null || AndroidMessageDatabase is not null;
}

internal sealed class MessageDatabaseAvailability
{
    private readonly QQDatabaseService _databaseService;

    public MessageDatabaseAvailability(QQDatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    public bool HasQQNtMessageDatabase =>
        _databaseService.MessageDatabase is not null ||
        _databaseService.AndroidMessageDatabase is not null;

    public bool HasMessageDatabase(AvaQQGroup conversation)
    {
        if (ConversationTypeClassifier.IsPCQQ(conversation))
            return _databaseService.PCQQMessageDatabase is not null;

        if (ConversationTypeClassifier.IsAndroidMobileQQ(conversation))
            return _databaseService.AndroidMobileQQMessageDatabase is not null;

        if (ConversationTypeClassifier.IsIcalingua(conversation))
            return _databaseService.IcalinguaMessageDatabases is not null;

        return HasQQNtMessageDatabase;
    }
}

internal static class MessageMediaDisplaySizes
{
    public const int FaceMaxDisplaySize = 200;

    public static int LimitFaceDisplaySize(int? value)
    {
        return value is > 0
            ? Math.Min(value.Value, FaceMaxDisplaySize)
            : FaceMaxDisplaySize;
    }
}

internal sealed class MessageFilterState
{
    private readonly Dictionary<string, MessageFilterCriteria> _filters = new(StringComparer.Ordinal);

    public MessageFilterCriteria Get(AvaQQGroup conversation)
    {
        return _filters.GetValueOrDefault(conversation.ConversationKey, MessageFilterCriteria.Empty);
    }

    public void Set(AvaQQGroup conversation, MessageFilterCriteria filter)
    {
        if (filter.IsEmpty)
            _filters.Remove(conversation.ConversationKey);
        else
            _filters[conversation.ConversationKey] = filter;
    }

    public void Clear(AvaQQGroup conversation)
    {
        _filters.Remove(conversation.ConversationKey);
    }

    public static string FormatSummary(MessageFilterCriteria filter)
    {
        if (filter.IsEmpty)
            return string.Empty;

        var parts = new List<string>();
        if (filter.SelectedDayStartTimes.Count > 0)
        {
            var dates = filter.SelectedDayStartTimes
                .Select(dayStartTime => DateTimeOffset.FromUnixTimeSeconds(dayStartTime).LocalDateTime.ToString("yyyy-MM-dd"))
                .ToArray();
            parts.Add(dates.Length <= 3
                ? string.Join("、", dates)
                : $"{dates[0]} 等 {dates.Length} 天");
        }
        else if (filter.StartTime is not null ||
            filter.EndTimeExclusive is not null)
        {
            var startText = filter.StartTime is null
                ? "最早"
                : DateTimeOffset.FromUnixTimeSeconds(filter.StartTime.Value).LocalDateTime.ToString("yyyy-MM-dd");
            var endText = filter.EndTimeExclusive is null
                ? "最新"
                : DateTimeOffset.FromUnixTimeSeconds(filter.EndTimeExclusive.Value).LocalDateTime.AddDays(-1).ToString("yyyy-MM-dd");
            parts.Add($"{startText} 至 {endText}");
        }

        if (filter.SenderIds.Count > 0)
            parts.Add($"发送人 {filter.SenderIds.Count} 个");

        return string.Join("，", parts);
    }
}

internal sealed class MessageSelectionState
{
    private readonly IEnumerable<AvaQQMessage> _messages;

    public MessageSelectionState(IEnumerable<AvaQQMessage> messages)
    {
        _messages = messages;
    }

    public IReadOnlyList<AvaQQMessage> SelectedMessages => _messages
        .Where(static message => message.CanSelect && message.IsSelected)
        .ToArray();

    public int SetSelectedMessages(IEnumerable<AvaQQMessage> selectedMessages)
    {
        var selectedMessageSet = selectedMessages
            .Where(static message => message.CanSelect)
            .ToHashSet(ReferenceEqualityComparer.Instance);

        var selectedCount = 0;
        foreach (var message in _messages)
        {
            var isSelected = message.CanSelect && selectedMessageSet.Contains(message);
            message.IsSelected = isSelected;
            if (isSelected)
                selectedCount++;
        }

        return selectedCount;
    }

    public int AddSelectedMessages(IEnumerable<AvaQQMessage> selectedMessages)
    {
        var selectedMessageSet = selectedMessages
            .Where(static message => message.CanSelect)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        if (selectedMessageSet.Count == 0)
            return CountSelectedMessages();

        var selectedCount = 0;
        foreach (var message in _messages)
        {
            if (selectedMessageSet.Contains(message))
                message.IsSelected = true;

            if (message.IsSelected)
                selectedCount++;
        }

        return selectedCount;
    }

    public int ToggleMessageSelection(AvaQQMessage message)
    {
        if (!message.CanSelect || !_messages.Contains(message, ReferenceEqualityComparer.Instance))
        {
            message.IsSelected = false;
            return CountSelectedMessages();
        }

        message.IsSelected = !message.IsSelected;
        return CountSelectedMessages();
    }

    public int ClearMessageSelection()
    {
        foreach (var message in _messages)
            message.IsSelected = false;

        return 0;
    }

    private int CountSelectedMessages()
    {
        return _messages.Count(static message => message.CanSelect && message.IsSelected);
    }
}

internal static class VoicePlaybackMessageStateUpdater
{
    public static void Update(IEnumerable<AvaQQMessage> messages, string? currentPlayingPath)
    {
        var normalizedCurrentPath = NormalizeLocalPath(currentPlayingPath);
        foreach (var message in messages)
        {
            foreach (var segment in message.Segments)
            {
                if (segment.Type != AvaQQMessageSegmentType.Voice)
                    continue;

                segment.IsVoicePlaying = segment.IsVoiceAvailable &&
                                         !string.IsNullOrWhiteSpace(normalizedCurrentPath) &&
                                         string.Equals(
                                             NormalizeLocalPath(segment.VoiceLocalPath),
                                             normalizedCurrentPath,
                                             StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static string? NormalizeLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
}

internal static class MessageReactionDisplayFactory
{
    public static IReadOnlyList<AvaMessageReaction> Create(byte[]? data)
    {
        var reactions = QQMessageReactionParser.Parse(data);
        if (reactions.Count == 0)
            return [];

        return reactions
            .Select(reaction =>
            {
                var (displayText, face) = ResolveReactionFace(reaction.FaceId);
                return new AvaMessageReaction
                {
                    FaceId = reaction.FaceId,
                    Count = reaction.Count,
                    DisplayText = displayText,
                    FaceAssetPath = face?.AssetPath,
                };
            })
            .ToArray();
    }

    private static (string DisplayText, QQFaceInfo? Face) ResolveReactionFace(string faceIdText)
    {
        if (!int.TryParse(faceIdText, out var faceId))
            return ($"[QQ表情:{faceIdText}]", null);

        if (TryCreateUnicodeEmoji(faceId, out var emojiText))
        {
            var unicodeFace = QQFaceCatalog.GetUnicodeEmoji(emojiText);
            return (emojiText, unicodeFace);
        }

        var qqFace = QQFaceCatalog.Get(faceId);
        return qqFace is { Name.Length: > 0 }
            ? ($"[{qqFace.Name}]", qqFace)
            : ($"[QQ表情:{faceIdText}]", null);
    }

    private static bool TryCreateUnicodeEmoji(int codePoint, out string emojiText)
    {
        emojiText = string.Empty;
        if (codePoint is < 0x1F000 or > 0x10FFFF)
            return false;

        try
        {
            emojiText = char.ConvertFromUtf32(codePoint);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal static class MessageMediaPathResolver
{
    public static string? ResolveLocalMediaPath(
        LocalMediaContext mediaContext,
        int messageTime,
        QQMessageSegment segment,
        SubMessageType subMessageType)
    {
        if (mediaContext.PlatformType is DatabasePlatformType.AndroidQQNT)
        {
            var androidPath = AndroidMobileQQMediaPathResolver.ResolveImagePath(
                mediaContext.MobileQQPath,
                mediaContext.ChatPicPath,
                segment);
            if (!string.IsNullOrWhiteSpace(androidPath))
                return androidPath;

            return QqNtLocalMediaPathResolver.ResolveExplicitLocalImagePath(segment.ImageLocalPath);
        }

        return QqNtLocalMediaPathResolver.ResolveImagePath(
            mediaContext.NtDataPath,
            messageTime,
            segment,
            subMessageType);
    }

    public static string? ResolveLocalVoicePath(
        LocalMediaContext mediaContext,
        int messageTime,
        QQMessageSegment segment)
    {
        if (mediaContext.PlatformType is DatabasePlatformType.AndroidQQNT)
            return null;

        return QqNtLocalMediaPathResolver.ResolveVoicePath(mediaContext.NtDataPath, messageTime, segment);
    }

    public static ResolvedVideoMediaPath ResolveLocalVideoMediaPath(
        LocalMediaContext mediaContext,
        int messageTime,
        QQMessageSegment segment)
    {
        if (mediaContext.PlatformType is DatabasePlatformType.AndroidQQNT ||
            string.IsNullOrWhiteSpace(mediaContext.NtDataPath))
        {
            return ResolvedVideoMediaPath.Missing;
        }

        return QqNtLocalMediaPathResolver.ResolveVideoPath(mediaContext.NtDataPath, messageTime, segment);
    }
}

internal static class MessageMediaSegmentClassifier
{
    public static bool MatchesMediaSegment(AvaQQMessageSegment displaySegment, QQMessageSegment parsedSegment)
    {
        if (parsedSegment.IsVoice)
            return displaySegment.Type == AvaQQMessageSegmentType.Voice;

        if (parsedSegment.IsVideo)
            return displaySegment.Type == AvaQQMessageSegmentType.Video;

        return displaySegment.Type == AvaQQMessageSegmentType.Image;
    }

    public static bool IsVoiceSegment(MessageType messageType, QQMessageSegment segment)
    {
        return segment.IsVoice || messageType == MessageType.Voice;
    }

    public static bool IsVideoSegment(MessageType messageType, QQMessageSegment segment)
    {
        return segment.IsVideo || messageType == MessageType.Video;
    }

    public static bool IsRenderableImageSegment(
        MessageType messageType,
        SubMessageType subMessageType,
        QQMessageSegment segment)
    {
        if (messageType is MessageType.GroupFile
            or MessageType.Video
            or MessageType.Voice
            or MessageType.System)
        {
            return false;
        }

        return segment.IsImage ||
               IsStickerMediaSegment(subMessageType, segment);
    }

    public static bool IsStickerMessage(SubMessageType subMessageType)
    {
        return subMessageType is SubMessageType.Sticker;
    }

    private static bool IsStickerMediaSegment(SubMessageType subMessageType, QQMessageSegment segment)
    {
        return IsStickerMessage(subMessageType) &&
               (!string.IsNullOrWhiteSpace(segment.ImageFileName) ||
                !string.IsNullOrWhiteSpace(segment.ImageLocalPath));
    }
}

internal static class MessageMediaFileNameCandidateFactory
{
    public static IEnumerable<string> CreateImageCandidates(QQMessageSegment segment)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fileName in CreateFileNameCandidates(segment.ImageFileName))
        {
            if (seen.Add(fileName))
                yield return fileName;
        }

        if (segment.ImageMd5 is { Length: > 0 } md5)
        {
            var extension = GetFileExtension(segment.ImageFileName);
            var md5Name = Convert.ToHexString(md5).ToLowerInvariant();
            var md5FileName = string.IsNullOrEmpty(extension) ? md5Name : md5Name + extension;
            if (seen.Add(md5FileName))
                yield return md5FileName;
        }
    }

    public static IEnumerable<string> CreateVoiceCandidates(QQMessageSegment segment)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in CreateFileNameCandidates(segment.VoiceFileName))
        {
            if (seen.Add(fileName))
                yield return fileName;
        }

        if (segment.VoiceMd5 is { Length: > 0 } md5)
        {
            var md5Name = Convert.ToHexString(md5).ToLowerInvariant();
            var extension = GetFileExtension(segment.VoiceFileName);
            if (string.IsNullOrWhiteSpace(extension))
                extension = ".amr";

            var md5FileName = md5Name + extension;
            if (seen.Add(md5FileName))
                yield return md5FileName;

            var amrFileName = md5Name + ".amr";
            if (seen.Add(amrFileName))
                yield return amrFileName;
        }
    }

    public static IEnumerable<string> CreateVideoCandidates(QQMessageSegment segment)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in CreateFileNameCandidates(segment.VideoFileName))
        {
            if (seen.Add(fileName))
                yield return fileName;
        }

        if (segment.VideoMd5 is { Length: > 0 } md5)
        {
            var md5Name = Convert.ToHexString(md5).ToLowerInvariant();
            var extension = GetFileExtension(segment.VideoFileName);
            if (string.IsNullOrWhiteSpace(extension))
                extension = ".mp4";

            var md5FileName = md5Name + extension;
            if (seen.Add(md5FileName))
                yield return md5FileName;

            var mp4FileName = md5Name + ".mp4";
            if (seen.Add(mp4FileName))
                yield return mp4FileName;
        }
    }

    public static IEnumerable<string> CreateVideoCoverCandidates(QQMessageSegment segment)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in CreateFileNameCandidates(segment.VideoCoverFileName))
        {
            if (seen.Add(fileName))
                yield return fileName;
        }

        if (segment.VideoMd5 is { Length: > 0 } md5)
        {
            var md5Name = Convert.ToHexString(md5).ToLowerInvariant();
            foreach (var fileName in new[] { $"{md5Name}_0.png", $"{md5Name}_0.jpg", $"{md5Name}.png", $"{md5Name}.jpg" })
            {
                if (seen.Add(fileName))
                    yield return fileName;
            }
        }
    }

    private static IEnumerable<string> CreateFileNameCandidates(string? rawFileName)
    {
        if (string.IsNullOrWhiteSpace(rawFileName))
            yield break;

        var fileName = Path.GetFileName(rawFileName);
        if (string.IsNullOrWhiteSpace(fileName))
            yield break;

        yield return fileName;

        var lowerFileName = fileName.ToLowerInvariant();
        if (!string.Equals(lowerFileName, fileName, StringComparison.Ordinal))
            yield return lowerFileName;

        var extension = Path.GetExtension(fileName);
        var normalizedName = NormalizeHashName(Path.GetFileNameWithoutExtension(fileName));
        if (!string.IsNullOrWhiteSpace(normalizedName))
        {
            yield return normalizedName + extension;

            var lowerNormalizedFileName = (normalizedName + extension).ToLowerInvariant();
            if (!string.Equals(lowerNormalizedFileName, normalizedName + extension, StringComparison.Ordinal))
                yield return lowerNormalizedFileName;
        }
    }

    private static string NormalizeHashName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var normalized = name.Trim();
        if (normalized.Length >= 2 && normalized[0] == '{' && normalized[^1] == '}')
            normalized = normalized[1..^1];

        normalized = normalized.Replace("-", string.Empty, StringComparison.Ordinal);
        return normalized.Length > 0 && normalized.All(char.IsAsciiHexDigit)
            ? normalized.ToLowerInvariant()
            : string.Empty;
    }

    private static string GetFileExtension(string? fileName)
    {
        return string.IsNullOrWhiteSpace(fileName)
            ? string.Empty
            : Path.GetExtension(Path.GetFileName(fileName)).ToLowerInvariant();
    }
}

internal sealed class ReplyTargetConversationResolver
{
    private readonly IEnumerable<AvaQQGroup> _conversations;
    private readonly MessageConversationListApplier _conversationApplier;
    private readonly Func<AvaQQGroup?> _getSelectedConversation;

    public ReplyTargetConversationResolver(
        IEnumerable<AvaQQGroup> conversations,
        MessageConversationListApplier conversationApplier,
        Func<AvaQQGroup?> getSelectedConversation)
    {
        _conversations = conversations;
        _conversationApplier = conversationApplier;
        _getSelectedConversation = getSelectedConversation;
    }

    public AvaQQGroup? Resolve(AvaQQMessage sourceMessage, AvaReplyMessage reply)
    {
        if (sourceMessage.ConversationType == AvaConversationType.Icalingua)
            return FindSourceConversation(sourceMessage.ConversationKey);

        if (reply.SourceGroupId == 0 ||
            reply.SourceGroupId == sourceMessage.GroupId)
        {
            return FindSourceConversation(sourceMessage.ConversationKey);
        }

        var group = _conversationApplier.GetOrCreateGroup(reply.SourceGroupId);
        if (!string.IsNullOrWhiteSpace(reply.SourceGroupName) &&
            string.IsNullOrWhiteSpace(group.GroupName))
        {
            group.GroupName = reply.SourceGroupName;
        }

        return group;
    }

    private AvaQQGroup? FindSourceConversation(string conversationKey)
    {
        var selectedConversation = _getSelectedConversation();
        return selectedConversation?.ConversationKey == conversationKey
            ? selectedConversation
            : _conversations.FirstOrDefault(group => group.ConversationKey == conversationKey);
    }
}

internal static class QqNtReplyTargetResolver
{
    public static uint ResolveSourceGroupId(AvaQQGroup conversation, QQReplyMessage reply)
    {
        if (reply.SourceGroupId == 0)
            return 0;

        if (conversation.ConversationType == AvaConversationType.Group)
        {
            return reply.SourceGroupId == conversation.GroupId
                ? 0
                : reply.SourceGroupId;
        }

        if (conversation.ConversationType != AvaConversationType.Private)
            return 0;

        return reply.SourceGroupId;
    }

    public static string ResolveSourceGroupName(QQReplyMessage reply)
    {
        return !string.IsNullOrWhiteSpace(reply.SourceGroupName)
            ? reply.SourceGroupName
            : reply.SourceGroupId.ToString();
    }

    public static ReplyTargetConversation? ResolveTargetConversation(
        AvaQQGroup conversation,
        MessageRecord sourceMessage,
        QQReplyMessage reply)
    {
        if (reply.SourceGroupId != 0 &&
            reply.SourceGroupId != sourceMessage.GroupId &&
            conversation.ConversationType == AvaConversationType.Group)
        {
            return new ReplyTargetConversation(AvaConversationType.Group, reply.SourceGroupId, 0);
        }

        return conversation.ConversationType switch
        {
            AvaConversationType.Group => new ReplyTargetConversation(AvaConversationType.Group, conversation.GroupId, 0),
            AvaConversationType.Private => new ReplyTargetConversation(AvaConversationType.Private, 0, conversation.PrivateConversationId),
            _ => null,
        };
    }

    public static ReplyTargetConversation? ResolveTargetConversationForReplyFactory(
        MessageRecord sourceMessage,
        AvaQQGroup conversation,
        QQReplyMessage reply)
    {
        return ResolveTargetConversation(conversation, sourceMessage, reply);
    }
}

internal static class LoadedReplyTargetFinder
{
    public static bool TryFind(
        IEnumerable<AvaQQMessage> loadedMessages,
        string? activeConversationKey,
        string sourceConversationKey,
        AvaConversationType sourceConversationType,
        AvaReplyMessage reply,
        out AvaQQMessage loadedMessage)
    {
        loadedMessage = null!;
        if (string.IsNullOrWhiteSpace(sourceConversationKey) ||
            !IsActiveConversationKey(activeConversationKey, sourceConversationKey))
        {
            return false;
        }

        var messages = loadedMessages as IReadOnlyCollection<AvaQQMessage> ?? loadedMessages.ToArray();
        if (messages.Count == 0)
            return false;

        var messageRandoms = ReplyTargetMatcher.GetReplyMessageRandomCandidates(reply);
        var messageSeqs = ReplyTargetMatcher.GetReplyMessageSeqCandidates(reply, sourceConversationType);
        var messageIds = ReplyTargetMatcher.GetReplyMessageIdCandidates(reply);
        foreach (var messageRandom in messageRandoms)
        {
            var randomMessages = messages
                .Where(message => message.MessageRandom == messageRandom)
                .ToArray();

            loadedMessage = randomMessages
                .FirstOrDefault(message => messageSeqs.Contains(ConversationTypeClassifier.IsPCQQ(sourceConversationType)
                    ? message.PCQQMessageSeq
                    : message.MessageSeq))!;
            if (loadedMessage is not null)
                return true;

            loadedMessage = randomMessages
                .FirstOrDefault(message => messageIds.Contains(message.MessageId))!;
            if (loadedMessage is not null)
                return true;

            if (randomMessages.Length == 1)
            {
                loadedMessage = randomMessages[0];
                return true;
            }
        }

        if (messageRandoms.Count > 0 && sourceConversationType == AvaConversationType.Group)
            return false;

        foreach (var messageId in messageIds)
        {
            loadedMessage = messages.FirstOrDefault(message => message.MessageId == messageId)!;
            if (loadedMessage is not null)
                return true;
        }

        foreach (var messageSeq in messageSeqs)
        {
            var loadedMatches = messages
                .Where(message => ConversationTypeClassifier.IsPCQQ(sourceConversationType)
                    ? message.PCQQMessageSeq == messageSeq
                    : message.MessageSeq == messageSeq)
                .ToArray();

            loadedMessage = ReplyTargetMatcher.SelectReplyTargetCandidate(
                loadedMatches,
                reply,
                message => message.SenderId,
                message => message.MessageTime,
                message => message.DisplayText)!;
            if (loadedMessage is not null)
                return true;
        }

        return false;
    }

    private static bool IsActiveConversationKey(string? activeConversationKey, string conversationKey)
    {
        return !string.IsNullOrWhiteSpace(activeConversationKey) &&
               string.Equals(activeConversationKey, conversationKey, StringComparison.Ordinal);
    }
}

internal static class ReplyTargetMatcher
{
    public static T? SelectReplyTargetCandidate<T>(
        IReadOnlyList<T> candidates,
        AvaReplyMessage? reply,
        Func<T, uint> getSenderId,
        Func<T, int> getMessageTime,
        Func<T, string> getDisplayText)
        where T : class
    {
        if (candidates.Count == 0)
            return null;

        if (candidates.Count == 1)
            return candidates[0];

        if (reply is null)
            return null;

        IReadOnlyList<T> currentCandidates = candidates;

        if (reply.SenderId != 0 &&
            TryNarrowReplyTargetCandidates(
                currentCandidates,
                candidate => getSenderId(candidate) == reply.SenderId,
                out var senderMatches))
        {
            if (senderMatches.Count == 1)
                return senderMatches[0];

            currentCandidates = senderMatches;
        }

        if (reply.MessageTime > 0 &&
            TryNarrowReplyTargetCandidates(
                currentCandidates,
                candidate => getMessageTime(candidate) == reply.MessageTime,
                out var timeMatches))
        {
            if (timeMatches.Count == 1)
                return timeMatches[0];

            currentCandidates = timeMatches;
        }

        var previewText = NormalizeReplyTargetText(reply.PreviewText);
        if (!string.IsNullOrWhiteSpace(previewText) &&
            TryNarrowReplyTargetCandidates(
                currentCandidates,
                candidate => IsReplyPreviewTextMatch(getDisplayText(candidate), previewText),
                out var textMatches))
        {
            if (textMatches.Count == 1)
                return textMatches[0];

            currentCandidates = textMatches;
        }

        return currentCandidates.Count == 1 ? currentCandidates[0] : null;
    }

    public static ReplyTargetKey CreateReplyTargetKey(
        AvaConversationType conversationType,
        uint groupId,
        long privateConversationId,
        QQReplyMessage reply)
    {
        var messageIds = GetReplyMessageIdCandidates(reply).ToArray();
        var messageRandoms = GetReplyMessageRandomCandidates(reply).ToArray();
        var messageSeqs = GetReplyMessageSeqCandidates(reply, conversationType).ToArray();
        return new ReplyTargetKey(
            conversationType,
            groupId,
            privateConversationId,
            messageIds.Length > 0 ? messageIds[0] : 0,
            messageIds.Length > 1 ? messageIds[1] : 0,
            messageRandoms.Length > 0 ? messageRandoms[0] : 0,
            messageSeqs.Length > 0 ? messageSeqs[0] : 0,
            messageSeqs.Length > 1 ? messageSeqs[1] : 0);
    }

    public static MessageRecord? TryGetReplyTargetMessage(
        IReadOnlyDictionary<ReplyTargetKey, MessageRecord> replyTargetMessages,
        ReplyTargetConversation targetConversation,
        QQReplyMessage reply)
    {
        return replyTargetMessages.TryGetValue(
            CreateReplyTargetKey(
                targetConversation.ConversationType,
                targetConversation.GroupId,
                targetConversation.PrivateConversationId,
                reply),
            out var message)
            ? message
            : null;
    }

    public static IReadOnlyList<long> GetReplyMessageIdCandidates(AvaReplyMessage reply)
    {
        return new[]
            {
                reply.MessageId,
                reply.InternalMessageId,
            }
            .Where(value => value > 0)
            .Distinct()
            .ToArray();
    }

    public static IReadOnlyList<long> GetReplyMessageIdCandidates(QQReplyMessage reply)
    {
        return new[]
            {
                reply.MessageId,
                reply.InternalMessageId,
            }
            .Where(value => value > 0)
            .Distinct()
            .ToArray();
    }

    public static IReadOnlyList<long> GetReplyMessageRandomCandidates(AvaReplyMessage reply)
    {
        return reply.MessageRandom > 0
            ? [reply.MessageRandom]
            : [];
    }

    public static IReadOnlyList<long> GetReplyMessageRandomCandidates(QQReplyMessage reply)
    {
        return reply.MessageRandom > 0
            ? [reply.MessageRandom]
            : [];
    }

    public static IReadOnlyList<long> GetReplyMessageSeqCandidates(
        AvaReplyMessage reply,
        AvaConversationType conversationType)
    {
        var candidates = conversationType == AvaConversationType.Private
            ? new[]
            {
                // 私聊的 47402/40850 经常是另一套序号；47419 才是当前 c2c_msg_table 会话内的消息序号。
                reply.AlternateMessageSeq,
                reply.MessageSeq,
            }
            : new[]
            {
                reply.MessageSeq,
                reply.AlternateMessageSeq,
            };

        return candidates
            .Where(value => value > 0)
            .Distinct()
            .ToArray();
    }

    public static IReadOnlyList<long> GetReplyMessageSeqCandidates(
        QQReplyMessage reply,
        AvaConversationType conversationType)
    {
        var candidates = conversationType == AvaConversationType.Private
            ? new[]
            {
                reply.MessageSeq2,
                reply.MessageSeq,
            }
            : new[]
            {
                reply.MessageSeq,
                reply.MessageSeq2,
            };

        return candidates
            .Where(value => value > 0)
            .Distinct()
            .ToArray();
    }

    private static bool TryNarrowReplyTargetCandidates<T>(
        IReadOnlyList<T> candidates,
        Func<T, bool> predicate,
        out IReadOnlyList<T> matches)
    {
        matches = candidates.Where(predicate).ToArray();
        return matches.Count > 0 && matches.Count < candidates.Count;
    }

    private static bool IsReplyPreviewTextMatch(string candidateText, string normalizedPreviewText)
    {
        var normalizedCandidateText = NormalizeReplyTargetText(candidateText);
        if (string.IsNullOrWhiteSpace(normalizedCandidateText))
            return false;

        if (string.Equals(normalizedCandidateText, normalizedPreviewText, StringComparison.Ordinal))
            return true;

        return normalizedPreviewText.Length >= 4 &&
               (normalizedCandidateText.Contains(normalizedPreviewText, StringComparison.Ordinal) ||
                normalizedPreviewText.Contains(normalizedCandidateText, StringComparison.Ordinal));
    }

    private static string NormalizeReplyTargetText(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : Regex.Replace(text.Trim(), @"\s+", " ");
    }
}

// recent 表与消息表对齐时使用的 key。
internal readonly record struct RecentGroupMessageKey(
    uint GroupId,
    long MessageId,
    long MessageSeq,
    long MessageRandom);

internal readonly record struct RecentPrivateMessageKey(
    string PeerUid,
    long MessageId,
    long MessageSeq,
    long MessageRandom);

internal sealed record RecentPrivateMessageMatch(
    long ConversationId,
    uint PrivateUin,
    int LastTime,
    MessageRecord? LatestMessage);

internal sealed record MessagePage(
    IReadOnlyList<MessageRecord> Messages,
    IReadOnlyList<MessageRecord> ReferencedMessages,
    IReadOnlyDictionary<ReplyTargetKey, MessageRecord> ReplyTargetMessages)
{
    public static IReadOnlyDictionary<ReplyTargetKey, MessageRecord> EmptyReplyTargetMessages { get; } =
        new Dictionary<ReplyTargetKey, MessageRecord>();
}

internal sealed record QqNtReplyDisplayRequest(
    MessageRecord Item,
    QQMessageContent? Content,
    AvaQQGroup Conversation,
    string CurrentSenderName,
    IReadOnlyDictionary<uint, string> GroupSenderNames,
    IReadOnlyDictionary<long, MessageSenderInfo> MessageSenderInfos,
    IReadOnlyDictionary<ReplyTargetKey, MessageRecord> ReplyTargetMessages,
    LocalMediaContext MediaContext);

// 数据库目录、筛选项、消息时间线由不同数据库实现，接口保持在这里作为 MessageTab 的内部边界。
internal interface IConversationCatalogSource
{
    bool CanApply(IQQDatabase database);

    Task ApplyAsync(IQQDatabase database);
}

internal interface IConversationDatabaseRemovalHandler
{
    bool CanRemove(IQQDatabase database);

    ConversationDatabaseRemovalResult Remove(IQQDatabase database, ConversationDatabaseRemovalContext context);
}

internal interface IMessageFilterOptionSource
{
    bool CanLoadDateOptions(AvaQQGroup conversation);

    IReadOnlyList<MessageDateFilterOption> LoadDateOptions(AvaQQGroup conversation);

    bool CanLoadSenderOptions(AvaQQGroup conversation);

    IReadOnlyList<MessageSenderFilterOption>? TryLoadSenderOptions(AvaQQGroup conversation);
}

internal interface IMessageTimelineProvider
{
    bool CanLoad(AvaQQGroup conversation);

    List<MessageRecord> LoadLatestMessages(
        AvaQQGroup conversation,
        int pageSize,
        MessageFilterCriteria filter);

    List<MessageRecord> LoadEarliestMessages(
        AvaQQGroup conversation,
        int pageSize,
        MessageFilterCriteria filter);

    List<MessageRecord> LoadOlderMessages(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        int pageSize,
        MessageFilterCriteria filter);

    List<MessageRecord> LoadNewerMessages(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        int pageSize,
        MessageFilterCriteria filter);

    List<MessageRecord> LoadNewerOrEqualMessages(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        int pageSize,
        MessageFilterCriteria filter);

    MessageRecord? LoadMessage(
        AvaQQGroup conversation,
        long messageSeq,
        long messageId,
        MessageFilterCriteria filter);
}

internal static class MessageTimelineExporter
{
    public static IReadOnlyList<MessageRecord> LoadAll(
        IMessageTimelineProvider provider,
        AvaQQGroup conversation,
        int pageSize)
    {
        var messages = new List<MessageRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var page = provider.LoadEarliestMessages(conversation, pageSize, MessageFilterCriteria.Empty);
        var previousAnchorKey = string.Empty;

        while (page.Count > 0)
        {
            foreach (var message in page)
            {
                if (seen.Add(CreateMessageKey(message)))
                    messages.Add(message);
            }

            if (page.Count < pageSize)
                break;

            var last = page
                .OrderBy(static message => message.MessageSeq)
                .ThenBy(static message => message.MessageId)
                .Last();
            var anchorKey = CreateMessageKey(last);
            if (string.Equals(anchorKey, previousAnchorKey, StringComparison.Ordinal))
                break;

            previousAnchorKey = anchorKey;
            page = provider.LoadNewerMessages(
                conversation,
                last.MessageSeq,
                last.MessageId,
                pageSize,
                MessageFilterCriteria.Empty);
        }

        return messages
            .OrderBy(static message => message.MessageSeq)
            .ThenBy(static message => message.MessageId)
            .ToArray();
    }

    private static string CreateMessageKey(MessageRecord message)
    {
        return string.Join(
            ":",
            message.MessageId,
            message.MessageRandom,
            message.MessageSeq,
            message.MessageTime,
            message.SenderId);
    }
}
