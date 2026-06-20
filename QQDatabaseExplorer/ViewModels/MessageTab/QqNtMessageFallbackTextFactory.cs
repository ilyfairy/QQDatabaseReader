using System;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal static class QqNtMessageFallbackTextFactory
{
    public static string CreateMissingMessageText(MessageRecord item)
    {
        return FirstNonEmpty(
            QQMessageDisplayText.CreateText(item.Content, item.MessageType, item.SubMessageType),
            CreateUnsupportedMessageText(item.MessageType, item.SubMessageType));
    }

    public static string CreateMissingMessageText(QQForwardedMessage item)
    {
        return FirstNonEmpty(
            QQMessageDisplayText.CreateText(item.Segments, item.MessageType, item.SubMessageType),
            CreateUnsupportedMessageText(item.MessageType, item.SubMessageType));
    }

    public static string CreateUnsupportedSegmentText(MessageRecord item, QQMessageSegment segment)
    {
        return CreateUnsupportedMessageText(item.MessageType, item.SubMessageType, segment);
    }

    public static string CreateUnsupportedSegmentText(QQForwardedMessage item, QQMessageSegment segment)
    {
        return CreateUnsupportedMessageText(item.MessageType, item.SubMessageType, segment);
    }

    public static string CreateUnsupportedSegmentText(
        MessageType messageType,
        SubMessageType subMessageType,
        QQMessageSegment segment)
    {
        return CreateUnsupportedMessageText(messageType, subMessageType, segment);
    }

    private static string CreateUnsupportedMessageText(
        MessageType messageType,
        SubMessageType subMessageType,
        QQMessageSegment? segment = null)
    {
        var messageTypeText = $"{messageType}({(int)messageType})";
        var subMessageTypeText = $"{subMessageType}({(int)subMessageType})";
        if (segment is null)
            return $"[未支持消息: {messageTypeText}, {subMessageTypeText}]";

        return $"[未支持消息: {messageTypeText}, {subMessageTypeText}, 段类型 {(int)segment.Type}]";
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
}
