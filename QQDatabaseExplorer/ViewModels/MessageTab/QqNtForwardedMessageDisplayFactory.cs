using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Controls;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class QqNtForwardedMessageDisplayFactory
{
    private readonly QqNtMessageMediaResolver _mediaResolver;
    private readonly Func<bool> _highlightMentions;

    public QqNtForwardedMessageDisplayFactory(
        QqNtMessageMediaResolver mediaResolver,
        Func<bool> highlightMentions)
    {
        _mediaResolver = mediaResolver;
        _highlightMentions = highlightMentions;
    }

    public IReadOnlyList<AvaQQMessage> CreateForwardedMessages(
        MessageRecord item,
        QQMessageContent? content,
        LocalMediaContext mediaContext)
    {
        if (item.SubContent is not { Length: > 0 } subContent ||
            item.MessageType != MessageType.Forwarded && !ContainsForwardedMessageCard(content))
        {
            return [];
        }

        try
        {
            var messages = QQMessageReader.ParseForwardedMessages(subContent);
            return CreateForwardedMessages(messages, mediaContext);
        }
        catch
        {
            return [];
        }
    }

    public IReadOnlyList<AvaQQMessage> CreateForwardedMessages(
        IReadOnlyList<QQForwardedMessage> messages,
        LocalMediaContext mediaContext)
    {
        if (messages.Count == 0)
            return [];

        var senderNames = CreateForwardedSenderNames(messages);
        return messages
            .Select(message => CreateForwardedMessage(message, senderNames, mediaContext))
            .ToArray();
    }

    private AvaQQMessage CreateForwardedMessage(
        QQForwardedMessage message,
        IReadOnlyDictionary<uint, string> senderNames,
        LocalMediaContext mediaContext)
    {
        var segments = QqNtMessageSegmentFactory.CreateMessageSegments(message, CreateForwardedMessageCard);
        _mediaResolver.ResolveMessageMediaPaths(segments, message, mediaContext);
        if (!MessageTextSegmentBuilder.HasDisplayContent(segments))
        {
            segments.Clear();
            segments.Add(AvaQQMessageSegment.CreateUnsupportedText(QqNtMessageFallbackTextFactory.CreateMissingMessageText(message)));
        }

        var senderName = message.SendNickName | message.SendMemberName ?? string.Empty;
        return new AvaQQMessage
        {
            MessageId = message.MessageId,
            MessageSeq = message.MessageSeq,
            MessageRandom = 0,
            Name = senderName,
            SenderId = message.SenderId,
            CachedAvatarUrl = message.AvatarUrl,
            MessageTime = message.MessageTime,
            Segments = segments,
            ForwardedMessages = CreateForwardedMessages(message.NestedForwardedMessages, mediaContext),
            Reply = CreateReplyMessage(message, senderName, senderNames, mediaContext),
            DisplayText = MessageTextSegmentBuilder.CreateDisplayText(segments),
            IsHoverTimeVisible = true,
            HighlightMentions = _highlightMentions(),
        };
    }

    private static IReadOnlyDictionary<uint, string> CreateForwardedSenderNames(
        IReadOnlyList<QQForwardedMessage> messages)
    {
        return messages
            .Where(message => message.SenderId != 0)
            .Select(message => new
            {
                message.SenderId,
                Name = message.SendNickName | message.SendMemberName,
            })
            .Where(message => !string.IsNullOrWhiteSpace(message.Name))
            .GroupBy(message => message.SenderId)
            .ToDictionary(message => message.Key, message => message.First().Name!);
    }

    private static bool ContainsForwardedMessageCard(QQMessageContent? content)
    {
        if (content is null)
            return false;

        return content.Segments.Any(segment =>
            ForwardedMessageCardParser.TryParse(
                segment.AppJson,
                segment.AppResid,
                segment.AppUniseq,
                segment.Xml,
                segment.XmlResid,
                segment.XmlFileName,
                out var card) &&
            card is not null);
    }

    public static ForwardedMessageCard CreateForwardedMessageCard(IReadOnlyList<QQForwardedMessage> messages)
    {
        var previewLines = messages
            .Take(3)
            .Select(CreateForwardedMessagePreviewLine)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        return new ForwardedMessageCard(
            "群聊的聊天记录",
            $"查看{messages.Count}条转发消息",
            previewLines,
            null,
            null,
            null,
            messages.Count,
            string.Empty);
    }

    private static string CreateForwardedMessagePreviewLine(QQForwardedMessage message)
    {
        var senderName = message.SendNickName | message.SendMemberName;
        var text = QQMessageDisplayText.CreateText(message.Segments, message.MessageType, message.SubMessageType);
        if (string.IsNullOrWhiteSpace(text) && message.NestedForwardedMessages.Count > 0)
            text = "[聊天记录]";

        if (string.IsNullOrWhiteSpace(senderName))
            return text;

        return string.IsNullOrWhiteSpace(text)
            ? senderName
            : $"{senderName}: {text}";
    }

    private static AvaReplyMessage? CreateReplyMessage(
        QQForwardedMessage item,
        string senderName,
        IReadOnlyDictionary<uint, string> forwardedSenderNames,
        LocalMediaContext mediaContext)
    {
        var reply = item.Segments
            .Select(segment => segment.Reply)
            .FirstOrDefault(reply => reply is not null);
        if (reply is null)
            return null;

        var replySegments = QqNtReplyDisplayFactory.CreatePreviewSegments(
            item.MessageType,
            item.SubMessageType,
            item.MessageTime,
            reply,
            mediaContext);
        var previewText = MessageTextSegmentBuilder.CreateDisplayText(replySegments);
        if (string.IsNullOrWhiteSpace(previewText))
        {
            previewText = !string.IsNullOrWhiteSpace(reply.PreviewText)
                ? reply.PreviewText
                : "[原消息]";
        }

        return new AvaReplyMessage
        {
            MessageId = reply.MessageId,
            InternalMessageId = reply.InternalMessageId,
            MessageRandom = reply.MessageRandom,
            MessageSeq = reply.MessageSeq,
            AlternateMessageSeq = reply.MessageSeq2,
            SenderId = reply.SenderId,
            SenderName = ResolveForwardedReplySenderName(item.SenderId, senderName, reply, forwardedSenderNames),
            MessageTime = reply.MessageTime,
            SourceGroupId = reply.SourceGroupId,
            SourceGroupName = reply.SourceGroupName ?? string.Empty,
            Segments = replySegments,
            PreviewText = previewText.Trim(),
        };
    }

    private static string ResolveForwardedReplySenderName(
        uint currentSenderId,
        string currentSenderName,
        QQReplyMessage reply,
        IReadOnlyDictionary<uint, string> forwardedSenderNames)
    {
        if (reply.SourceGroupId != 0 && !string.IsNullOrWhiteSpace(reply.SourceSenderName))
            return reply.SourceSenderName;

        if (reply.SenderId == 0)
            return string.Empty;

        if (reply.SenderId == currentSenderId && !string.IsNullOrWhiteSpace(currentSenderName))
            return currentSenderName;

        return forwardedSenderNames.TryGetValue(reply.SenderId, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : !string.IsNullOrWhiteSpace(reply.SourceSenderName)
                ? reply.SourceSenderName
                : reply.SenderId.ToString();
    }
}
