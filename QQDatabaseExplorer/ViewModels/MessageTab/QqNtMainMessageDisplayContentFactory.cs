using System.Collections.Generic;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class QqNtMainMessageDisplayContentFactory
{
    private readonly QqNtMessageMediaResolver _mediaResolver;
    private readonly QqNtRecalledOriginalMessageFactory _recalledOriginalMessageFactory;

    public QqNtMainMessageDisplayContentFactory(
        QqNtMessageMediaResolver mediaResolver,
        QqNtRecalledOriginalMessageFactory recalledOriginalMessageFactory)
    {
        _mediaResolver = mediaResolver;
        _recalledOriginalMessageFactory = recalledOriginalMessageFactory;
    }

    public QqNtMainMessageDisplayContent Create(
        MessageRecord item,
        string senderName,
        LocalMediaContext mediaContext)
    {
        var content = QqNtMessageContentParser.TryParse(item.Content);
        var segments = content is null
            ? []
            : QqNtMessageSegmentFactory.CreateMessageSegments(item, content);
        _mediaResolver.ResolveMessageMediaPaths(segments, item, content, mediaContext);

        var recalledMessage = _recalledOriginalMessageFactory.Create(item, content);
        if (recalledMessage is { } recalled)
        {
            segments = recalled.Segments;
            _mediaResolver.ResolveMessageMediaPaths(segments, recalled.ForwardedMessage, mediaContext);
            senderName = FirstNonEmpty(
                recalled.ForwardedMessage.SendMemberName,
                recalled.ForwardedMessage.SendNickName,
                senderName);
        }

        return new QqNtMainMessageDisplayContent(
            content,
            segments,
            recalledMessage,
            senderName);
    }

    public static void EnsureFallbackText(
        List<AvaQQMessageSegment> segments,
        MessageRecord item)
    {
        if (MessageTextSegmentBuilder.HasDisplayContent(segments))
            return;

        segments.Clear();
        segments.Add(AvaQQMessageSegment.CreateUnsupportedText(QqNtMessageFallbackTextFactory.CreateMissingMessageText(item)));
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
