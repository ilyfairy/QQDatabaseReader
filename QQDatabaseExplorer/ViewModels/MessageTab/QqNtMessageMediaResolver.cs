using System.Collections.Generic;
using System.IO;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class QqNtMessageMediaResolver
{
    private readonly MessageSegmentMediaApplier _mediaApplier;

    public QqNtMessageMediaResolver(MessageSegmentMediaApplier mediaApplier)
    {
        _mediaApplier = mediaApplier;
    }

    public void ResolveMessageMediaPaths(
        IReadOnlyList<AvaQQMessageSegment> segments,
        MessageRecord item,
        QQMessageContent? content,
        LocalMediaContext mediaContext)
    {
        if (content is null || segments.Count == 0)
            return;

        ApplyResolvedMediaPaths(
            segments,
            content.Segments,
            segment => ResolveMediaPathForMessageSegment(segment, item, mediaContext),
            CreateUnavailableMediaText(item));
    }

    public void ResolveMessageMediaPaths(
        IReadOnlyList<AvaQQMessageSegment> segments,
        QQForwardedMessage item,
        LocalMediaContext mediaContext)
    {
        if (segments.Count == 0 || item.Segments.Count == 0)
            return;

        ApplyResolvedMediaPaths(
            segments,
            item.Segments,
            segment => ResolveMediaPathForForwardedMessageSegment(segment, item, mediaContext),
            CreateUnavailableMediaText(item.SubMessageType));
    }

    private void ApplyResolvedMediaPaths(
        IReadOnlyList<AvaQQMessageSegment> segments,
        IReadOnlyList<QQMessageSegment> parsedSegments,
        TryResolveSegmentMediaPath resolveMediaPath,
        string unavailableMediaText)
    {
        var segmentIndex = 0;
        foreach (var parsedSegment in parsedSegments)
        {
            if (parsedSegment.Type == MessageSegmentType.Reply ||
                resolveMediaPath(parsedSegment) is not { } mediaPath)
            {
                continue;
            }

            // 显示段和 protobuf 段不是一一同索引关系，只能按顺序找下一个媒体段。
            while (segmentIndex < segments.Count &&
                   !MessageMediaSegmentClassifier.MatchesMediaSegment(segments[segmentIndex], parsedSegment))
            {
                segmentIndex++;
            }

            if (segmentIndex >= segments.Count)
                return;

            _mediaApplier.Apply(
                segments[segmentIndex],
                parsedSegment,
                mediaPath.LocalPath,
                mediaPath.IsMissing,
                mediaPath.CoverPath,
                mediaPath.IsCoverAvailable,
                unavailableMediaText);
            segmentIndex++;
        }
    }

    private static ResolvedSegmentMediaPath? ResolveMediaPathForMessageSegment(
        QQMessageSegment segment,
        MessageRecord item,
        LocalMediaContext mediaContext)
    {
        return ResolveMediaPath(
            segment,
            item.MessageType,
            item.SubMessageType,
            item.MessageTime,
            mediaContext);
    }

    private static ResolvedSegmentMediaPath? ResolveMediaPathForForwardedMessageSegment(
        QQMessageSegment segment,
        QQForwardedMessage item,
        LocalMediaContext mediaContext)
    {
        return ResolveMediaPath(
            segment,
            item.MessageType,
            item.SubMessageType,
            item.MessageTime,
            mediaContext);
    }

    private static ResolvedSegmentMediaPath? ResolveMediaPath(
        QQMessageSegment segment,
        MessageType messageType,
        SubMessageType subMessageType,
        int messageTime,
        LocalMediaContext mediaContext)
    {
        if (segment.IsMarketFace)
        {
            var localPath = QqNtMarketFacePathResolver.ResolvePath(mediaContext.NtDataPath, segment);
            return ResolvedSegmentMediaPath.FromLocalPath(localPath);
        }

        if (MessageMediaSegmentClassifier.IsVoiceSegment(messageType, segment))
        {
            var localPath = MessageMediaPathResolver.ResolveLocalVoicePath(mediaContext, messageTime, segment);
            return ResolvedSegmentMediaPath.FromLocalPath(localPath);
        }

        if (MessageMediaSegmentClassifier.IsVideoSegment(messageType, segment))
        {
            var resolved = MessageMediaPathResolver.ResolveLocalVideoMediaPath(mediaContext, messageTime, segment);
            return new ResolvedSegmentMediaPath(
                resolved.VideoPath,
                !resolved.IsVideoAvailable,
                resolved.CoverPath,
                resolved.IsCoverAvailable);
        }

        if (MessageMediaSegmentClassifier.IsRenderableImageSegment(messageType, subMessageType, segment))
        {
            var localPath = MessageMediaPathResolver.ResolveLocalMediaPath(mediaContext, messageTime, segment, subMessageType);
            return ResolvedSegmentMediaPath.FromLocalPath(localPath);
        }

        return null;
    }

    private static string CreateUnavailableMediaText(MessageRecord item)
    {
        return CreateUnavailableMediaText(item.SubMessageType);
    }

    private static string CreateUnavailableMediaText(SubMessageType subMessageType)
    {
        return MessageMediaSegmentClassifier.IsStickerMessage(subMessageType)
            ? "[动画表情文件未找到]"
            : "[图片文件未找到]";
    }

    private delegate ResolvedSegmentMediaPath? TryResolveSegmentMediaPath(QQMessageSegment segment);

    private readonly record struct ResolvedSegmentMediaPath(
        string? LocalPath,
        bool IsMissing,
        string? CoverPath = null,
        bool IsCoverAvailable = false)
    {
        public static ResolvedSegmentMediaPath FromLocalPath(string? localPath)
        {
            return new ResolvedSegmentMediaPath(
                localPath,
                string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath));
        }
    }
}
