using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using QQDatabaseExplorer.Controls;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.Services;

public sealed class ChatExportService
{
    public const string SchemaVersion = "1.0";
    private const string ExporterName = "QQDatabaseExplorer";
    private const string ViewerName = "QQDatabaseExportViewer";
    private const string MhtmlBaseUrl = "https://qqdatabaseexport.local/";
    private const string ExportResourceDirectory = "resources";
    private const int ViewerMessageChunkSize = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new(ChatExportJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
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

        progress?.Report(new ChatExportProgress("准备导出", "创建导出目录", 0, 1));
        var exportRoot = CreateExportRoot(parentDirectory, conversation);
        Directory.CreateDirectory(exportRoot);

        var isJsonOnly = options.Format == ChatExportFormat.Json;
        progress?.Report(new ChatExportProgress("整理消息", "转换聊天记录数据", 0, sources.Sum(static source => source.Messages.Count)));
        var mediaStore = new ExportMediaStore(exportRoot, isJsonOnly ? ChatExportContentOptions.None : options.Content);
        var document = CreateDocument(conversation, sources, mediaStore, progress, cancellationToken);
        if (isJsonOnly)
            document = RemoveAvatarUrls(document);

        string primaryOutputPath;
        if (isJsonOnly)
        {
            progress?.Report(new ChatExportProgress("写入数据", "保存 chat.json", 0, 1));
            primaryOutputPath = await WriteJsonOnlyDataAsync(exportRoot, document, cancellationToken);
        }
        else
        {
            progress?.Report(new ChatExportProgress("复制查看器", "写入离线查看器", 0, 1));
            await CopyViewerAsync(exportRoot, cancellationToken);
            progress?.Report(new ChatExportProgress("写入数据", "保存 resources/chat-data.js", 0, 1));
            await WriteViewerDataAsync(exportRoot, document, cancellationToken);

            if (options.Format == ChatExportFormat.Mhtml)
            {
                progress?.Report(new ChatExportProgress("打包 MHTML", "打包查看器、数据和资源", 0, 1));
                primaryOutputPath = await WriteMhtmlAsync(exportRoot, progress, cancellationToken);
                DeleteMhtmlStagingFiles(exportRoot, primaryOutputPath);
            }
            else
            {
                primaryOutputPath = Path.Combine(exportRoot, "index.html");
            }
        }

        progress?.Report(new ChatExportProgress("完成", "聊天记录导出完成", 1, 1));
        return new ChatExportResult(exportRoot, primaryOutputPath, document.Metadata.MessageCount, sources.Count);
    }

    private static ChatExportDocument CreateDocument(
        AvaQQGroup conversation,
        IReadOnlyList<ChatExportSource> sources,
        ExportMediaStore mediaStore,
        IProgress<ChatExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var messages = sources
            .SelectMany(static source => source.Messages)
            .GroupBy(static message => CreateMergedMessageKey(message), StringComparer.Ordinal)
            .Select(static group => group
                .OrderBy(static message => message.ConversationKey, StringComparer.Ordinal)
                .First())
            .ToArray();
        var participants = CreateParticipants(messages, mediaStore);
        var participantLookup = participants.ToDictionary(static participant => participant.Key, StringComparer.Ordinal);
        var orderedMessages = messages
            .OrderBy(static message => message.MessageTime)
            .ThenBy(static message => message.MessageSeq)
            .ThenBy(static message => message.MessageId)
            .ToArray();
        var exportedMessages = new ChatExportMessage[orderedMessages.Length];

        progress?.Report(new ChatExportProgress("整理消息", "转换聊天记录数据", 0, orderedMessages.Length));
        for (var i = 0; i < orderedMessages.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            exportedMessages[i] = CreateMessage(orderedMessages[i], mediaStore, depth: 0, participantLookup);

            var current = i + 1;
            if (current == orderedMessages.Length || current % 25 == 0)
            {
                progress?.Report(new ChatExportProgress(
                    "整理消息",
                    $"转换聊天记录数据 {current}/{orderedMessages.Length}",
                    current,
                    orderedMessages.Length));
            }
        }

        return new ChatExportDocument(
            SchemaVersion,
            new ChatExportMetadata(
                DateTimeOffset.Now,
                ExporterName,
                ViewerName,
                exportedMessages.Length),
            CreateConversation(conversation, sources, mediaStore),
            participants,
            exportedMessages);
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

    private static IReadOnlyList<ChatExportParticipant> CreateParticipants(
        IReadOnlyList<AvaQQMessage> messages,
        ExportMediaStore mediaStore)
    {
        return messages
            .GroupBy(static message => CreateParticipantKey(message), StringComparer.Ordinal)
            .Select(group => CreateParticipantRef(ChooseParticipantMessage(group), mediaStore))
            .OrderBy(static participant => participant.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static participant => participant.Uin)
            .Select(static participant => new ChatExportParticipant(
                participant.Key,
                participant.Uin,
                participant.Uid,
                participant.DisplayName,
                participant.AvatarUrl,
                participant.AvatarPath))
            .ToArray();
    }

    private static AvaQQMessage ChooseParticipantMessage(IEnumerable<AvaQQMessage> messages)
    {
        AvaQQMessage? first = null;
        AvaQQMessage? firstWithUrl = null;
        foreach (var message in messages)
        {
            first ??= message;
            if (!string.IsNullOrWhiteSpace(message.AvatarLocalPath) &&
                File.Exists(message.AvatarLocalPath))
            {
                return message;
            }

            if (firstWithUrl is null && !string.IsNullOrWhiteSpace(message.AvatarUrl))
                firstWithUrl = message;
        }

        return firstWithUrl ?? first ?? throw new InvalidOperationException("Participant group is empty.");
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

        var name = FirstNonEmpty(
            message.Name,
            message.SenderId == 0 ? null : message.SenderId.ToString(CultureInfo.InvariantCulture),
            message.PeerUid,
            "未知");

        return new ChatExportParticipantRef(
            key,
            message.SenderId,
            message.PeerUid,
            name,
            message.AvatarUrl,
            mediaStore.CopyAvatar(message.AvatarLocalPath, message.AvatarUrl));
    }

    private static string CreateParticipantKey(AvaQQMessage message)
    {
        return !string.IsNullOrWhiteSpace(message.PeerUid) && message.SenderId == 0
            ? "uid:" + message.PeerUid
            : "uin:" + message.SenderId.ToString(CultureInfo.InvariantCulture);
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
        var resourceDirectory = Path.Combine(exportRoot, ExportResourceDirectory);
        Directory.CreateDirectory(resourceDirectory);

        var chunks = await WriteViewerMessageChunksAsync(resourceDirectory, document.Messages ?? [], cancellationToken);
        var manifest = document with
        {
            Messages = null,
            MessageChunks = chunks,
            TimelineDates = CreateTimelineDates(document.Messages ?? []),
            MessageIndex = CreateMessageIndex(document.Messages ?? []),
        };
        var dataPath = Path.Combine(resourceDirectory, "chat-data.js");
        var json = JsonSerializer.Serialize(manifest, JsonOnlyContext.ChatExportDocument);
        var script = "window.__QQ_DATABASE_EXPLORER_CHAT_EXPORT__=" + json + ";";
        await File.WriteAllTextAsync(dataPath, script, new UTF8Encoding(false), cancellationToken);
    }

    private static async Task<IReadOnlyList<ChatExportMessageChunk>> WriteViewerMessageChunksAsync(
        string resourceDirectory,
        IReadOnlyList<ChatExportMessage> messages,
        CancellationToken cancellationToken)
    {
        if (messages.Count == 0)
            return [];

        var messageDirectory = Path.Combine(resourceDirectory, "messages");
        Directory.CreateDirectory(messageDirectory);
        var chunks = new List<ChatExportMessageChunk>((messages.Count + ViewerMessageChunkSize - 1) / ViewerMessageChunkSize);
        for (var start = 0; start < messages.Count; start += ViewerMessageChunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkIndex = chunks.Count;
            var count = Math.Min(ViewerMessageChunkSize, messages.Count - start);
            var chunkMessages = messages.Skip(start).Take(count).ToArray();
            var relativePath = $"{ExportResourceDirectory}/messages/message-{chunkIndex:00000}.js";
            var chunkPath = Path.Combine(messageDirectory, $"message-{chunkIndex:00000}.js");
            var json = JsonSerializer.Serialize(chunkMessages, JsonContext.IReadOnlyListChatExportMessage);
            var script =
                "window.__QQ_DATABASE_EXPLORER_CHAT_EXPORT_MESSAGE_CHUNKS__=window.__QQ_DATABASE_EXPLORER_CHAT_EXPORT_MESSAGE_CHUNKS__||{};" +
                $"window.__QQ_DATABASE_EXPLORER_CHAT_EXPORT_MESSAGE_CHUNKS__[{chunkIndex}]=" +
                json +
                ";";
            await File.WriteAllTextAsync(chunkPath, script, new UTF8Encoding(false), cancellationToken);
            chunks.Add(new ChatExportMessageChunk(chunkIndex, start, count, relativePath));
        }

        return chunks;
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
        var messageSeqs = new HashSet<long>();
        var messageIds = new HashSet<long>();
        var messageRandoms = new HashSet<long>();
        foreach (var message in messages)
        {
            var reply = message.Reply;
            if (reply is null)
                continue;

            AddIfNotZero(messageSeqs, reply.MessageSeq);
            AddIfNotZero(messageSeqs, reply.AlternateMessageSeq);
            AddIfNotZero(messageIds, reply.MessageId);
            AddIfNotZero(messageIds, reply.InternalMessageId);
            AddIfNotZero(messageRandoms, reply.MessageRandom);
        }

        if (messageSeqs.Count == 0 && messageIds.Count == 0 && messageRandoms.Count == 0)
            return [];

        var indexes = new List<ChatExportMessageIndex>();
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (!messageSeqs.Contains(message.MessageSeq) &&
                !messageIds.Contains(message.MessageId) &&
                !messageRandoms.Contains(message.MessageRandom))
            {
                continue;
            }

            indexes.Add(new ChatExportMessageIndex(
                    index,
                    message.Key,
                    message.MessageId,
                    message.MessageRandom,
                    message.MessageSeq));
        }

        return indexes;
    }

    private static void AddIfNotZero(HashSet<long> values, long value)
    {
        if (value != 0)
            values.Add(value);
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

    private static ChatExportDocument RemoveAvatarUrls(ChatExportDocument document)
    {
        return document with
        {
            Conversation = document.Conversation with { AvatarUrl = null },
            Participants = document.Participants
                .Select(static participant => participant with { AvatarUrl = null })
                .ToArray(),
            Messages = document.Messages?
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

    private static async Task<string> WriteMhtmlAsync(
        string exportRoot,
        IProgress<ChatExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var indexPath = Path.Combine(exportRoot, "index.html");
        if (!File.Exists(indexPath))
            throw new FileNotFoundException("Export viewer index.html was not found.", indexPath);

        var boundary = "----=_QQDatabaseExplorer_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var outputPath = Path.Combine(exportRoot, "index.mhtml");
        await using var stream = File.Create(outputPath);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

        await writer.WriteLineAsync("MIME-Version: 1.0");
        await writer.WriteLineAsync($"Content-Type: multipart/related; type=\"text/html\"; boundary=\"{boundary}\"");
        await writer.WriteLineAsync();

        var resourceFiles = Directory.EnumerateFiles(exportRoot, "*", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Where(filePath =>
                !string.Equals(filePath, indexPath, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(filePath, outputPath, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    Path.GetRelativePath(exportRoot, filePath).Replace('\\', '/'),
                    $"{ExportResourceDirectory}/chat-data.js",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var totalParts = resourceFiles.Length + 1;

        var indexHtml = await File.ReadAllTextAsync(indexPath, cancellationToken);
        var chatScript = await File.ReadAllTextAsync(Path.Combine(exportRoot, ExportResourceDirectory, "chat-data.js"), cancellationToken);
        indexHtml = InjectMhtmlDataScript(indexHtml, chatScript);
        await WriteMhtmlPartAsync(
            writer,
            boundary,
            "text/html; charset=utf-8",
            MhtmlBaseUrl + "index.html",
            Encoding.UTF8.GetBytes(indexHtml),
            cancellationToken);
        progress?.Report(new ChatExportProgress("打包 MHTML", "写入查看器和聊天数据", 1, totalParts));

        for (var i = 0; i < resourceFiles.Length; i++)
        {
            var filePath = resourceFiles[i];
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(exportRoot, filePath).Replace('\\', '/');
            await WriteMhtmlPartAsync(
                writer,
                boundary,
                GetContentType(filePath),
                MhtmlBaseUrl + EscapeRelativeUrl(relativePath),
                filePath,
                cancellationToken);
            progress?.Report(new ChatExportProgress(
                "打包 MHTML",
                $"写入资源 {i + 1}/{resourceFiles.Length}",
                i + 2,
                totalParts));
        }

        await writer.WriteLineAsync($"--{boundary}--");
        return outputPath;
    }

    private static string InjectMhtmlDataScript(string indexHtml, string chatScript)
    {
        var dataScript = "<script>" + chatScript + "</script>";
        var headEnd = indexHtml.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headEnd >= 0)
            return indexHtml.Insert(headEnd, "    " + dataScript + Environment.NewLine);

        return dataScript + Environment.NewLine + indexHtml;
    }

    private static void DeleteMhtmlStagingFiles(string exportRoot, string mhtmlPath)
    {
        foreach (var file in Directory.EnumerateFiles(exportRoot, "*", SearchOption.AllDirectories)
                     .OrderByDescending(static path => path.Length))
        {
            if (!string.Equals(file, mhtmlPath, StringComparison.OrdinalIgnoreCase))
                TryDeleteFile(file);
        }

        foreach (var directory in Directory.EnumerateDirectories(exportRoot, "*", SearchOption.AllDirectories)
                     .OrderByDescending(static path => path.Length))
        {
            TryDeleteDirectory(directory);
        }
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
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await writer.WriteLineAsync($"--{boundary}");
        await writer.WriteLineAsync($"Content-Type: {contentType}");
        await writer.WriteLineAsync("Content-Transfer-Encoding: base64");
        await writer.WriteLineAsync($"Content-Location: {contentLocation}");
        await writer.WriteLineAsync();

        await using var fileStream = File.OpenRead(filePath);
        using var transform = new ToBase64Transform();
        using var cryptoStream = new CryptoStream(fileStream, transform, CryptoStreamMode.Read);
        using var reader = new StreamReader(cryptoStream, Encoding.ASCII);

        var buffer = new char[76];
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await writer.WriteLineAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        await writer.WriteLineAsync();
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
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
                """
                <!doctype html>
                <meta charset="utf-8">
                <title>QQ Chat Export</title>
                <body>
                <p>导出查看器尚未构建。请先构建 QQDatabaseExportViewer。</p>
                </body>
                """,
                new UTF8Encoding(false),
                cancellationToken);
            return;
        }

        CopyDirectory(viewerDist, exportRoot, cancellationToken);
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

    private static string CreateExportRoot(string parentDirectory, AvaQQGroup conversation)
    {
        var title = SanitizeFileName(conversation.DisplayName);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var directoryName = string.IsNullOrWhiteSpace(title)
            ? $"ChatExport_{timestamp}"
            : $"ChatExport_{title}_{timestamp}";
        return Path.Combine(parentDirectory, directoryName);
    }

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
            message.DisplayText);
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
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? new FileInfo(path).Length
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
        private readonly string _exportRoot;
        private readonly Dictionary<string, string> _copiedFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> _downloadedAvatars = new(StringComparer.OrdinalIgnoreCase);
        private readonly ChatExportContentOptions _options;
        private static readonly HttpClient AvatarHttpClient = CreateAvatarHttpClient();

        public ExportMediaStore(string exportRoot, ChatExportContentOptions options)
        {
            _exportRoot = exportRoot;
            _options = options;
        }

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

            var fullPath = Path.GetFullPath(Path.Combine(
                _exportRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var rootPath = Path.GetFullPath(_exportRoot);
            var relativeToRoot = Path.GetRelativePath(rootPath, fullPath);
            if (relativeToRoot.StartsWith("..", StringComparison.Ordinal) ||
                Path.IsPathRooted(relativeToRoot))
            {
                return null;
            }

            return LocalImageFile.TryGetImageSize(fullPath);
        }

        private string? DownloadAvatar(string? sourceUrl)
        {
            if (string.IsNullOrWhiteSpace(sourceUrl))
                return null;

            if (_downloadedAvatars.TryGetValue(sourceUrl, out var cachedPath))
                return cachedPath;

            var relativePath = TryDownloadAvatar(sourceUrl);
            _downloadedAvatars[sourceUrl] = relativePath;
            return relativePath;
        }

        private string? TryDownloadAvatar(string sourceUrl)
        {
            if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                return null;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = AvatarHttpClient.Send(request);
                if (!response.IsSuccessStatusCode)
                    return null;

                var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (bytes.Length == 0)
                    return null;

                var extension = GetAvatarExtension(bytes, response.Content.Headers.ContentType?.MediaType);
                if (extension is null)
                    return null;

                var relativeDirectory = $"{ExportResourceDirectory}/avatars";
                var targetDirectory = Path.Combine(_exportRoot, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(targetDirectory);

                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceUrl))).ToLowerInvariant()[..12];
                var targetPath = Path.Combine(targetDirectory, $"avatar_{hash}{extension}");
                File.WriteAllBytes(targetPath, bytes);

                return Path.Combine(relativeDirectory, Path.GetFileName(targetPath)).Replace('\\', '/');
            }
            catch
            {
                return null;
            }
        }

        private string? Copy(string? sourcePath, string relativeDirectory)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                return null;

            try
            {
                var fullPath = Path.GetFullPath(sourcePath);
                if (_copiedFiles.TryGetValue(fullPath, out var copiedRelativePath))
                    return copiedRelativePath;

                var targetDirectory = Path.Combine(_exportRoot, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(targetDirectory);

                var fileName = CreateUniqueFileName(fullPath);
                var targetPath = Path.Combine(targetDirectory, fileName);
                File.Copy(fullPath, targetPath, overwrite: true);

                var relativePath = Path.Combine(relativeDirectory, fileName).Replace('\\', '/');
                _copiedFiles[fullPath] = relativePath;
                return relativePath;
            }
            catch
            {
                return null;
            }
        }

        private string? CopyDisplayImage(string? sourcePath, string relativeDirectory)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                return null;

            try
            {
                var fullPath = Path.GetFullPath(sourcePath);
                if (!LocalImageFile.IsMarketFaceEncodedGifImage(fullPath))
                    return Copy(fullPath, relativeDirectory);

                if (_copiedFiles.TryGetValue(fullPath, out var copiedRelativePath))
                    return copiedRelativePath;

                var bytes = LocalImageFile.ReadDisplayBytes(fullPath);
                var extension = GetImageFileExtension(bytes, null) ?? ".gif";
                var targetDirectory = Path.Combine(_exportRoot, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(targetDirectory);

                var fileName = CreateUniqueFileName(fullPath, extension);
                var targetPath = Path.Combine(targetDirectory, fileName);
                File.WriteAllBytes(targetPath, bytes);

                var relativePath = Path.Combine(relativeDirectory, fileName).Replace('\\', '/');
                _copiedFiles[fullPath] = relativePath;
                return relativePath;
            }
            catch
            {
                return null;
            }
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
            var httpClient = new HttpClient
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
}

public sealed record ChatExportSource(AvaQQGroup Conversation, IReadOnlyList<AvaQQMessage> Messages);

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
