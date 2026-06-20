using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Controls;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class IcalinguaReplyDisplayFactory
{
    private readonly IcalinguaMessageSegmentFactory _segmentFactory;
    private readonly Func<AvaQQGroup, string, IcalinguaMessageRecord?> _loadReplyTarget;

    public IcalinguaReplyDisplayFactory(
        IcalinguaMessageSegmentFactory segmentFactory,
        Func<AvaQQGroup, string, IcalinguaMessageRecord?> loadReplyTarget)
    {
        _segmentFactory = segmentFactory;
        _loadReplyTarget = loadReplyTarget;
    }

    public AvaReplyMessage? Create(IcalinguaMessagePayload? payload, AvaQQGroup conversation)
    {
        if (!ShouldShowReply(payload) || payload?.Reply is not { } reply)
            return null;

        var replySegments = CreatePreviewSegments(reply, conversation);
        var previewText = MessageTextSegmentBuilder.CreateDisplayText(replySegments).Trim();
        if (string.IsNullOrWhiteSpace(previewText))
            return null;

        var stableId = string.IsNullOrWhiteSpace(reply.RawId)
            ? 0
            : IcalinguaMessageReader.CreateStableNumericId(reply.RawId);
        return new AvaReplyMessage
        {
            MessageId = stableId,
            MessageRandom = stableId,
            RawMessageId = reply.RawId,
            MessageSeq = reply.MessageSortTime,
            SenderId = reply.SenderId,
            SenderName = FirstNonEmpty(reply.SenderName, reply.SenderId == 0 ? null : reply.SenderId.ToString()),
            MessageTime = reply.MessageTime,
            Segments = replySegments,
            PreviewText = previewText,
        };
    }

    private static bool ShouldShowReply(IcalinguaMessagePayload? payload)
    {
        if (payload?.Reply is null)
            return false;

        return payload.Reveal || payload.Deleted != true && payload.Hide != true;
    }

    private List<AvaQQMessageSegment> CreatePreviewSegments(
        IcalinguaReplyPreview reply,
        AvaQQGroup conversation)
    {
        if (TryCreatePreviewSegmentsFromTarget(reply, conversation) is { Count: > 0 } targetSegments)
            return targetSegments;

        var segments = new List<AvaQQMessageSegment>();
        IcalinguaMessageTextSegmentFactory.AddTextSegments(segments, reply.PreviewText.Trim());
        foreach (var file in reply.Files)
        {
            var displayText = IcalinguaMessageFileSegmentFactory.CreatePreviewText(file);
            if (segments.Count == 0 || !segments.Any(segment => string.Equals(segment.DisplayText, displayText, StringComparison.Ordinal)))
                segments.Add(AvaQQMessageSegment.CreateText(displayText));
        }

        return segments;
    }

    private List<AvaQQMessageSegment>? TryCreatePreviewSegmentsFromTarget(
        IcalinguaReplyPreview reply,
        AvaQQGroup conversation)
    {
        if (conversation.IcalinguaRoomId == 0 ||
            string.IsNullOrWhiteSpace(reply.RawId))
        {
            return null;
        }

        var targetMessage = _loadReplyTarget(conversation, reply.RawId);
        if (targetMessage is null)
            return null;

        var targetPayload = IcalinguaMessagePayload.FromContent(IcalinguaMessagePayload.ToContentBytes(targetMessage));
        var targetSegments = _segmentFactory.Create(
            targetPayload,
            conversation,
            forSystemHint: targetPayload?.System == true);
        if (MessageTextSegmentBuilder.HasDisplayContent(targetSegments))
            return targetSegments;

        if (!string.IsNullOrWhiteSpace(targetPayload?.PreviewText))
        {
            var previewSegments = new List<AvaQQMessageSegment>();
            IcalinguaMessageTextSegmentFactory.AddTextSegments(previewSegments, targetPayload.PreviewText);
            if (previewSegments.Count > 0)
                return previewSegments;
        }

        return null;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}
