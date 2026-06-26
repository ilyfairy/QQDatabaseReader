using System;
using System.Linq;
using QQDatabaseExplorer.Controls;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class PCQQDisplayMessageFactory
{
    private readonly Func<bool> _alwaysShowMessageTime;
    private readonly Func<bool> _highlightMentions;
    private readonly Func<uint, string?> _resolvePcqqContactName;
    private readonly PCQQMessageSegmentFactory _segmentFactory;

    public PCQQDisplayMessageFactory(
        Func<bool> alwaysShowMessageTime,
        Func<bool> highlightMentions,
        Func<string?> getPcqqDataPath,
        Func<uint, string?> resolvePcqqContactName)
    {
        _alwaysShowMessageTime = alwaysShowMessageTime;
        _highlightMentions = highlightMentions;
        _resolvePcqqContactName = resolvePcqqContactName;
        _segmentFactory = new PCQQMessageSegmentFactory(getPcqqDataPath);
    }

    public AvaQQMessage Create(MessageRecord item, AvaQQGroup conversation)
    {
        var parsed = PCQQMessageContentParser.Parse(item.Content);
        var segments = _segmentFactory.Create(parsed);
        var senderName = FirstNonEmpty(item.SendMemberName, item.SendNickName);
        if (string.IsNullOrWhiteSpace(senderName) &&
            conversation.ConversationType == AvaConversationType.PCQQPrivate &&
            PrivateMessageParticipantMatcher.IsPrivatePeerMessage(item, conversation))
        {
            senderName = conversation.DisplayName;
        }

        if (string.IsNullOrWhiteSpace(senderName) && item.SenderId != 0)
        {
            senderName = item.SenderId.ToString();
        }

        if (!MessageTextSegmentBuilder.HasDisplayContent(segments))
        {
            segments.Clear();
            segments.Add(AvaQQMessageSegment.CreateUnsupportedText(parsed.DisplayText));
        }

        var reply = CreateReply(parsed);
        return new AvaQQMessage
        {
            MessageId = item.MessageId,
            MessageRandom = item.MessageRandom,
            MessageSeq = item.MessageSeq,
            PCQQMessageSeq = parsed.MessageSeq,
            GroupId = item.GroupId,
            ConversationType = conversation.ConversationType,
            ConversationKey = conversation.ConversationKey,
            PrivateConversationId = item.PrivateConversationId,
            PrivateUin = item.PeerUin,
            PeerUid = item.PeerUid,
            DisplayText = MessageTextSegmentBuilder.CreateDisplayText(segments),
            Segments = segments,
            Reply = reply,
            Name = senderName,
            MessageTime = item.MessageTime,
            SenderId = item.SenderId,
            ProtobufContent = item.Content,
            RawData = item.CreateRawData("PCQQ"),
            IsHoverTimeVisible = _alwaysShowMessageTime(),
            HighlightMentions = _highlightMentions(),
        };
    }

    private AvaReplyMessage? CreateReply(PCQQParsedMessage parsed)
    {
        if (parsed.Reply is null)
            return null;

        var previewText = parsed.Reply.PreviewText.Trim();
        if (string.IsNullOrWhiteSpace(previewText))
            return null;

        var senderName = parsed.Reply.SenderUin == 0
            ? null
            : _resolvePcqqContactName(parsed.Reply.SenderUin);
        return new AvaReplyMessage
        {
            MessageSeq = parsed.Reply.MessageSeq,
            SenderId = parsed.Reply.SenderUin,
            SenderName = FirstNonEmpty(senderName, parsed.Reply.SenderUin == 0 ? null : parsed.Reply.SenderUin.ToString()),
            Segments = MessageTextSegmentBuilder.CreateTextSegments(previewText).ToList(),
            PreviewText = previewText,
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}
