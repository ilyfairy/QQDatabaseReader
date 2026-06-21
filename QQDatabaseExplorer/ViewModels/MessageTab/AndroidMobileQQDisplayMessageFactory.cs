using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Controls;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class AndroidMobileQQDisplayMessageFactory
{
    private readonly Func<bool> _alwaysShowMessageTime;
    private readonly Func<bool> _highlightMentions;
    private readonly Func<string?> _getMobileQQPath;
    private readonly Func<string?> _getChatPicPath;

    public AndroidMobileQQDisplayMessageFactory(
        Func<bool> alwaysShowMessageTime,
        Func<bool> highlightMentions,
        Func<string?> getMobileQQPath,
        Func<string?> getChatPicPath)
    {
        _alwaysShowMessageTime = alwaysShowMessageTime;
        _highlightMentions = highlightMentions;
        _getMobileQQPath = getMobileQQPath;
        _getChatPicPath = getChatPicPath;
    }

    public AvaQQMessage Create(MessageRecord item, AvaQQGroup conversation)
    {
        var payload = AndroidMobileQQMessagePayload.FromContent(item.Content);
        var segments = CreateSegments(payload, _getMobileQQPath(), _getChatPicPath());
        if (!MessageTextSegmentBuilder.HasDisplayContent(segments))
        {
            segments.Clear();
            segments.Add(AvaQQMessageSegment.CreateUnsupportedText("[旧版AndroidQQ消息]"));
        }

        var displayText = MessageTextSegmentBuilder.CreateDisplayText(segments);
        return new AvaQQMessage
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
            DisplayText = displayText,
            Segments = segments,
            Name = FirstNonEmpty(item.SendMemberName, item.SendNickName, item.SenderId == 0 ? null : item.SenderId.ToString()),
            MessageTime = item.MessageTime,
            SenderId = item.SenderId,
            ProtobufContent = null,
            IsHoverTimeVisible = _alwaysShowMessageTime(),
            HighlightMentions = _highlightMentions(),
        };
    }

    private static List<AvaQQMessageSegment> CreateSegments(
        AndroidMobileQQMessagePayload? payload,
        string? mobileQQPath,
        string? chatPicPath)
    {
        if (payload is null)
            return [];

        var segments = new List<AvaQQMessageSegment>();
        foreach (var part in payload.Parts)
        {
            switch (part.Type)
            {
                case AndroidMobileQQMessagePartType.Text:
                    segments.AddRange(MessageTextSegmentBuilder.CreateTextSegments(part.Text));
                    break;
                case AndroidMobileQQMessagePartType.Face when part.FaceId is { } faceId:
                    var faceSegment = AvaQQMessageSegment.CreateQQFace(faceId);
                    segments.Add(string.IsNullOrWhiteSpace(faceSegment.FaceAssetPath)
                        ? AvaQQMessageSegment.CreateText(faceSegment.DisplayText)
                        : faceSegment);
                    break;
                case AndroidMobileQQMessagePartType.Image:
                    segments.Add(CreateImageSegment(part, mobileQQPath, chatPicPath));
                    break;
                default:
                    segments.Add(AvaQQMessageSegment.CreateUnsupportedText(FirstNonEmpty(part.Text, payload.DisplayText)));
                    break;
            }
        }

        if (segments.Count == 0 && !string.IsNullOrWhiteSpace(payload.DisplayText))
            segments.AddRange(MessageTextSegmentBuilder.CreateTextSegments(payload.DisplayText));

        return segments;
    }

    private static AvaQQMessageSegment CreateImageSegment(
        AndroidMobileQQMessagePart part,
        string? mobileQQPath,
        string? chatPicPath)
    {
        var localPath = AndroidMobileQQMediaPathResolver.ResolveImagePath(mobileQQPath, chatPicPath, part.ImageMd5);
        var imageSize = LocalImageFile.TryGetImageSize(localPath);
        return string.IsNullOrWhiteSpace(localPath)
            ? AvaQQMessageSegment.CreateBrokenImage(null, null, "[图片文件未找到]")
            : AvaQQMessageSegment.CreateImage(
                localPath,
                imageSize?.Width,
                imageSize?.Height,
                "[图片]");
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}
