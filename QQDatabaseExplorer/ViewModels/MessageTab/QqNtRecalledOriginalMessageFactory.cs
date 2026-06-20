using System.Linq;
using QQDatabaseExplorer.Controls;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class QqNtRecalledOriginalMessageFactory
{
    public RecalledOriginalMessage? Create(MessageRecord item, QQMessageContent? content)
    {
        if (content is null)
            return null;

        var originalContent = content.Segments
            .Select(segment => segment.SystemHint?.RecalledOriginalMessageContent)
            .FirstOrDefault(bytes => bytes is { Length: > 0 });
        if (originalContent is not { Length: > 0 })
            return null;

        var originalMessage = TryParseOriginalMessage(item, originalContent);
        if (originalMessage is null || originalMessage.Segments.Count == 0)
            return null;

        NormalizeOriginalMessage(item, originalMessage);

        var segments = QqNtMessageSegmentFactory.CreateMessageSegments(
            originalMessage,
            QqNtForwardedMessageDisplayFactory.CreateForwardedMessageCard);
        return MessageTextSegmentBuilder.HasDisplayContent(segments)
            ? new RecalledOriginalMessage(originalMessage, segments)
            : null;
    }

    private static QQForwardedMessage? TryParseOriginalMessage(MessageRecord item, byte[] originalContent)
    {
        try
        {
            return QQMessageReader.ParseEmbeddedMessageRecord(originalContent);
        }
        catch
        {
            return TryParseSingleSegmentOriginalMessage(item, originalContent);
        }
    }

    private static QQForwardedMessage? TryParseSingleSegmentOriginalMessage(MessageRecord item, byte[] originalContent)
    {
        try
        {
            var originalSegment = QQMessageReader.ParseMessageSegment(originalContent);
            return new QQForwardedMessage
            {
                MessageType = MessageType.Text,
                SubMessageType = SubMessageType.Text,
                MessageTime = item.MessageTime,
                Segments = [originalSegment],
            };
        }
        catch
        {
            return null;
        }
    }

    private static void NormalizeOriginalMessage(MessageRecord item, QQForwardedMessage originalMessage)
    {
        originalMessage.MessageType = originalMessage.MessageType == default
            ? MessageType.Text
            : originalMessage.MessageType;
        originalMessage.SubMessageType = originalMessage.SubMessageType == default
            ? SubMessageType.Text
            : originalMessage.SubMessageType;
        if (originalMessage.MessageTime <= 0)
            originalMessage.MessageTime = item.MessageTime;
    }
}
