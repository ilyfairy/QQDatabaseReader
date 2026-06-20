using System.Collections.Generic;
using QQDatabaseExplorer.Controls;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal static class QqNtDisplayMessageBuilder
{
    public static AvaQQMessage Create(
        MessageRecord item,
        AvaQQGroup conversation,
        List<AvaQQMessageSegment> segments,
        IReadOnlyList<AvaQQMessage> forwardedMessages,
        AvaReplyMessage? reply,
        IReadOnlyList<AvaMessageReaction> reactions,
        RecalledOriginalMessage? recalledMessage,
        SystemHintDisplay? systemHint,
        string senderName,
        string? cachedAvatarLocalPath,
        bool alwaysShowMessageTime,
        bool highlightMentions)
    {
        var message = new AvaQQMessage
        {
            MessageId = item.MessageId,
            MessageRandom = item.MessageRandom,
            MessageSeq = item.MessageSeq,
            GroupId = item.GroupId,
            ConversationType = conversation.ConversationType,
            ConversationKey = conversation.ConversationKey,
            PrivateConversationId = item.PrivateConversationId,
            PrivateUin = item.PeerUin,
            PeerUid = item.PeerUid,
            DisplayText = MessageTextSegmentBuilder.CreateDisplayText(segments),
            Segments = segments,
            ForwardedMessages = forwardedMessages,
            Reply = reply,
            Reactions = reactions,
            IsRecalledMessage = recalledMessage is not null,
            Name = senderName,
            MessageTime = item.MessageTime,
            SenderId = item.SenderId,
            CachedAvatarLocalPath = cachedAvatarLocalPath,
            ProtobufContent = item.Content,
            IsHoverTimeVisible = alwaysShowMessageTime,
            HighlightMentions = highlightMentions,
        };

        if (systemHint is not null && recalledMessage is null)
            ApplySystemHint(message, systemHint.Value);

        return message;
    }

    private static void ApplySystemHint(AvaQQMessage message, SystemHintDisplay systemHint)
    {
        message.IsSystemHint = true;
        message.SystemHintSourceName = systemHint.SourceName;
        message.SystemHintSourceUin = systemHint.SourceUin;
        message.SystemHintSourceIsUser = systemHint.SourceIsUser;
        message.SystemHintTargetName = systemHint.TargetName;
        message.SystemHintTargetUin = systemHint.TargetUin;
        message.SystemHintTargetIsUser = systemHint.TargetIsUser;
        message.SystemHintAction = systemHint.Action;
        message.SystemHintSuffix = systemHint.Suffix;
        message.SystemHintActionImageUrl = systemHint.ActionImageUrl;
        message.SystemHintTargetMessageSeq = systemHint.TargetMessageSeq;
        message.SystemHintFaceId = systemHint.FaceId;
        message.SystemHintFaceAssetPath = systemHint.FaceAssetPath;
        message.DisplayText = systemHint.DisplayText;
        message.Segments = [AvaQQMessageSegment.CreateText(systemHint.DisplayText)];
        message.Reply = null;
        message.Reactions = [];
    }
}
