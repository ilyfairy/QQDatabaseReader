using System;
using System.Buffers;
using System.ComponentModel;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using QQDatabaseExplorer.Controls;
using QQDatabaseExplorer.Models;
using QQDatabaseReader.Archives;

namespace QQDatabaseExplorer.Services;

public sealed class ChatExportService
{
    public const string SchemaVersion = "1.0";
    private const string ExporterName = "QQDatabaseExplorer";
    private const string ViewerName = "QQDatabaseExportViewer";
    private const string MhtmlBaseUrl = "https://qqdatabaseexport.local/";
    private const string ExportResourceDirectory = "resources";
    private const int ViewerMessageChunkSize = 1000;
    private const int MaxViewerMessageChunkEntries = 65_536;
    private const int MaxAvatarDownloadBytes = 4 * 1024 * 1024;
    private const int MaxAvatarRedirects = 3;
    private const int MaxReplyMessageIndexEntries = 65_536;
    private const int MaxReplyMessageIndexConversationKeyLength = 512;
    private const int MaxReplyMessageIndexStoredKeyLength = 1024;
    private const int MaxExportParticipantEntries = 8_192;
    private const int MaxExportParticipantKeyLength = 512;
    private const int MaxExportParticipantUidLength = 512;
    private const int MaxExportParticipantNameLength = 1024;
    private const int MaxExportParticipantAvatarValueLength = 2048;
    private const int MaxExportMediaCacheEntries = 8_192;
    private const int MaxExportMediaCacheKeyLength = 1024;
    private const int MaxExportMediaCacheValueLength = 2048;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private static readonly object ExportDirectoryClaimLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new(ChatExportJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };
    private static readonly JsonSerializerOptions JsonOnlyOptions = new(JsonOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly ChatExportJsonContext JsonContext = new(JsonOptions);
    private static readonly ChatExportJsonContext JsonOnlyContext = new(JsonOnlyOptions);

    public async Task<ChatExportResult> ExportAsync(
        AvaQQGroup conversation,
        IReadOnlyList<AvaQQMessage> messages,
        string parentDirectory,
        CancellationToken cancellationToken = default)
    {
        return await ExportAsync(
            conversation,
            [new ChatExportSource(conversation, messages)],
            parentDirectory,
            ChatExportOptions.Default,
            null,
            cancellationToken);
    }

    public async Task<ChatExportResult> ExportAsync(
        AvaQQGroup conversation,
        IReadOnlyList<ChatExportSource> sources,
        string parentDirectory,
        ChatExportOptions options,
        IProgress<ChatExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(parentDirectory))
            throw new ArgumentException("Export directory is empty.", nameof(parentDirectory));
        if (sources.Count == 0)
            throw new ArgumentException("Export sources are empty.", nameof(sources));

        return await ExportDirectAsync(
            [new ChatExportConversationExport(conversation, sources)],
            parentDirectory,
            options,
            progress,
            isMerged: false,
            cancellationToken);
    }

    public async Task<ChatExportResult> ExportMergedAsync(
        IReadOnlyList<ChatExportConversationExport> conversations,
        string parentDirectory,
        ChatExportOptions options,
        IProgress<ChatExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(parentDirectory))
            throw new ArgumentException("Export directory is empty.", nameof(parentDirectory));
        if (conversations.Count == 0)
            throw new ArgumentException("Export conversations are empty.", nameof(conversations));

        return await ExportDirectAsync(
            conversations,
            parentDirectory,
            options,
            progress,
            isMerged: true,
            cancellationToken);
    }

    private async Task<ChatExportResult> ExportDirectAsync(
        IReadOnlyList<ChatExportConversationExport> conversations,
        string parentDirectory,
        ChatExportOptions options,
        IProgress<ChatExportProgress>? progress,
        bool isMerged,
        CancellationToken cancellationToken)
    {
        progress?.Report(new ChatExportProgress("准备导出", "创建导出目录", 0, 1));
        var exportRoot = isMerged
            ? ClaimMergedExportRoot(
                parentDirectory,
                conversations.Select(static item => item.Conversation))
            : ClaimExportRoot(parentDirectory, conversations[0].Conversation);

        var isJsonOnly = options.Format == ChatExportFormat.Json;
        var mediaStore = new ExportMediaStore(
            exportRoot,
            isJsonOnly ? ChatExportContentOptions.None : options.Content,
            collectForMhtml: options.Format == ChatExportFormat.Mhtml,
            cancellationToken);

        ChatExportDocument document;
        if (isMerged)
        {
            var documents = new ChatExportDocument[conversations.Count];
            for (var index = 0; index < conversations.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = conversations[index];
                progress?.Report(new ChatExportProgress(
                    "整理消息",
                    $"{item.Conversation.DisplayName} ({index + 1}/{conversations.Count})",
                    index,
                    conversations.Count));
                documents[index] = await CreateDirectDocumentAsync(
                    item.Conversation,
                    item.Sources,
                    mediaStore,
                    null,
                    cancellationToken);
            }

            document = CreateMergedDocument(documents);
        }
        else
        {
            var item = conversations[0];
            document = await CreateDirectDocumentAsync(
                item.Conversation,
                item.Sources,
                mediaStore,
                progress,
                cancellationToken);
        }

        if (isJsonOnly)
            document = RemoveAvatarUrls(document);

        var primaryOutputPath = await WriteExportAsync(
            exportRoot,
            document,
            options.Format,
            mediaStore,
            progress,
            cancellationToken);
        progress?.Report(new ChatExportProgress("完成", "聊天记录导出完成", 1, 1));
        return new ChatExportResult(
            exportRoot,
            primaryOutputPath,
            document.Metadata.MessageCount,
            conversations.Sum(static item => item.Sources.Count));
    }

    private async Task<ChatExportDocument> CreateDirectDocumentAsync(
        AvaQQGroup conversation,
        IReadOnlyList<ChatExportSource> sources,
        ExportMediaStore mediaStore,
        IProgress<ChatExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var messagesByKey = new Dictionary<string, AvaQQMessage>(StringComparer.Ordinal);
        var loadedCount = 0;
        foreach (var source in sources)
        {
            await foreach (var message in source.ReadMessagesAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = CreateMergedMessageKey(message);
                if (!messagesByKey.TryGetValue(key, out var current) ||
                    string.CompareOrdinal(message.ConversationKey, current.ConversationKey) < 0)
                {
                    messagesByKey[key] = message;
                }

                loadedCount++;
                if (loadedCount % 1000 == 0)
                {
                    progress?.Report(new ChatExportProgress(
                        "读取消息",
                        $"已读取 {loadedCount.ToString(CultureInfo.InvariantCulture)} 条消息",
                        loadedCount,
                        0));
                }
            }
        }

        var messages = messagesByKey.Values.ToArray();
        var participants = CreateParticipants(messages, mediaStore);
        var participantLookup = participants.ToDictionary(static participant => participant.Key, StringComparer.Ordinal);
        var orderedMessages = messages
            .OrderBy(static message => message.MessageTime)
            .ThenBy(static message => message.MessageSeq)
            .ThenBy(static message => message.MessageId)
            .ToArray();
        var exportedMessages = new ChatExportMessage[orderedMessages.Length];

        for (var index = 0; index < orderedMessages.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            exportedMessages[index] = CreateMessage(
                orderedMessages[index],
                mediaStore,
                depth: 0,
                participantLookup);
            var current = index + 1;
            if (current == orderedMessages.Length || current % 100 == 0)
            {
                progress?.Report(new ChatExportProgress(
                    "整理消息",
                    $"已转换 {current.ToString(CultureInfo.InvariantCulture)} 条消息",
                    current,
                    orderedMessages.Length));
            }
        }

        var exportConversation = CreateConversation(conversation, sources, mediaStore);
        return new ChatExportDocument(
            SchemaVersion,
            new ChatExportMetadata(DateTimeOffset.Now, ExporterName, ViewerName, exportedMessages.Length),
            exportConversation,
            [exportConversation],
            participants,
            exportedMessages);
    }

    private static ChatExportDocument CreateMergedDocument(IReadOnlyList<ChatExportDocument> documents)
    {
        var participants = documents
            .SelectMany(static document => document.Participants)
            .GroupBy(static participant => participant.Key, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static participant => participant.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static participant => participant.Uin)
            .Take(MaxExportParticipantEntries)
            .ToArray();
        var conversations = documents
            .Select(static document => document.Conversation)
            .DistinctBy(static conversation => conversation.Key, StringComparer.Ordinal)
            .ToArray();
        var chats = documents
            .Select(static document => new ChatExportChat(
                document.Conversation.Key,
                document.Conversation,
                document.Participants,
                document.Metadata.MessageCount,
                document.Messages))
            .ToArray();

        return new ChatExportDocument(
            SchemaVersion,
            new ChatExportMetadata(
                DateTimeOffset.Now,
                ExporterName,
                ViewerName,
                chats.Sum(static chat => chat.MessageCount)),
            CreateMergedConversation(conversations),
            conversations,
            participants,
            null,
            Chats: chats);
    }

    private static IReadOnlyList<ChatExportParticipant> CreateParticipants(
        IReadOnlyList<AvaQQMessage> messages,
        ExportMediaStore mediaStore)
    {
        var participants = new Dictionary<string, ChatExportParticipant>(StringComparer.Ordinal);
        foreach (var group in messages.GroupBy(
                     static message => CreateParticipantKey(message),
                     StringComparer.Ordinal))
        {
            AddOrUpdateParticipant(
                participants,
                CreateParticipantRef(ChooseParticipantMessage(group), mediaStore));
        }

        return participants.Values
            .OrderBy(static participant => participant.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static participant => participant.Uin)
            .ToArray();
    }

    private static AvaQQMessage ChooseParticipantMessage(IEnumerable<AvaQQMessage> messages)
    {
        AvaQQMessage? first = null;
        AvaQQMessage? firstWithUrl = null;
        foreach (var message in messages)
        {
            first ??= message;
            if (!string.IsNullOrWhiteSpace(message.AvatarLocalPath) && ArchiveFile.Exists(message.AvatarLocalPath))
                return message;
            if (firstWithUrl is null && !string.IsNullOrWhiteSpace(message.AvatarUrl))
                firstWithUrl = message;
        }

        return firstWithUrl ?? first ?? throw new InvalidOperationException("Participant group is empty.");
    }

    private static void AddOrUpdateParticipant(
        IDictionary<string, ChatExportParticipant> participants,
        ChatExportParticipantRef participantRef)
    {
        if (participantRef.Key.Length > MaxExportParticipantKeyLength ||
            (participantRef.Uid?.Length ?? 0) > MaxExportParticipantUidLength ||
            participantRef.DisplayName.Length > MaxExportParticipantNameLength ||
            (participantRef.AvatarUrl?.Length ?? 0) > MaxExportParticipantAvatarValueLength ||
            (participantRef.AvatarPath?.Length ?? 0) > MaxExportParticipantAvatarValueLength)
        {
            return;
        }

        var candidate = new ChatExportParticipant(
            participantRef.Key,
            participantRef.Uin,
            participantRef.Uid,
            participantRef.DisplayName,
            participantRef.AvatarUrl,
            participantRef.AvatarPath);
        if (participants.TryGetValue(candidate.Key, out var current))
        {
            if (GetParticipantQuality(candidate) > GetParticipantQuality(current))
                participants[candidate.Key] = candidate;
            return;
        }

        if (participants.Count < MaxExportParticipantEntries)
            participants[candidate.Key] = candidate;
    }

    private static int GetParticipantQuality(ChatExportParticipant participant)
    {
        if (!string.IsNullOrWhiteSpace(participant.AvatarPath))
            return 2;
        return !string.IsNullOrWhiteSpace(participant.AvatarUrl) ? 1 : 0;
    }

    private static ChatExportConversation CreateMergedConversation(IReadOnlyList<ChatExportConversation> conversations)
    {
        var sources = conversations
            .SelectMany(static conversation => conversation.Sources)
            .DistinctBy(static source => source.Key, StringComparer.Ordinal)
            .ToArray();

        return new ChatExportConversation(
            "merged:" + string.Join("|", conversations.Select(static conversation => conversation.Key)),
            conversations.FirstOrDefault()?.Type ?? AvaConversationType.Group,
            conversations.Count == 1 ? conversations[0].Title : $"合并聊天记录 ({conversations.Count} 个聊天)",
            null,
            null,
            "merged",
            conversations.Count.ToString(CultureInfo.InvariantCulture),
            sources,
            0,
            0,
            0,
            null,
            null,
            0);
    }

    private static ChatExportConversation CreateConversation(
        AvaQQGroup conversation,
        IReadOnlyList<ChatExportSource> sources,
        ExportMediaStore mediaStore)
    {
        var logicalKey = ChatExportConversationMatcher.GetLogicalKey(conversation);
        return new ChatExportConversation(
            conversation.ConversationKey,
            conversation.ConversationType,
            conversation.DisplayName,
            conversation.AvatarUrl,
            mediaStore.CopyAvatar(conversation.AvatarLocalPath, conversation.AvatarUrl),
            logicalKey.Kind,
            logicalKey.Id,
            sources
                .Select(static source => CreateConversationSource(source.Conversation))
                .DistinctBy(static source => source.Key, StringComparer.Ordinal)
                .ToArray(),
            conversation.GroupId,
            conversation.PrivateConversationId,
            conversation.PrivateUin,
            conversation.PrivateUid,
            conversation.AndroidMobileQQPeerUin,
            conversation.IcalinguaRoomId);
    }

    private static ChatExportConversationSource CreateConversationSource(AvaQQGroup conversation)
    {
        return new ChatExportConversationSource(
            conversation.ConversationKey,
            conversation.ConversationType,
            conversation.DisplayName,
            conversation.GroupId,
            conversation.PrivateConversationId,
            conversation.PrivateUin,
            conversation.PrivateUid,
            conversation.AndroidMobileQQPeerUin,
            conversation.IcalinguaRoomId);
    }

    private static ChatExportMessage CreateMessage(
        AvaQQMessage message,
        ExportMediaStore mediaStore,
        int depth,
        IReadOnlyDictionary<string, ChatExportParticipant>? participantLookup = null)
    {
        var segments = message.Segments
            .Select(segment => CreateSegment(segment, mediaStore))
            .ToArray();
        var forwardedMessages = depth >= 8
            ? []
            : message.ForwardedMessages
                .Select(forwarded => CreateMessage(forwarded, mediaStore, depth + 1))
                .ToArray();

        return new ChatExportMessage(
            CreateMessageKey(message),
            message.MessageId,
            message.MessageRandom,
            message.MessageSeq,
            message.PCQQMessageSeq,
            message.MessageTime,
            FormatMessageTime(message.MessageTime),
            CreateParticipantRef(message, mediaStore, participantLookup),
            message.IsSystemHint,
            message.IsRecalledMessage,
            message.DisplayText,
            CreateReply(message.Reply, mediaStore),
            segments,
            message.Reactions.Select(reaction => CreateReaction(reaction, mediaStore)).ToArray(),
            forwardedMessages,
            CreateSystemHint(message, mediaStore),
            CreateRawMessage(message));
    }

    private static ChatExportRawMessage CreateRawMessage(AvaQQMessage message)
    {
        var raw = message.RawData;
        return new ChatExportRawMessage(
            message.ConversationType,
            message.ConversationKey,
            message.ProtobufBase64,
            raw?.Source,
            raw?.MessageType ?? 0,
            raw?.SubMessageType ?? 0,
            raw?.SendType ?? 0,
            raw?.SenderUid ?? string.Empty,
            raw?.PeerUid ?? string.Empty,
            raw?.PeerUin ?? 0,
            raw?.GroupId ?? 0,
            raw?.PrivateConversationId ?? 0,
            raw?.ReplyToMessageSeq ?? 0,
            raw?.ContentBase64,
            raw?.SubContentBase64,
            raw?.MessageReactionsBase64);
    }

    private static ChatExportParticipantRef CreateParticipantRef(
        AvaQQMessage message,
        ExportMediaStore mediaStore,
        IReadOnlyDictionary<string, ChatExportParticipant>? participantLookup = null)
    {
        var key = CreateParticipantKey(message);
        if (participantLookup?.TryGetValue(key, out var participant) == true)
        {
            return new ChatExportParticipantRef(
                participant.Key,
                participant.Uin,
                participant.Uid,
                participant.DisplayName,
                participant.AvatarUrl,
                participant.AvatarPath);
        }

        var senderUid = GetSenderUid(message);
        var name = FirstNonEmpty(
            message.Name,
            message.SenderId == 0 ? null : message.SenderId.ToString(CultureInfo.InvariantCulture),
            senderUid,
            message.PeerUid,
            "未知");

        return new ChatExportParticipantRef(
            key,
            message.SenderId,
            senderUid,
            name,
            message.AvatarUrl,
            mediaStore.CopyAvatar(message.AvatarLocalPath, message.AvatarUrl));
    }

    private static string CreateParticipantKey(AvaQQMessage message)
    {
        var senderUid = GetSenderUid(message);
        return !string.IsNullOrWhiteSpace(senderUid) && message.SenderId == 0
            ? "uid:" + senderUid
            : "uin:" + message.SenderId.ToString(CultureInfo.InvariantCulture);
    }

    private static string? GetSenderUid(AvaQQMessage message)
    {
        return !string.IsNullOrWhiteSpace(message.RawData?.SenderUid)
            ? message.RawData.SenderUid
            : message.SenderId == 0
                ? message.PeerUid
                : null;
    }

    private static ChatExportReply? CreateReply(AvaReplyMessage? reply, ExportMediaStore mediaStore)
    {
        if (reply is null)
            return null;

        return new ChatExportReply(
            reply.MessageId,
            reply.InternalMessageId,
            reply.MessageRandom,
            reply.RawMessageId,
            reply.MessageSeq,
            reply.AlternateMessageSeq,
            reply.SenderId,
            reply.SenderName,
            reply.MessageTime,
            reply.SourceGroupId,
            reply.SourceGroupName,
            reply.PreviewText,
            reply.Segments.Select(segment => CreateSegment(segment, mediaStore)).ToArray());
    }

    private static ChatExportSegment CreateSegment(AvaQQMessageSegment segment, ExportMediaStore mediaStore)
    {
        return new ChatExportSegment(
            segment.Type,
            segment.Tone,
            segment.Text,
            segment.DisplayText,
            segment.LinkUrl,
            segment.IsMention,
            segment.MentionUid,
            segment.FaceId,
            segment.FaceName,
            mediaStore.CopyFaceAsset(segment.FaceAssetPath),
            CreateMedia(segment, mediaStore),
            CreateForwardedCard(segment.ForwardedMessage),
            CreateSharedContactCard(segment.SharedContact),
            CreateMiniAppCard(segment.MiniApp));
    }

    private static ChatExportMedia? CreateMedia(AvaQQMessageSegment segment, ExportMediaStore mediaStore)
    {
        switch (segment.Type)
        {
            case AvaQQMessageSegmentType.Image:
            {
                var relativePath = mediaStore.CopyImage(segment.ImageLocalPath);
                var copiedImageSize = mediaStore.TryGetCopiedImageSize(relativePath);
                return new ChatExportMedia(
                ChatExportMediaKind.Image,
                segment.IsImageAvailable && relativePath is not null,
                Path.GetFileName(segment.ImageLocalPath),
                relativePath,
                null,
                copiedImageSize?.Width ?? segment.ImageWidth,
                copiedImageSize?.Height ?? segment.ImageHeight,
                segment.ImageMaxWidth,
                segment.ImageMaxHeight,
                null,
                null,
                segment.DisplayText);
            }
            case AvaQQMessageSegmentType.Voice:
            {
                var relativePath = mediaStore.CopyVoice(segment.VoiceLocalPath);
                return new ChatExportMedia(
                ChatExportMediaKind.Voice,
                segment.IsVoiceAvailable && relativePath is not null,
                FirstNonEmpty(segment.VoiceFileName, Path.GetFileName(segment.VoiceLocalPath)),
                relativePath,
                null,
                null,
                null,
                null,
                null,
                segment.VoiceDurationMilliseconds,
                TryGetFileSize(segment.VoiceLocalPath),
                segment.DisplayText);
            }
            case AvaQQMessageSegmentType.Video:
            {
                var relativePath = mediaStore.CopyVideo(segment.VideoLocalPath);
                return new ChatExportMedia(
                ChatExportMediaKind.Video,
                segment.IsVideoAvailable && relativePath is not null,
                FirstNonEmpty(segment.VideoFileName, Path.GetFileName(segment.VideoLocalPath)),
                relativePath,
                mediaStore.CopyVideoCover(segment.VideoCoverLocalPath),
                segment.ImageWidth,
                segment.ImageHeight,
                null,
                null,
                segment.VideoDurationMilliseconds,
                TryGetFileSize(segment.VideoLocalPath),
                segment.DisplayText);
            }
            case AvaQQMessageSegmentType.File:
            {
                var relativePath = mediaStore.CopyFile(segment.FileLocalPath);
                return new ChatExportMedia(
                ChatExportMediaKind.File,
                segment.IsFileAvailable && relativePath is not null,
                FirstNonEmpty(segment.FileName, Path.GetFileName(segment.FileLocalPath)),
                relativePath,
                null,
                null,
                null,
                null,
                null,
                null,
                segment.FileSize ?? TryGetFileSize(segment.FileLocalPath),
                segment.DisplayText);
            }
            default:
                return null;
        }
    }

    private static ChatExportForwardedCard? CreateForwardedCard(ForwardedMessageCard? card)
    {
        return card is null
            ? null
            : new ChatExportForwardedCard(
                card.Title,
                card.Footer,
                card.PreviewLines,
                card.Resid,
                card.Uniseq,
                card.FileName,
                card.MessageCount,
                card.RawPayload);
    }

    private static ChatExportSharedContactCard? CreateSharedContactCard(SharedContactCard? card)
    {
        return card is null
            ? null
            : new ChatExportSharedContactCard(
                card.Kind,
                card.Title,
                card.Subtitle,
                card.Tag,
                card.AvatarUrl,
                card.JumpUrl,
                card.RawPayload);
    }

    private static ChatExportMiniAppCard? CreateMiniAppCard(MiniAppCard? card)
    {
        return card is null
            ? null
            : new ChatExportMiniAppCard(
                card.Kind,
                card.AppName,
                card.Title,
                card.HostName,
                card.IconUrl,
                card.PreviewUrl,
                card.JumpUrl,
                card.RawPayload);
    }

    private static ChatExportReaction CreateReaction(AvaMessageReaction reaction, ExportMediaStore mediaStore)
    {
        return new ChatExportReaction(
            reaction.FaceId,
            reaction.Count,
            reaction.DisplayText,
            mediaStore.CopyFaceAsset(reaction.FaceAssetPath));
    }

    private static ChatExportSystemHint? CreateSystemHint(AvaQQMessage message, ExportMediaStore mediaStore)
    {
        if (!message.IsSystemHint)
            return null;

        return new ChatExportSystemHint(
            message.SystemHintSourceName,
            message.SystemHintSourceUin,
            message.SystemHintSourceIsUser,
            message.SystemHintTargetName,
            message.SystemHintTargetUin,
            message.SystemHintTargetIsUser,
            message.SystemHintAction,
            message.SystemHintSuffix,
            message.SystemHintActionImageUrl,
            message.SystemHintTargetMessageSeq,
            message.SystemHintFaceId,
            mediaStore.CopyFaceAsset(message.SystemHintFaceAssetPath));
    }

    private static async Task WriteViewerDataAsync(
        string exportRoot,
        ChatExportDocument document,
        CancellationToken cancellationToken)
    {
        var viewerData = CreateViewerData(document, cancellationToken);
        foreach (var resource in viewerData.MessageChunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resourcePath = Path.Combine(
                exportRoot,
                resource.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(resourcePath)!);
            await File.WriteAllTextAsync(
                resourcePath,
                CreateViewerMessageChunkScript(resource),
                new UTF8Encoding(false),
                cancellationToken);
        }

        var resourceDirectory = Path.Combine(exportRoot, ExportResourceDirectory);
        Directory.CreateDirectory(resourceDirectory);
        var dataPath = Path.Combine(resourceDirectory, "chat-data.js");
        await File.WriteAllTextAsync(
            dataPath,
            viewerData.ManifestScript,
            new UTF8Encoding(false),
            cancellationToken);
    }

    private static ViewerDataExport CreateViewerData(
        ChatExportDocument document,
        CancellationToken cancellationToken)
    {
        var messageChunkResources = new List<ViewerScriptResource>();
        ChatExportDocument manifest;
        if (document.Chats is { Count: > 0 } chats)
        {
            var exportedChats = new ChatExportChat[chats.Count];
            for (var i = 0; i < chats.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chat = chats[i];
                var messages = chat.Messages ?? [];
                var chunks = CreateViewerMessageChunks(
                    messages,
                    $"chat-{i:000}",
                    messageChunkResources,
                    cancellationToken);
                exportedChats[i] = chat with
                {
                    Messages = null,
                    MessageChunks = chunks,
                    TimelineDates = CreateTimelineDates(messages),
                    MessageIndex = CreateMessageIndex(messages),
                };
            }

            manifest = document with
            {
                Messages = null,
                MessageChunks = null,
                TimelineDates = null,
                MessageIndex = null,
                Chats = exportedChats,
            };
        }
        else
        {
            var messages = document.Messages ?? [];
            var chunks = CreateViewerMessageChunks(
                messages,
                null,
                messageChunkResources,
                cancellationToken);
            manifest = document with
            {
                Messages = null,
                MessageChunks = chunks,
                TimelineDates = CreateTimelineDates(messages),
                MessageIndex = CreateMessageIndex(messages),
            };
        }

        var json = JsonSerializer.Serialize(manifest, JsonOnlyContext.ChatExportDocument);
        var script = "window.__QQ_DATABASE_EXPLORER_CHAT_EXPORT__=" + json + ";";
        return new ViewerDataExport(script, messageChunkResources);
    }

    private static IReadOnlyList<ChatExportMessageChunk> CreateViewerMessageChunks(
        IReadOnlyList<ChatExportMessage> messages,
        string? chunkGroupKey,
        ICollection<ViewerScriptResource> resources,
        CancellationToken cancellationToken)
    {
        if (messages.Count == 0)
            return [];

        var chunkSize = GetViewerMessageChunkSize(messages.Count);
        var chunkCount = (int)(((long)messages.Count + chunkSize - 1) / chunkSize);
        var chunks = new List<ChatExportMessageChunk>(chunkCount);
        for (var start = 0; start < messages.Count; start += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkIndex = chunks.Count;
            var count = Math.Min(chunkSize, messages.Count - start);
            var chunkKey = string.IsNullOrWhiteSpace(chunkGroupKey)
                ? chunkIndex.ToString(CultureInfo.InvariantCulture)
                : $"{chunkGroupKey}:{chunkIndex.ToString(CultureInfo.InvariantCulture)}";
            var relativePath = string.IsNullOrWhiteSpace(chunkGroupKey)
                ? $"{ExportResourceDirectory}/messages/message-{chunkIndex:00000}.js"
                : $"{ExportResourceDirectory}/messages/{chunkGroupKey}/message-{chunkIndex:00000}.js";
            resources.Add(new ViewerScriptResource(relativePath, chunkKey, messages, start, count));
            chunks.Add(new ChatExportMessageChunk(chunkIndex, chunkKey, start, count, relativePath));
        }

        return chunks;
    }

    private sealed record ViewerDataExport(
        string ManifestScript,
        IReadOnlyList<ViewerScriptResource> MessageChunks);

    private static string CreateViewerMessageChunkScript(ViewerScriptResource resource)
    {
        var chunkMessages = resource.Messages
            .Skip(resource.Start)
            .Take(resource.Count)
            .ToArray();
        var json = JsonSerializer.Serialize(chunkMessages, JsonContext.IReadOnlyListChatExportMessage);
        return
            "window.__QQ_DATABASE_EXPLORER_CHAT_EXPORT_MESSAGE_CHUNKS__=window.__QQ_DATABASE_EXPLORER_CHAT_EXPORT_MESSAGE_CHUNKS__||{};" +
            "window.__QQ_DATABASE_EXPLORER_CHAT_EXPORT_MESSAGE_CHUNKS__[" +
            JsonSerializer.Serialize(resource.ChunkKey, JsonOptions) +
            "]=" +
            json +
            ";";
    }

    private sealed record ViewerScriptResource(
        string RelativePath,
        string ChunkKey,
        IReadOnlyList<ChatExportMessage> Messages,
        int Start,
        int Count);

    private static int GetViewerMessageChunkSize(int messageCount)
    {
        if (messageCount <= 0)
            return ViewerMessageChunkSize;

        var minimumBoundedSize = checked((int)(((long)messageCount + MaxViewerMessageChunkEntries - 1) /
                                                MaxViewerMessageChunkEntries));
        return Math.Max(ViewerMessageChunkSize, minimumBoundedSize);
    }

    private static IReadOnlyList<ChatExportTimelineDate> CreateTimelineDates(IReadOnlyList<ChatExportMessage> messages)
    {
        if (messages.Count == 0)
            return [];

        var dates = new List<ChatExportTimelineDate>();
        var rowIndex = 0;
        var previousDate = string.Empty;
        for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
        {
            var message = messages[messageIndex];
            var date = message.LocalTime.Length >= 10
                ? message.LocalTime[..10]
                : "未知日期";
            if (!string.Equals(date, previousDate, StringComparison.Ordinal))
            {
                dates.Add(new ChatExportTimelineDate(rowIndex, messageIndex, date));
                rowIndex++;
                previousDate = date;
            }

            rowIndex++;
        }

        return dates;
    }

    private static IReadOnlyList<ChatExportMessageIndex> CreateMessageIndex(IReadOnlyList<ChatExportMessage> messages)
    {
        var targets = new ReplyMessageIndexTargets();
        foreach (var message in messages)
        {
            var reply = message.Reply;
            if (reply is null)
                continue;

            var conversationKey = message.Raw.ConversationKey;
            targets.AddMessageSeq(conversationKey, reply.MessageSeq);
            targets.AddMessageSeq(conversationKey, reply.AlternateMessageSeq);
            targets.AddMessageId(conversationKey, reply.MessageId);
            targets.AddMessageId(conversationKey, reply.InternalMessageId);
            targets.AddMessageRandom(conversationKey, reply.MessageRandom);
        }

        if (targets.IsEmpty)
            return [];

        var indexes = new List<ChatExportMessageIndex>(Math.Min(targets.Count, messages.Count));
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var conversationKey = message.Raw.ConversationKey;
            if (conversationKey.Length > MaxReplyMessageIndexConversationKeyLength ||
                message.Key.Length > MaxReplyMessageIndexStoredKeyLength ||
                (!targets.MessageSeqs.Contains(CreateMessageIndexKey(conversationKey, message.MessageSeq)) &&
                 !targets.MessageIds.Contains(CreateMessageIndexKey(conversationKey, message.MessageId)) &&
                 !targets.MessageRandoms.Contains(CreateMessageIndexKey(conversationKey, message.MessageRandom))))
            {
                continue;
            }

            if (indexes.Count >= MaxReplyMessageIndexEntries)
                return [];

            indexes.Add(new ChatExportMessageIndex(
                    index,
                    message.Key,
                    message.Raw.ConversationKey,
                    message.MessageId,
                    message.MessageRandom,
                    message.MessageSeq));
        }

        return indexes;
    }

    private static string CreateMessageIndexKey(string conversationKey, long value)
    {
        return $"{conversationKey}\0{value.ToString(CultureInfo.InvariantCulture)}";
    }

    private sealed class ReplyMessageIndexTargets
    {
        private int _count;
        private bool _disabled;

        public HashSet<string> MessageSeqs { get; } = new(StringComparer.Ordinal);
        public HashSet<string> MessageIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> MessageRandoms { get; } = new(StringComparer.Ordinal);
        public int Count => _count;
        public bool IsEmpty => _count == 0;

        public void AddMessageSeq(string conversationKey, long value) => Add(MessageSeqs, conversationKey, value);
        public void AddMessageId(string conversationKey, long value) => Add(MessageIds, conversationKey, value);
        public void AddMessageRandom(string conversationKey, long value) => Add(MessageRandoms, conversationKey, value);

        private void Add(HashSet<string> values, string conversationKey, long value)
        {
            if (_disabled || value == 0 || conversationKey.Length > MaxReplyMessageIndexConversationKeyLength)
                return;

            var key = CreateMessageIndexKey(conversationKey, value);
            if (values.Contains(key))
                return;

            if (_count >= MaxReplyMessageIndexEntries)
            {
                MessageSeqs.Clear();
                MessageIds.Clear();
                MessageRandoms.Clear();
                _count = 0;
                _disabled = true;
                return;
            }

            values.Add(key);
            _count++;
        }
    }

    private static async Task<string> WriteExportAsync(
        string exportRoot,
        ChatExportDocument document,
        ChatExportFormat format,
        ExportMediaStore mediaStore,
        IProgress<ChatExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (format == ChatExportFormat.Json)
        {
            progress?.Report(new ChatExportProgress("写入数据", "保存 chat.json", 0, 1));
            return await WriteJsonOnlyDataAsync(exportRoot, document, cancellationToken);
        }

        if (format == ChatExportFormat.Markdown)
        {
            progress?.Report(new ChatExportProgress("写入 Markdown", "保存 chat.md", 0, 1));
            return await WriteMarkdownAsync(exportRoot, document, cancellationToken);
        }

        if (format == ChatExportFormat.Mhtml)
        {
            progress?.Report(new ChatExportProgress("打包 MHTML", "直接写入 index.mhtml", 0, 1));
            return await WriteMhtmlDirectAsync(
                exportRoot,
                document,
                mediaStore,
                progress,
                cancellationToken);
        }

        progress?.Report(new ChatExportProgress("复制查看器", "写入离线查看器", 0, 1));
        await CopyViewerAsync(exportRoot, cancellationToken);
        progress?.Report(new ChatExportProgress("写入数据", "保存 resources/chat-data.js", 0, 1));
        await WriteViewerDataAsync(exportRoot, document, cancellationToken);

        return Path.Combine(exportRoot, "index.html");
    }

    private static async Task<string> WriteJsonOnlyDataAsync(
        string exportRoot,
        ChatExportDocument document,
        CancellationToken cancellationToken)
    {
        var jsonPath = Path.Combine(exportRoot, "chat.json");
        await using (var stream = File.Create(jsonPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                document,
                JsonOnlyContext.ChatExportDocument,
                cancellationToken);
        }

        return jsonPath;
    }

    private static async Task<string> WriteMarkdownAsync(
        string exportRoot,
        ChatExportDocument document,
        CancellationToken cancellationToken)
    {
        var markdownPath = Path.Combine(exportRoot, "chat.md");
        await using var stream = File.Create(markdownPath);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

        await writer.WriteLineAsync("# " + EscapeMarkdown(document.Conversation.Title));
        await writer.WriteLineAsync();
        await writer.WriteLineAsync($"- 消息数量: {document.Metadata.MessageCount.ToString(CultureInfo.InvariantCulture)}");
        await writer.WriteLineAsync($"- 导出时间: {document.Metadata.ExportedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
        if (document.Conversations.Count > 1)
        {
            await writer.WriteLineAsync($"- 聊天数量: {document.Conversations.Count.ToString(CultureInfo.InvariantCulture)}");
        }

        await writer.WriteLineAsync();

        if (document.Chats is { Count: > 0 } chats)
        {
            foreach (var chat in chats)
            {
                await writer.WriteLineAsync();
                await writer.WriteLineAsync("## " + EscapeMarkdown(chat.Conversation.Title));
                await writer.WriteLineAsync();
                await WriteMarkdownMessagesAsync(writer, chat.Messages ?? [], cancellationToken);
            }
        }
        else
        {
            await WriteMarkdownMessagesAsync(writer, document.Messages ?? [], cancellationToken);
        }

        return markdownPath;
    }

    private static async Task WriteMarkdownMessagesAsync(
        StreamWriter writer,
        IReadOnlyList<ChatExportMessage> messages,
        CancellationToken cancellationToken)
    {
        var previousDate = string.Empty;
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var date = message.LocalTime.Length >= 10 ? message.LocalTime[..10] : "未知日期";
            if (!string.Equals(date, previousDate, StringComparison.Ordinal))
            {
                await writer.WriteLineAsync();
                await writer.WriteLineAsync("### " + EscapeMarkdown(date));
                await writer.WriteLineAsync();
                previousDate = date;
            }

            var prefix = $"[{message.LocalTime}] {message.Sender.DisplayName}: ";
            await writer.WriteLineAsync(EscapeMarkdown(prefix) + CreateMarkdownContent(message));
        }
    }

    private static string CreateMarkdownContent(ChatExportMessage message)
    {
        if (message.IsSystemHint)
            return EscapeMarkdown(message.DisplayText);

        var parts = new List<string>();
        if (message.Reply is { } reply && !string.IsNullOrWhiteSpace(reply.PreviewText))
            parts.Add($"> 回复 {EscapeMarkdown(reply.SenderName)}: {EscapeMarkdown(reply.PreviewText)}");

        foreach (var segment in message.Segments)
        {
            if (segment.Media is { } media)
            {
                parts.Add(CreateMarkdownMedia(segment, media));
                continue;
            }

            if (segment.MiniApp is { } miniApp)
            {
                var title = EscapeMarkdown(FirstNonEmpty(miniApp.Title, miniApp.AppName, "小程序"));
                parts.Add(string.IsNullOrWhiteSpace(miniApp.JumpUrl)
                    ? title
                    : $"[{title}]({EscapeMarkdownUrl(miniApp.JumpUrl)})");
                continue;
            }

            if (segment.SharedContact is { } sharedContact)
            {
                parts.Add(EscapeMarkdown($"{sharedContact.Tag} {sharedContact.Title} {sharedContact.Subtitle}".Trim()));
                continue;
            }

            if (segment.ForwardedMessage is { } forwarded)
            {
                parts.Add(EscapeMarkdown($"[转发] {forwarded.Title} {forwarded.Footer}".Trim()));
                continue;
            }

            var text = FirstNonEmpty(segment.DisplayText, segment.Text);
            if (!string.IsNullOrWhiteSpace(text))
                parts.Add(EscapeMarkdown(text));
        }

        if (message.ForwardedMessages.Count > 0)
            parts.Add(EscapeMarkdown($"[合并转发: {message.ForwardedMessages.Count} 条]"));
        if (message.Reactions.Count > 0)
            parts.Add(EscapeMarkdown(string.Join(" ", message.Reactions.Select(static reaction => $"{reaction.DisplayText}×{reaction.Count}"))));

        return parts.Count == 0
            ? EscapeMarkdown(message.DisplayText)
            : string.Join(" ", parts);
    }

    private static string CreateMarkdownMedia(ChatExportSegment segment, ChatExportMedia media)
    {
        var text = EscapeMarkdown(FirstNonEmpty(media.FileName, media.DisplayText, segment.DisplayText, segment.Text, media.Kind.ToString()));
        if (string.IsNullOrWhiteSpace(media.RelativePath))
            return EscapeMarkdown(FirstNonEmpty(media.DisplayText, segment.DisplayText, text));

        var url = EscapeRelativeUrl(media.RelativePath);
        return media.Kind == ChatExportMediaKind.Image
            ? $"![{text}]({url})"
            : $"[{text}]({url})";
    }

    private static string EscapeMarkdown(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal)
            .Replace("#", "\\#", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);
    }

    private static string EscapeMarkdownUrl(string value)
    {
        return value.Replace(" ", "%20", StringComparison.Ordinal);
    }

    private static ChatExportDocument RemoveAvatarUrls(ChatExportDocument document)
    {
        return document with
        {
            Conversation = document.Conversation with { AvatarUrl = null },
            Conversations = document.Conversations
                .Select(static conversation => conversation with { AvatarUrl = null })
                .ToArray(),
            Participants = document.Participants
                .Select(static participant => participant with { AvatarUrl = null })
                .ToArray(),
            Messages = document.Messages?
                .Select(RemoveMessageAvatarUrls)
                .ToArray(),
            Chats = document.Chats?
                .Select(RemoveChatAvatarUrls)
                .ToArray(),
        };
    }

    private static ChatExportChat RemoveChatAvatarUrls(ChatExportChat chat)
    {
        return chat with
        {
            Conversation = chat.Conversation with { AvatarUrl = null },
            Participants = chat.Participants
                .Select(static participant => participant with { AvatarUrl = null })
                .ToArray(),
            Messages = chat.Messages?
                .Select(RemoveMessageAvatarUrls)
                .ToArray(),
        };
    }

    private static ChatExportMessage RemoveMessageAvatarUrls(ChatExportMessage message)
    {
        return message with
        {
            Sender = message.Sender with { AvatarUrl = null },
            Reply = RemoveReplyAvatarUrls(message.Reply),
            Segments = message.Segments
                .Select(RemoveSegmentAvatarUrls)
                .ToArray(),
            ForwardedMessages = message.ForwardedMessages
                .Select(RemoveMessageAvatarUrls)
                .ToArray(),
        };
    }

    private static ChatExportReply? RemoveReplyAvatarUrls(ChatExportReply? reply)
    {
        return reply is null
            ? null
            : reply with
            {
                Segments = reply.Segments
                    .Select(RemoveSegmentAvatarUrls)
                    .ToArray(),
            };
    }

    private static ChatExportSegment RemoveSegmentAvatarUrls(ChatExportSegment segment)
    {
        return segment.SharedContact is null
            ? segment
            : segment with
            {
                SharedContact = segment.SharedContact with { AvatarUrl = null },
            };
    }

    private static async Task<string> WriteMhtmlDirectAsync(
        string exportRoot,
        ChatExportDocument document,
        ExportMediaStore mediaStore,
        IProgress<ChatExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var boundary = "----=_QQDatabaseExplorer_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var outputPath = Path.Combine(exportRoot, "index.mhtml");
        try
        {
            var viewerDist = FindViewerDistDirectory();
            var indexHtml = viewerDist is null
                ? GetFallbackViewerHtml()
                : await File.ReadAllTextAsync(Path.Combine(viewerDist, "index.html"), cancellationToken);
            var viewerData = CreateViewerData(document, cancellationToken);
            var embeddedIndex = InjectMhtmlDataScript(indexHtml, viewerData.ManifestScript);
            var viewerResources = viewerDist is null
                ? []
                : Directory.EnumerateFiles(viewerDist, "*", SearchOption.AllDirectories)
                    .Where(path => !string.Equals(
                        Path.GetRelativePath(viewerDist, path),
                        "index.html",
                        StringComparison.OrdinalIgnoreCase))
                    .Select(path => new MhtmlSourceResource(
                        Path.GetRelativePath(viewerDist, path).Replace('\\', '/'),
                        path,
                        false))
                    .ToArray();
            var mediaResources = mediaStore.MhtmlResources
                .OrderBy(static resource => resource.RelativePath, StringComparer.Ordinal)
                .ToArray();
            var totalParts = checked(
                1 + viewerData.MessageChunks.Count + viewerResources.Length + mediaResources.Length);

            await using var stream = new FileStream(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.SequentialScan);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

            await writer.WriteLineAsync("MIME-Version: 1.0");
            await writer.WriteLineAsync($"Content-Type: multipart/related; type=\"text/html\"; boundary=\"{boundary}\"");
            await writer.WriteLineAsync();

            await WriteMhtmlPartAsync(
                writer,
                boundary,
                "text/html; charset=utf-8",
                MhtmlBaseUrl + "index.html",
                Encoding.UTF8.GetBytes(embeddedIndex),
                cancellationToken);
            progress?.Report(new ChatExportProgress(
                "打包 MHTML",
                "写入查看器和聊天数据",
                1,
                totalParts));

            var completedParts = 1;
            foreach (var resource in viewerData.MessageChunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WriteMhtmlPartAsync(
                    writer,
                    boundary,
                    "text/javascript; charset=utf-8",
                    MhtmlBaseUrl + EscapeRelativeUrl(resource.RelativePath),
                    Encoding.UTF8.GetBytes(CreateViewerMessageChunkScript(resource)),
                    cancellationToken);
                completedParts++;
                ReportMhtmlResourceProgress(progress, completedParts, totalParts);
            }

            foreach (var resource in viewerResources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WriteMhtmlPartAsync(
                    writer,
                    boundary,
                    GetContentType(resource.SourcePath),
                    MhtmlBaseUrl + EscapeRelativeUrl(resource.RelativePath),
                    resource.SourcePath,
                    resource.DecodeMarketFace,
                    cancellationToken);
                completedParts++;
                ReportMhtmlResourceProgress(progress, completedParts, totalParts);
            }

            foreach (var resource in mediaResources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (resource.Content is not null)
                {
                    await WriteMhtmlPartAsync(
                        writer,
                        boundary,
                        GetContentType(resource.RelativePath),
                        MhtmlBaseUrl + EscapeRelativeUrl(resource.RelativePath),
                        resource.Content,
                        cancellationToken);
                }
                else
                {
                    await WriteMhtmlPartAsync(
                        writer,
                        boundary,
                        GetContentType(resource.RelativePath),
                        MhtmlBaseUrl + EscapeRelativeUrl(resource.RelativePath),
                        resource.SourcePath!,
                        resource.DecodeMarketFace,
                        cancellationToken);
                }

                completedParts++;
                ReportMhtmlResourceProgress(progress, completedParts, totalParts);
            }

            await writer.WriteLineAsync($"--{boundary}--");
            return outputPath;
        }
        catch
        {
            TryDeleteFile(outputPath);
            throw;
        }
    }

    private static void ReportMhtmlResourceProgress(
        IProgress<ChatExportProgress>? progress,
        int completedParts,
        int totalParts)
    {
        progress?.Report(new ChatExportProgress(
            "打包 MHTML",
            $"写入资源 {completedParts - 1}/{totalParts - 1}",
            completedParts,
            totalParts));
    }

    private static string InjectMhtmlDataScript(string indexHtml, string chatScript)
    {
        var inlineSafeScript = chatScript.Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
        var dataScript = "<script>" + inlineSafeScript + "</script>";
        var headEnd = indexHtml.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headEnd >= 0)
            return indexHtml.Insert(headEnd, "    " + dataScript + Environment.NewLine);

        return dataScript + Environment.NewLine + indexHtml;
    }

    private static async Task WriteMhtmlPartAsync(
        TextWriter writer,
        string boundary,
        string contentType,
        string contentLocation,
        byte[] content,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await writer.WriteLineAsync($"--{boundary}");
        await writer.WriteLineAsync($"Content-Type: {contentType}");
        await writer.WriteLineAsync("Content-Transfer-Encoding: base64");
        await writer.WriteLineAsync($"Content-Location: {contentLocation}");
        await writer.WriteLineAsync();

        var base64 = Convert.ToBase64String(content);
        for (var i = 0; i < base64.Length; i += 76)
        {
            await writer.WriteLineAsync(base64.Substring(i, Math.Min(76, base64.Length - i)));
        }

        await writer.WriteLineAsync();
    }

    private static async Task WriteMhtmlPartAsync(
        TextWriter writer,
        string boundary,
        string contentType,
        string contentLocation,
        string filePath,
        bool decodeMarketFace,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await writer.WriteLineAsync($"--{boundary}");
        await writer.WriteLineAsync($"Content-Type: {contentType}");
        await writer.WriteLineAsync("Content-Transfer-Encoding: base64");
        await writer.WriteLineAsync($"Content-Location: {contentLocation}");
        await writer.WriteLineAsync();

        await using var fileStream = OpenSourceFile(filePath);
        await WriteBase64StreamAsync(writer, fileStream, decodeMarketFace, cancellationToken);

        await writer.WriteLineAsync();
    }

    private static async Task WriteBase64StreamAsync(
        TextWriter writer,
        Stream source,
        bool decodeMarketFace,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024 + 2);
        try
        {
            var carryCount = 0;
            long sourceOffset = 0;
            while (true)
            {
                var read = await source.ReadAsync(
                    buffer.AsMemory(carryCount, buffer.Length - carryCount),
                    cancellationToken);
                if (read > 0 && decodeMarketFace)
                    ExportMediaStore.DecodeMarketFaceBytes(buffer.AsSpan(carryCount, read), sourceOffset);
                sourceOffset += read;

                var available = carryCount + read;
                var processed = read == 0 ? available : available - available % 3;
                if (processed > 0)
                {
                    var base64 = Convert.ToBase64String(buffer, 0, processed);
                    for (var i = 0; i < base64.Length; i += 76)
                    {
                        await writer.WriteLineAsync(
                            base64.AsMemory(i, Math.Min(76, base64.Length - i)),
                            cancellationToken);
                    }
                }

                carryCount = available - processed;
                if (carryCount > 0)
                    Buffer.BlockCopy(buffer, processed, buffer, 0, carryCount);
                if (read == 0)
                    break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed record MhtmlSourceResource(
        string RelativePath,
        string SourcePath,
        bool DecodeMarketFace);

    // 导出的媒体可能在 tar / zip 归档里 (例如 sdcard.tar\Tencent\MobileQQ\chatpic\...)
    private static Stream OpenSourceFile(string path)
    {
        return File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan)
            : ArchiveFile.OpenRead(path);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static string EscapeRelativeUrl(string relativePath)
    {
        return string.Join(
            "/",
            relativePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
    }

    private static string GetContentType(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            ".ico" => "image/x-icon",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".amr" => "audio/amr",
            ".silk" => "audio/silk",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            _ => "application/octet-stream",
        };
    }

    private static async Task CopyViewerAsync(string exportRoot, CancellationToken cancellationToken)
    {
        var viewerDist = FindViewerDistDirectory();
        if (viewerDist is null)
        {
            await File.WriteAllTextAsync(
                Path.Combine(exportRoot, "index.html"),
                GetFallbackViewerHtml(),
                new UTF8Encoding(false),
                cancellationToken);
            return;
        }

        CopyDirectory(viewerDist, exportRoot, cancellationToken);
    }

    private static string GetFallbackViewerHtml()
    {
        return """
               <!doctype html>
               <meta charset="utf-8">
               <title>QQ Chat Export</title>
               <body>
               <p>导出查看器尚未构建。请先构建 QQDatabaseExportViewer。</p>
               </body>
               """;
    }

    private static string? FindViewerDistDirectory()
    {
        string? fallback = null;
        foreach (var candidate in EnumerateViewerDistCandidates())
        {
            if (!Directory.Exists(candidate) || !File.Exists(Path.Combine(candidate, "index.html")))
                continue;

            fallback ??= candidate;
            if (IsSingleFileViewerDist(candidate))
                return candidate;
        }

        return fallback;
    }

    private static IEnumerable<string> EnumerateViewerDistCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "ChatExportViewer");
        foreach (var candidate in EnumerateRepositoryViewerDistCandidates(Environment.CurrentDirectory))
            yield return candidate;
        foreach (var candidate in EnumerateRepositoryViewerDistCandidates(AppContext.BaseDirectory))
            yield return candidate;
    }

    private static IEnumerable<string> EnumerateRepositoryViewerDistCandidates(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            yield return Path.Combine(directory.FullName, "QQDatabaseExportViewer", "dist");
            directory = directory.Parent;
        }
    }

    private static bool IsSingleFileViewerDist(string viewerDist)
    {
        try
        {
            var indexHtml = File.ReadAllText(Path.Combine(viewerDist, "index.html"));
            return indexHtml.Contains("<script", StringComparison.OrdinalIgnoreCase) &&
                   indexHtml.Contains("<style", StringComparison.OrdinalIgnoreCase) &&
                   !indexHtml.Contains("src=\"/assets/", StringComparison.OrdinalIgnoreCase) &&
                   !indexHtml.Contains("src=\"./assets/", StringComparison.OrdinalIgnoreCase) &&
                   !indexHtml.Contains("src=\"assets/", StringComparison.OrdinalIgnoreCase) &&
                   !indexHtml.Contains("href=\"/assets/", StringComparison.OrdinalIgnoreCase) &&
                   !indexHtml.Contains("href=\"./assets/", StringComparison.OrdinalIgnoreCase) &&
                   !indexHtml.Contains("href=\"assets/", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory, CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destination = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static string ClaimExportRoot(
        string parentDirectory,
        AvaQQGroup conversation)
    {
        return ClaimExportRoot(parentDirectory, conversation.DisplayName);
    }

    private static string ClaimMergedExportRoot(
        string parentDirectory,
        IEnumerable<AvaQQGroup> conversations)
    {
        var titles = conversations
            .Select(static conversation => conversation.DisplayName)
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Take(2)
            .ToArray();
        var title = titles.Length switch
        {
            0 => "合并聊天记录",
            1 => $"合并_{titles[0]}",
            _ => $"合并_{titles[0]}_等",
        };

        return ClaimExportRoot(parentDirectory, title);
    }

    private static string ClaimExportRoot(
        string parentDirectory,
        string titleValue)
    {
        Directory.CreateDirectory(parentDirectory);
        var title = SanitizeFileName(titleValue);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var directoryName = string.IsNullOrWhiteSpace(title)
            ? $"ChatExport_{timestamp}"
            : $"ChatExport_{title}_{timestamp}";

        for (var i = 1;; i++)
        {
            var candidateName = i == 1 ? directoryName : $"{directoryName}_{i}";
            var candidate = Path.Combine(parentDirectory, candidateName);
            if (TryClaimExportDirectory(candidate))
                return candidate;
        }
    }

    private static bool TryClaimExportDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (CreateDirectoryW(path, IntPtr.Zero))
                return true;

            var error = Marshal.GetLastWin32Error();
            if (error is ErrorFileExists or ErrorAlreadyExists)
                return false;
            throw new IOException(
                $"Unable to create export directory '{path}': {new Win32Exception(error).Message}",
                new Win32Exception(error));
        }

        lock (ExportDirectoryClaimLock)
        {
            if (Directory.Exists(path) || File.Exists(path))
                return false;
            Directory.CreateDirectory(path);
            return true;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);

    private static string CreateMessageKey(AvaQQMessage message)
    {
        return string.Join(
            ":",
            message.ConversationKey,
            message.MessageSeq.ToString(CultureInfo.InvariantCulture),
            message.MessageId.ToString(CultureInfo.InvariantCulture),
            message.MessageRandom.ToString(CultureInfo.InvariantCulture));
    }

    private static string CreateMergedMessageKey(AvaQQMessage message)
    {
        var platform = message.ConversationType.ToString();
        if (message.MessageId != 0 ||
            message.MessageSeq != 0 ||
            message.MessageRandom != 0 ||
            message.PCQQMessageSeq != 0)
        {
            return string.Join(
                ":",
                platform,
                "id",
                message.MessageId.ToString(CultureInfo.InvariantCulture),
                message.MessageSeq.ToString(CultureInfo.InvariantCulture),
                message.PCQQMessageSeq.ToString(CultureInfo.InvariantCulture),
                message.MessageRandom.ToString(CultureInfo.InvariantCulture),
                message.MessageTime.ToString(CultureInfo.InvariantCulture),
                message.SenderId.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(
            ":",
            platform,
            "text",
            message.MessageTime.ToString(CultureInfo.InvariantCulture),
            message.SenderId.ToString(CultureInfo.InvariantCulture),
            CreateStableTextFingerprint(message.DisplayText));
    }

    private static string CreateStableTextFingerprint(string value)
    {
        const ulong firstOffset = 14695981039346656037UL;
        const ulong secondOffset = 7809847782465536322UL;
        const ulong firstPrime = 1099511628211UL;
        const ulong secondPrime = 14029467366897019727UL;
        var first = firstOffset;
        var second = secondOffset;
        foreach (var character in value)
        {
            first = unchecked((first ^ character) * firstPrime);
            second = unchecked((second ^ (character + 0x9E37U)) * secondPrime);
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{value.Length:x8}:{first:x16}{second:x16}");
    }

    private static string FormatMessageTime(int messageTime)
    {
        return messageTime <= 0
            ? string.Empty
            : DateTimeOffset.FromUnixTimeSeconds(messageTime).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static long? TryGetFileSize(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && ArchiveFile.Exists(path)
                ? ArchiveFile.GetLength(path)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(invalidChars.Contains(ch) ? '_' : ch);
        }

        var result = builder.ToString().Trim(' ', '.');
        return result.Length > 80 ? result[..80] : result;
    }

    private sealed class ExportMediaStore
    {
        private const int FileBufferSize = 128 * 1024;
        private const int MarketFaceXorChunkSize = 20;
        private const int MarketFaceCycleSize = 50;

        private readonly string _exportRoot;
        private readonly Dictionary<string, string> _copiedFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _copiedFilesByHash = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _copiedImagesByHash = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> _downloadedAvatars = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MhtmlMediaResource> _mhtmlResources = new(StringComparer.OrdinalIgnoreCase);
        private readonly ChatExportContentOptions _options;
        private readonly bool _collectForMhtml;
        private readonly CancellationToken _cancellationToken;
        private static readonly HttpClient AvatarHttpClient = CreateAvatarHttpClient();

        public ExportMediaStore(
            string exportRoot,
            ChatExportContentOptions options,
            bool collectForMhtml,
            CancellationToken cancellationToken)
        {
            _exportRoot = exportRoot;
            _options = options;
            _collectForMhtml = collectForMhtml;
            _cancellationToken = cancellationToken;
        }

        public IReadOnlyCollection<MhtmlMediaResource> MhtmlResources => _mhtmlResources.Values;

        public string? CopyAvatar(string? path, string? url)
        {
            if (!_options.IncludeAvatars)
                return null;

            return Copy(path, $"{ExportResourceDirectory}/avatars") ?? DownloadAvatar(url);
        }

        public string? CopyImage(string? path) => _options.IncludeImages ? CopyDisplayImage(path, $"{ExportResourceDirectory}/images") : null;

        public string? CopyVoice(string? path) => _options.IncludeVoice ? Copy(path, $"{ExportResourceDirectory}/voice") : null;

        public string? CopyVideo(string? path) => _options.IncludeVideos ? Copy(path, $"{ExportResourceDirectory}/video") : null;

        public string? CopyVideoCover(string? path) => _options.IncludeVideos ? Copy(path, $"{ExportResourceDirectory}/video/covers") : null;

        public string? CopyFile(string? path) => _options.IncludeFiles ? Copy(path, $"{ExportResourceDirectory}/files") : null;

        public string? CopyFaceAsset(string? path) => _options.IncludeFaceAssets ? Copy(ResolveAssetPath(path), $"{ExportResourceDirectory}/faces") : null;

        public (int Width, int Height)? TryGetCopiedImageSize(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return null;

            if (_collectForMhtml)
            {
                return _mhtmlResources.TryGetValue(relativePath, out var resource) &&
                       resource.SourcePath is not null
                    ? LocalImageFile.TryGetImageSize(resource.SourcePath)
                    : null;
            }

            var fullPath = Path.GetFullPath(Path.Combine(
                _exportRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            return LocalImageFile.TryGetImageSize(fullPath);
        }

        private string? DownloadAvatar(string? sourceUrl)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(sourceUrl) || sourceUrl.Length > 4096)
                return null;

            if (_downloadedAvatars.TryGetValue(sourceUrl, out var cachedPath))
                return cachedPath;

            var relativePath = TryDownloadAvatar(sourceUrl);
            RememberCacheEntry(_downloadedAvatars, sourceUrl, relativePath);
            return relativePath;
        }

        private string? TryDownloadAvatar(string sourceUrl)
        {
            if (sourceUrl.Length > 4096 ||
                !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ||
                !IsTrustedAvatarDownloadUri(uri))
            {
                return null;
            }

            byte[]? bytes;
            string? mediaType;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                bytes = null;
                mediaType = null;
                for (var redirectCount = 0; redirectCount <= MaxAvatarRedirects; redirectCount++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    using var response = AvatarHttpClient.Send(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token);
                    if ((int)response.StatusCode is >= 300 and < 400)
                    {
                        if (redirectCount == MaxAvatarRedirects ||
                            response.Headers.Location is not { } location)
                        {
                            return null;
                        }

                        var redirectedUri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                        if (!IsTrustedAvatarDownloadUri(redirectedUri))
                            return null;

                        uri = redirectedUri;
                        continue;
                    }

                    if (!response.IsSuccessStatusCode ||
                        response.Content.Headers.ContentLength is > MaxAvatarDownloadBytes)
                    {
                        return null;
                    }

                    using var contentStream = response.Content.ReadAsStream(timeout.Token);
                    bytes = ReadBoundedAvatarBytes(contentStream, timeout.Token);
                    mediaType = response.Content.Headers.ContentType?.MediaType;
                    break;
                }

                if (bytes is null)
                    return null;
                if (bytes.Length == 0)
                    return null;
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }

            var extension = GetAvatarExtension(bytes, mediaType);
            if (extension is null)
                return null;

            var relativeDirectory = $"{ExportResourceDirectory}/avatars";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceUrl))).ToLowerInvariant()[..12];
            var relativePath = $"{relativeDirectory}/avatar_{hash}{extension}";
            _cancellationToken.ThrowIfCancellationRequested();
            if (_collectForMhtml)
            {
                _mhtmlResources[relativePath] = new MhtmlMediaResource(relativePath, null, bytes, false);
                return relativePath;
            }

            var targetPath = Path.Combine(
                _exportRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            try
            {
                File.WriteAllBytes(targetPath, bytes);
                return relativePath;
            }
            catch
            {
                TryDeleteFile(targetPath);
                throw;
            }
        }

        private static bool IsTrustedAvatarDownloadUri(Uri uri)
        {
            if (uri.Scheme is not ("http" or "https") ||
                !uri.IsDefaultPort ||
                !string.IsNullOrEmpty(uri.UserInfo))
            {
                return false;
            }

            var host = uri.IdnHost.TrimEnd('.');
            return host.Equals("qlogo.cn", StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith(".qlogo.cn", StringComparison.OrdinalIgnoreCase);
        }

        private static byte[]? ReadBoundedAvatarBytes(Stream stream, CancellationToken cancellationToken)
        {
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = stream.ReadAsync(buffer.AsMemory(), cancellationToken)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                if (read == 0)
                    return output.ToArray();
                if (output.Length + read > MaxAvatarDownloadBytes)
                    return null;

                output.Write(buffer, 0, read);
            }
        }

        private string? Copy(string? sourcePath, string relativeDirectory)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(sourcePath) || !ArchiveFile.Exists(sourcePath))
                return null;

            var fullPath = Path.GetFullPath(sourcePath);
            var pathKey = CreateResourceKey(relativeDirectory, fullPath);
            if (_copiedFiles.TryGetValue(pathKey, out var copiedRelativePath))
                return copiedRelativePath;

            var fileKey = CreateResourceKey(relativeDirectory, ComputeFileHash(fullPath));
            if (_copiedFilesByHash.TryGetValue(fileKey, out var existingRelativePath))
            {
                RememberCacheEntry(_copiedFiles, pathKey, existingRelativePath);
                return existingRelativePath;
            }

            var fileName = CreateResourceFileName(fullPath, relativeDirectory);
            var relativePath = Path.Combine(relativeDirectory, fileName).Replace('\\', '/');
            StoreFileResource(fullPath, relativePath, decodeMarketFace: false);

            RememberCacheEntry(_copiedFiles, pathKey, relativePath);
            RememberCacheEntry(_copiedFilesByHash, fileKey, relativePath);
            return relativePath;
        }

        private string? CopyDisplayImage(string? sourcePath, string relativeDirectory)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(sourcePath) || !ArchiveFile.Exists(sourcePath))
                return null;

            var fullPath = Path.GetFullPath(sourcePath);
            var pathKey = CreateResourceKey(relativeDirectory, fullPath);
            if (_copiedFiles.TryGetValue(pathKey, out var copiedRelativePath))
                return copiedRelativePath;

            var isEncodedMarketFace = LocalImageFile.IsMarketFaceEncodedGifImage(fullPath);
            var imageHash = ComputeFileHash(fullPath, isEncodedMarketFace);
            var imageKey = CreateResourceKey(relativeDirectory, imageHash);
            if (_copiedImagesByHash.TryGetValue(imageKey, out var existingRelativePath))
            {
                RememberCacheEntry(_copiedFiles, pathKey, existingRelativePath);
                return existingRelativePath;
            }

            var extension = GetImageFileExtension(fullPath, isEncodedMarketFace) ?? Path.GetExtension(fullPath);
            var fileName = $"{imageHash}{extension}";
            var relativePath = Path.Combine(relativeDirectory, fileName).Replace('\\', '/');
            StoreFileResource(fullPath, relativePath, isEncodedMarketFace);

            RememberCacheEntry(_copiedFiles, pathKey, relativePath);
            RememberCacheEntry(_copiedImagesByHash, imageKey, relativePath);
            return relativePath;
        }

        private void StoreFileResource(string sourcePath, string relativePath, bool decodeMarketFace)
        {
            if (_collectForMhtml)
            {
                _mhtmlResources[relativePath] = new MhtmlMediaResource(
                    relativePath,
                    sourcePath,
                    null,
                    decodeMarketFace);
                return;
            }

            var targetPath = Path.Combine(
                _exportRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            CopyFileWithCancellation(sourcePath, targetPath, decodeMarketFace);
        }

        private string ComputeFileHash(string fullPath, bool decodeMarketFace = false)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var stream = OpenSourceFile(fullPath);
            var buffer = ArrayPool<byte>.Shared.Rent(FileBufferSize);
            try
            {
                long offset = 0;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var data = buffer.AsSpan(0, read);
                    if (decodeMarketFace)
                        DecodeMarketFaceBytes(data, offset);
                    hash.AppendData(data);
                    offset += read;
                }

                return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private string? GetImageFileExtension(string fullPath, bool decodeMarketFace)
        {
            Span<byte> header = stackalloc byte[12];
            using var stream = ArchiveFile.OpenRead(fullPath);
            var read = stream.Read(header);
            _cancellationToken.ThrowIfCancellationRequested();
            if (decodeMarketFace)
                DecodeMarketFaceBytes(header[..read], 0);
            return GetImageFileExtension(header[..read].ToArray(), null);
        }

        private void CopyFileWithCancellation(
            string sourcePath,
            string targetPath,
            bool decodeMarketFace)
        {
            try
            {
                using (var source = OpenSourceFile(sourcePath))
                using (var target = new FileStream(
                           targetPath,
                           FileMode.Create,
                           FileAccess.Write,
                           FileShare.None,
                           FileBufferSize,
                           FileOptions.SequentialScan))
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(FileBufferSize);
                    try
                    {
                        long offset = 0;
                        int read;
                        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            _cancellationToken.ThrowIfCancellationRequested();
                            var data = buffer.AsSpan(0, read);
                            if (decodeMarketFace)
                                DecodeMarketFaceBytes(data, offset);
                            target.Write(data);
                            offset += read;
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }

                _cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                TryDeleteFile(targetPath);
                throw;
            }
        }

        internal static void DecodeMarketFaceBytes(Span<byte> bytes, long absoluteOffset)
        {
            var index = 0;
            while (index < bytes.Length)
            {
                var cycleOffset = (int)((absoluteOffset + index) % MarketFaceCycleSize);
                if (cycleOffset < MarketFaceXorChunkSize)
                {
                    var count = Math.Min(MarketFaceXorChunkSize - cycleOffset, bytes.Length - index);
                    for (var i = 0; i < count; i++)
                        bytes[index + i] ^= 0xFF;
                    index += count;
                    continue;
                }

                index += Math.Min(MarketFaceCycleSize - cycleOffset, bytes.Length - index);
            }
        }

        private static string CreateResourceKey(string relativeDirectory, string value)
        {
            return $"{relativeDirectory}|{value}";
        }

        private static void RememberCacheEntry<TValue>(
            IDictionary<string, TValue> cache,
            string key,
            TValue value)
        {
            if (key.Length > MaxExportMediaCacheKeyLength ||
                value is string stringValue && stringValue.Length > MaxExportMediaCacheValueLength)
            {
                return;
            }

            if (cache.ContainsKey(key) || cache.Count < MaxExportMediaCacheEntries)
                cache[key] = value;
        }

        private string CreateResourceFileName(string fullPath, string relativeDirectory)
        {
            var fileName = SanitizeFileName(Path.GetFileName(fullPath));
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = CreateUniqueFileName(fullPath);

            var relativePath = Path.Combine(relativeDirectory, fileName).Replace('\\', '/');
            if (!ResourcePathExists(relativePath))
                return fileName;

            var uniqueFileName = CreateUniqueFileName(fullPath);
            relativePath = Path.Combine(relativeDirectory, uniqueFileName).Replace('\\', '/');
            if (!ResourcePathExists(relativePath))
                return uniqueFileName;

            var name = Path.GetFileNameWithoutExtension(uniqueFileName);
            var extension = Path.GetExtension(uniqueFileName);
            for (var index = 2;; index++)
            {
                var candidate = $"{name}_{index}{extension}";
                relativePath = Path.Combine(relativeDirectory, candidate).Replace('\\', '/');
                if (!ResourcePathExists(relativePath))
                    return candidate;
            }
        }

        private bool ResourcePathExists(string relativePath)
        {
            return _collectForMhtml
                ? _mhtmlResources.ContainsKey(relativePath)
                : File.Exists(Path.Combine(
                    _exportRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static string CreateUniqueFileName(string fullPath, string? extensionOverride = null)
        {
            var name = SanitizeFileName(Path.GetFileNameWithoutExtension(fullPath));
            if (string.IsNullOrWhiteSpace(name))
                name = "file";

            if (name.Length > 60)
                name = name[..60];

            var extension = string.IsNullOrWhiteSpace(extensionOverride) ? Path.GetExtension(fullPath) : extensionOverride;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath))).ToLowerInvariant()[..12];
            return $"{name}_{hash}{extension}";
        }

        private static string? GetAvatarExtension(byte[] bytes, string? mediaType) => GetImageFileExtension(bytes, mediaType);

        private static string? GetImageFileExtension(byte[] bytes, string? mediaType)
        {
            if (bytes.Length >= 8 &&
                bytes[0] == 0x89 &&
                bytes[1] == 0x50 &&
                bytes[2] == 0x4E &&
                bytes[3] == 0x47)
            {
                return ".png";
            }

            if (bytes.Length >= 3 &&
                bytes[0] == 0xFF &&
                bytes[1] == 0xD8 &&
                bytes[2] == 0xFF)
            {
                return ".jpg";
            }

            if (bytes.Length >= 6 &&
                bytes[0] == 0x47 &&
                bytes[1] == 0x49 &&
                bytes[2] == 0x46)
            {
                return ".gif";
            }

            if (bytes.Length >= 12 &&
                bytes[0] == 0x52 &&
                bytes[1] == 0x49 &&
                bytes[2] == 0x46 &&
                bytes[3] == 0x46 &&
                bytes[8] == 0x57 &&
                bytes[9] == 0x45 &&
                bytes[10] == 0x42 &&
                bytes[11] == 0x50)
            {
                return ".webp";
            }

            return mediaType?.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                _ => null,
            };
        }

        private static HttpClient CreateAvatarHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
            };
            var httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(10),
            };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("QQDatabaseExplorer/1.0");
            return httpClient;
        }

        private static string? ResolveAssetPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            if (File.Exists(path))
                return path;

            var normalized = path.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
            foreach (var root in EnumerateAssetRoots())
            {
                var candidate = Path.Combine(root, normalized);
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private static IEnumerable<string> EnumerateAssetRoots()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                yield return directory.FullName;
                yield return Path.Combine(directory.FullName, "QQDatabaseExplorer");
                directory = directory.Parent;
            }
        }
    }

    private sealed record MhtmlMediaResource(
        string RelativePath,
        string? SourcePath,
        byte[]? Content,
        bool DecodeMarketFace);
}

public sealed class ChatExportSource
{
    private readonly Func<CancellationToken, IAsyncEnumerable<AvaQQMessage>>? _messageFactory;

    public ChatExportSource(AvaQQGroup conversation, IReadOnlyList<AvaQQMessage> messages)
    {
        Conversation = conversation;
        Messages = messages;
    }

    public ChatExportSource(
        AvaQQGroup conversation,
        Func<CancellationToken, IAsyncEnumerable<AvaQQMessage>> messageFactory)
    {
        Conversation = conversation;
        Messages = [];
        _messageFactory = messageFactory ?? throw new ArgumentNullException(nameof(messageFactory));
    }

    public AvaQQGroup Conversation { get; }

    public IReadOnlyList<AvaQQMessage> Messages { get; }

    internal async IAsyncEnumerable<AvaQQMessage> ReadMessagesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_messageFactory is null)
        {
            foreach (var message in Messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return message;
            }

            yield break;
        }

        await foreach (var message in _messageFactory(cancellationToken).WithCancellation(cancellationToken))
            yield return message;
    }
}

public sealed record ChatExportConversationExport(AvaQQGroup Conversation, IReadOnlyList<ChatExportSource> Sources);

public sealed record ChatExportResult(
    string ExportDirectory,
    string PrimaryOutputPath,
    int MessageCount,
    int SourceConversationCount);

public readonly record struct ChatExportLogicalConversationKey(string Kind, string Id)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Kind) && !string.IsNullOrWhiteSpace(Id);
}

public static class ChatExportConversationMatcher
{
    public static ChatExportLogicalConversationKey GetLogicalKey(AvaQQGroup conversation)
    {
        return conversation.ConversationType switch
        {
            AvaConversationType.Group => CreateGroupKey(conversation.GroupId),
            AvaConversationType.PCQQGroup => CreateGroupKey(conversation.GroupId),
            AvaConversationType.AndroidMobileQQGroup => CreateGroupKey(conversation.AndroidMobileQQPeerUin),
            AvaConversationType.Private => CreatePrivateKey(
                conversation.PrivateUin != 0
                    ? conversation.PrivateUin.ToString(CultureInfo.InvariantCulture)
                    : conversation.PrivateUid),
            AvaConversationType.PCQQPrivate => CreatePrivateKey(conversation.PrivateUin),
            AvaConversationType.AndroidMobileQQPrivate => CreatePrivateKey(conversation.AndroidMobileQQPeerUin),
            AvaConversationType.Icalingua when conversation.IcalinguaRoomId < 0 =>
                CreateGroupKey(Math.Abs(conversation.IcalinguaRoomId).ToString(CultureInfo.InvariantCulture)),
            AvaConversationType.Icalingua when conversation.IcalinguaRoomId > 0 =>
                CreatePrivateKey(conversation.IcalinguaRoomId.ToString(CultureInfo.InvariantCulture)),
            _ => default,
        };
    }

    public static bool IsSameLogicalConversation(AvaQQGroup left, AvaQQGroup right)
    {
        var leftKey = GetLogicalKey(left);
        var rightKey = GetLogicalKey(right);
        return leftKey.IsValid &&
               rightKey.IsValid &&
               string.Equals(leftKey.Kind, rightKey.Kind, StringComparison.Ordinal) &&
               string.Equals(leftKey.Id, rightKey.Id, StringComparison.Ordinal);
    }

    private static ChatExportLogicalConversationKey CreateGroupKey(uint groupId)
    {
        return groupId == 0
            ? default
            : new ChatExportLogicalConversationKey("group", groupId.ToString(CultureInfo.InvariantCulture));
    }

    private static ChatExportLogicalConversationKey CreateGroupKey(long groupId)
    {
        return groupId == 0
            ? default
            : new ChatExportLogicalConversationKey("group", groupId.ToString(CultureInfo.InvariantCulture));
    }

    private static ChatExportLogicalConversationKey CreateGroupKey(string? groupId)
    {
        return string.IsNullOrWhiteSpace(groupId)
            ? default
            : new ChatExportLogicalConversationKey("group", groupId.Trim());
    }

    private static ChatExportLogicalConversationKey CreatePrivateKey(uint uin)
    {
        return uin == 0
            ? default
            : new ChatExportLogicalConversationKey("private", uin.ToString(CultureInfo.InvariantCulture));
    }

    private static ChatExportLogicalConversationKey CreatePrivateKey(string? uinOrUid)
    {
        return string.IsNullOrWhiteSpace(uinOrUid)
            ? default
            : new ChatExportLogicalConversationKey("private", uinOrUid.Trim());
    }
}
