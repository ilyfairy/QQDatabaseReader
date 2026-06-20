using System;
using System.Linq;
using System.Text.Json;
using QQDatabaseExplorer.Controls;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class IcalinguaDisplayMessageFactory
{
    private readonly Func<bool> _alwaysShowMessageTime;
    private readonly Func<bool> _highlightMentions;
    private readonly IcalinguaMessageSegmentFactory _segmentFactory;
    private readonly IcalinguaReplyDisplayFactory _replyFactory;

    public IcalinguaDisplayMessageFactory(
        Func<bool> alwaysShowMessageTime,
        Func<bool> highlightMentions,
        IcalinguaMessageSegmentFactory segmentFactory,
        IcalinguaReplyDisplayFactory replyFactory)
    {
        _alwaysShowMessageTime = alwaysShowMessageTime;
        _highlightMentions = highlightMentions;
        _segmentFactory = segmentFactory;
        _replyFactory = replyFactory;
    }

    public static IcalinguaDisplayMessageFactory Create(
        Func<bool> alwaysShowMessageTime,
        Func<bool> highlightMentions,
        Func<IcalinguaMessageFile, AvaQQGroup, string?> resolveLocalFilePath,
        Func<IcalinguaMessageFile, string?, AvaQQGroup, string?> resolveVideoCoverPath,
        Func<AvaQQGroup, string, IcalinguaMessageRecord?> loadReplyTarget,
        Func<string?, bool> canPlayVoice,
        Func<string?, int?> getVoiceDurationMilliseconds)
    {
        var fileSegmentFactory = new IcalinguaMessageFileSegmentFactory(
            resolveLocalFilePath,
            resolveVideoCoverPath,
            canPlayVoice,
            getVoiceDurationMilliseconds);
        var segmentFactory = new IcalinguaMessageSegmentFactory(fileSegmentFactory);
        var replyFactory = new IcalinguaReplyDisplayFactory(
            segmentFactory,
            loadReplyTarget);

        return new IcalinguaDisplayMessageFactory(
            alwaysShowMessageTime,
            highlightMentions,
            segmentFactory,
            replyFactory);
    }

    public AvaQQMessage Create(MessageRecord item, AvaQQGroup conversation)
    {
        var payload = IcalinguaMessagePayload.FromContent(item.Content);
        var segments = _segmentFactory.Create(payload, conversation, forSystemHint: payload?.System == true);
        if (!MessageTextSegmentBuilder.HasDisplayContent(segments))
        {
            segments.Clear();
            segments.Add(AvaQQMessageSegment.CreateUnsupportedText(FirstNonEmpty(payload?.PreviewText, "[Icalingua消息]")));
        }

        var senderName = FirstNonEmpty(item.SendMemberName, item.SendNickName, item.SenderId == 0 ? null : item.SenderId.ToString());
        var reply = _replyFactory.Create(payload, conversation);
        var displayText = MessageTextSegmentBuilder.CreateDisplayText(segments);
        var isDeleted = payload?.Deleted == true && payload.Reveal != true;
        var isHidden = payload?.Hide == true && payload.Reveal != true;
        var isSystemHint = payload?.System == true;

        return new AvaQQMessage
        {
            MessageId = item.MessageId,
            MessageRandom = item.MessageRandom,
            MessageSeq = item.MessageSeq,
            GroupId = item.GroupId,
            ConversationType = conversation.ConversationType,
            ConversationKey = conversation.ConversationKey,
            PrivateConversationId = item.PrivateConversationId,
            DisplayText = displayText,
            Segments = segments,
            Reply = reply,
            IsRecalledMessage = (isDeleted || isHidden) && !isSystemHint,
            IsSystemHint = isSystemHint,
            Name = senderName,
            MessageTime = item.MessageTime,
            SenderId = item.SenderId,
            CachedAvatarUrl = ResolveAvatarUrl(payload),
            ProtobufContent = null,
            IsHoverTimeVisible = _alwaysShowMessageTime(),
            HighlightMentions = _highlightMentions(),
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string? ResolveAvatarUrl(IcalinguaMessagePayload? payload)
    {
        if (payload is null)
            return null;

        if (!string.IsNullOrWhiteSpace(payload.MiraiJson))
        {
            try
            {
                using var document = JsonDocument.Parse(payload.MiraiJson);
                if (TryGetMiraiEqqValue(document.RootElement, "avatarMd5") is { Length: > 0 } avatarMd5)
                    return "https://gchat.qpic.cn/gchatpic_new/0/0-0-" + avatarMd5.ToUpperInvariant() + "/0";

                if (TryGetMiraiEqqValue(document.RootElement, "avatarUrl") is { Length: > 0 } avatarUrl)
                    return avatarUrl;
            }
            catch
            {
            }
        }

        return string.IsNullOrWhiteSpace(payload.HeadImage) ? null : payload.HeadImage;
    }

    private static string? TryGetMiraiEqqValue(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("eqq", out var eqq) ||
            eqq.ValueKind != JsonValueKind.Object ||
            !eqq.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }
}
