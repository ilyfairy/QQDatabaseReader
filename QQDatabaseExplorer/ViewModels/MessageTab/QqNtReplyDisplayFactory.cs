using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class QqNtReplyDisplayFactory
{
    private readonly Func<MessageRecord, QQMessageContent?> _parseMessageContent;
    private readonly Func<MessageRecord, AvaQQGroup, QQReplyMessage, ReplyTargetConversation?> _resolveTargetConversation;
    private readonly Func<AvaQQGroup, QQReplyMessage, uint> _resolveSourceGroupId;
    private readonly Func<QQReplyMessage, string> _resolveSourceGroupName;
    private readonly Func<ReplySenderNameRequest, string> _resolveSenderName;

    public QqNtReplyDisplayFactory(
        Func<MessageRecord, QQMessageContent?> parseMessageContent,
        Func<MessageRecord, AvaQQGroup, QQReplyMessage, ReplyTargetConversation?> resolveTargetConversation,
        Func<AvaQQGroup, QQReplyMessage, uint> resolveSourceGroupId,
        Func<QQReplyMessage, string> resolveSourceGroupName,
        Func<ReplySenderNameRequest, string> resolveSenderName)
    {
        _parseMessageContent = parseMessageContent;
        _resolveTargetConversation = resolveTargetConversation;
        _resolveSourceGroupId = resolveSourceGroupId;
        _resolveSourceGroupName = resolveSourceGroupName;
        _resolveSenderName = resolveSenderName;
    }

    public AvaReplyMessage? Create(QqNtReplyDisplayRequest request)
    {
        if (request.Content is null)
            return null;

        var reply = request.Content.Segments
            .Select(segment => segment.Reply)
            .FirstOrDefault(reply => reply is not null);
        if (reply is null)
            return null;

        var replyTargetMessage = _resolveTargetConversation(request.Item, request.Conversation, reply) is { } targetConversation
            ? ReplyTargetMatcher.TryGetReplyTargetMessage(request.ReplyTargetMessages, targetConversation, reply)
            : null;
        var replySegments = replyTargetMessage is null
            ? CreatePreviewSegments(
                request.Item.MessageType,
                request.Item.SubMessageType,
                request.Item.MessageTime,
                reply,
                request.MediaContext)
            : CreatePreviewSegments(replyTargetMessage, request.MediaContext);
        var previewText = MessageTextSegmentBuilder.CreateDisplayText(replySegments);
        if (string.IsNullOrWhiteSpace(previewText))
        {
            previewText = !string.IsNullOrWhiteSpace(reply.PreviewText)
                ? reply.PreviewText
                : "[原消息]";
        }

        var sourceGroupId = _resolveSourceGroupId(request.Conversation, reply);
        return new AvaReplyMessage
        {
            MessageId = reply.MessageId,
            InternalMessageId = reply.InternalMessageId,
            MessageRandom = reply.MessageRandom,
            MessageSeq = ResolveReplyMessageSeq(request.Item.ReplyToMessageSeq, reply),
            AlternateMessageSeq = reply.MessageSeq2,
            SenderId = reply.SenderId,
            SenderName = _resolveSenderName(new ReplySenderNameRequest(
                request.CurrentSenderName,
                request.Item.SenderId,
                request.Conversation,
                reply,
                request.GroupSenderNames,
                request.MessageSenderInfos)),
            MessageTime = reply.MessageTime,
            SourceGroupId = sourceGroupId,
            SourceGroupName = sourceGroupId == 0 ? string.Empty : _resolveSourceGroupName(reply),
            Segments = replySegments,
            PreviewText = previewText.Trim(),
        };
    }

    private List<AvaQQMessageSegment> CreatePreviewSegments(
        MessageRecord replyTargetMessage,
        LocalMediaContext mediaContext)
    {
        var content = _parseMessageContent(replyTargetMessage);
        if (content is null)
            return [];

        var segments = QqNtMessageSegmentFactory.CreateMessageSegments(replyTargetMessage, content);
        if (MessageTextSegmentBuilder.HasDisplayContent(segments))
            return segments;

        segments.Clear();
        segments.Add(AvaQQMessageSegment.CreateUnsupportedText(QqNtMessageFallbackTextFactory.CreateMissingMessageText(replyTargetMessage)));
        return segments;
    }

    public static List<AvaQQMessageSegment> CreatePreviewSegments(
        MessageType messageType,
        SubMessageType subMessageType,
        int messageTime,
        QQReplyMessage reply,
        LocalMediaContext mediaContext)
    {
        var segments = QqNtMessageSegmentFactory.CreateSegments(
            reply.Segments,
            messageType,
            subMessageType,
            _ => "[原消息]");

        if (MessageTextSegmentBuilder.HasDisplayContent(segments))
            return segments;

        segments.Clear();
        if (!string.IsNullOrWhiteSpace(reply.PreviewText))
        {
            segments.AddRange(MessageTextSegmentBuilder.CreateTextSegments(reply.PreviewText));
        }

        return segments;
    }

    public static long ResolveReplyMessageSeq(long replyToMessageSeq, QQReplyMessage reply)
    {
        if (replyToMessageSeq != 0)
            return replyToMessageSeq;

        if (reply.MessageSeq != 0)
            return reply.MessageSeq;

        return reply.MessageSeq2;
    }
}

internal sealed record ReplySenderNameRequest(
    string? CurrentSenderName,
    uint CurrentSenderId,
    AvaQQGroup Conversation,
    QQReplyMessage Reply,
    IReadOnlyDictionary<uint, string> GroupSenderNames,
    IReadOnlyDictionary<long, MessageSenderInfo> MessageSenderInfos);
