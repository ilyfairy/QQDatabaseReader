using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal static class QqNtMessageSegmentFactory
{
    public static List<AvaQQMessageSegment> CreateMessageSegments(
        MessageRecord item,
        QQMessageContent content)
    {
        return CreateSegments(
            content.Segments,
            item.MessageType,
            item.SubMessageType,
            segment => QqNtMessageFallbackTextFactory.CreateUnsupportedSegmentText(item, segment));
    }

    public static List<AvaQQMessageSegment> CreateMessageSegments(
        QQForwardedMessage item,
        Func<IReadOnlyList<QQForwardedMessage>, ForwardedMessageCard> createForwardedMessageCard)
    {
        if (item.Segments.Count == 0 && item.NestedForwardedMessages.Count > 0)
        {
            return [AvaQQMessageSegment.CreateForwardedMessage(createForwardedMessageCard(item.NestedForwardedMessages))];
        }

        var segments = new List<AvaQQMessageSegment>();
        foreach (var segment in item.Segments)
        {
            if (QqNtRichCardSegmentFactory.CreateRichCardSegment(item, segment, createForwardedMessageCard) is { } richCardSegment)
            {
                segments.Add(richCardSegment);
                continue;
            }

            AddSegment(
                segments,
                segment,
                item.MessageType,
                item.SubMessageType,
                QqNtMessageFallbackTextFactory.CreateUnsupportedSegmentText(item, segment));
        }

        return segments;
    }

    public static List<AvaQQMessageSegment> CreateSegments(
        IReadOnlyList<QQMessageSegment> sourceSegments,
        MessageType messageType,
        SubMessageType subMessageType,
        Func<QQMessageSegment, string>? createUnsupportedText = null)
    {
        var segments = new List<AvaQQMessageSegment>();

        foreach (var segment in sourceSegments)
        {
            AddSegment(
                segments,
                segment,
                messageType,
                subMessageType,
                createUnsupportedText?.Invoke(segment) ??
                QqNtMessageFallbackTextFactory.CreateUnsupportedSegmentText(messageType, subMessageType, segment));
        }

        return segments;
    }

    private static void AddSegment(
        List<AvaQQMessageSegment> segments,
        QQMessageSegment segment,
        MessageType messageType,
        SubMessageType subMessageType,
        string unsupportedText)
    {
        if (segment.Type == MessageSegmentType.Reply)
            return;

        if (MessageMediaSegmentClassifier.IsVoiceSegment(messageType, segment))
        {
            segments.Add(QqNtMediaMessageSegmentFactory.CreateVoice(segment));
            return;
        }

        if (MessageMediaSegmentClassifier.IsVideoSegment(messageType, segment))
        {
            segments.Add(QqNtMediaMessageSegmentFactory.CreateVideo(segment));
            return;
        }

        if (QQMessageDisplayText.TryGetPriorityText(messageType, subMessageType, out var priorityText) &&
            !MessageMediaSegmentClassifier.IsRenderableImageSegment(messageType, subMessageType, segment))
        {
            AddUnsupportedMessagePlaceholder(segments, priorityText);
            return;
        }

        if (QqNtRichCardSegmentFactory.CreateRichCardSegment(segment) is { } richCardSegment)
        {
            segments.Add(richCardSegment);
            return;
        }

        if (segment.Type == MessageSegmentType.Xml &&
            (!string.IsNullOrWhiteSpace(segment.Xml) ||
             !string.IsNullOrWhiteSpace(segment.XmlResid) ||
             !string.IsNullOrWhiteSpace(segment.XmlFileName)))
        {
            return;
        }

        if (segment.IsQQFace && segment.FaceId is { } faceId)
        {
            var faceSegment = AvaQQMessageSegment.CreateQQFace(faceId);
            segments.Add(string.IsNullOrWhiteSpace(faceSegment.FaceAssetPath)
                ? AvaQQMessageSegment.CreateUnsupportedText(faceSegment.DisplayText)
                : faceSegment);
            return;
        }

        if (segment.IsMarketFace)
        {
            segments.Add(QqNtMediaMessageSegmentFactory.CreateMarketFace(segment));
            return;
        }

        if (MessageMediaSegmentClassifier.IsRenderableImageSegment(messageType, subMessageType, segment))
        {
            segments.Add(QqNtMediaMessageSegmentFactory.CreateImage(segment, messageType, subMessageType));
            return;
        }

        var text = QQMessageDisplayText.CreateSegmentText(segment, messageType, subMessageType);
        if (!string.IsNullOrEmpty(text))
        {
            if (IsUnsupportedDisplaySegment(segment))
            {
                segments.Add(AvaQQMessageSegment.CreateUnsupportedText(text));
            }
            else
            {
                segments.AddRange(MessageTextSegmentBuilder.CreateTextSegments(text, isMention: segment.IsMention, mentionUid: segment.MentionUid));
            }

            return;
        }

        segments.Add(AvaQQMessageSegment.CreateUnsupportedText(unsupportedText));
    }

    private static void AddUnsupportedMessagePlaceholder(
        List<AvaQQMessageSegment> segments,
        string unsupportedText)
    {
        if (segments.Any(segment =>
                segment.Type == AvaQQMessageSegmentType.Unsupported &&
                string.Equals(segment.Text, unsupportedText, StringComparison.Ordinal)))
        {
            return;
        }

        segments.Add(AvaQQMessageSegment.CreateUnsupportedText(unsupportedText));
    }

    private static bool IsUnsupportedDisplaySegment(QQMessageSegment segment)
    {
        return segment.Type is MessageSegmentType.File
            or MessageSegmentType.Record
            or MessageSegmentType.Video
            or MessageSegmentType.System
            or MessageSegmentType.App
            or MessageSegmentType.RichMedia
            or MessageSegmentType.Xml
            or MessageSegmentType.Call
            or MessageSegmentType.Dynamic;
    }

}
