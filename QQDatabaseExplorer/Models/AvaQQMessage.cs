using System;
using System.Collections.Generic;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QQDatabaseExplorer.Models;

public partial class AvaQQMessage : ObservableObject
{
    private byte[]? _protobufContent;
    private string? _protobufBase64;

    public string DisplayText { get; set; } = string.Empty;
    public IReadOnlyList<AvaQQMessageSegment> Segments { get; set; } = [];
    public IReadOnlyList<AvaQQMessage> ForwardedMessages { get; set; } = [];
    public AvaReplyMessage? Reply { get; set; }
    public int MessageTime { get; set; }
    public long MessageId { get; set; }
    public long MessageRandom { get; set; }
    public long MessageSeq { get; set; }
    public long PCQQMessageSeq { get; set; }
    public uint SenderId { get; set; }
    public uint GroupId { get; set; }
    public AvaConversationType ConversationType { get; set; } = AvaConversationType.Group;
    public string ConversationKey { get; set; } = string.Empty;
    public long PrivateConversationId { get; set; }
    public uint PrivateUin { get; set; }
    public string? PeerUid { get; set; }
    public string? CachedAvatarLocalPath { get; set; }
    public string? CachedAvatarUrl { get; set; }
    public bool IsRecalledMessage { get; set; }
    public bool IsSystemHint { get; set; }
    public string SystemHintSourceName { get; set; } = string.Empty;
    public string SystemHintSourceUin { get; set; } = string.Empty;
    public bool SystemHintSourceIsUser { get; set; } = true;
    public string SystemHintTargetName { get; set; } = string.Empty;
    public string SystemHintTargetUin { get; set; } = string.Empty;
    public bool SystemHintTargetIsUser { get; set; }
    public string SystemHintAction { get; set; } = string.Empty;
    public string SystemHintSuffix { get; set; } = string.Empty;
    public string? SystemHintActionImageUrl { get; set; }
    public long SystemHintTargetMessageSeq { get; set; }
    public int SystemHintFaceId { get; set; }
    public string? SystemHintFaceAssetPath { get; set; }
    public IReadOnlyList<AvaMessageReaction> Reactions { get; set; } = [];

    public byte[]? ProtobufContent
    {
        get => _protobufContent;
        set
        {
            if (SetProperty(ref _protobufContent, value))
            {
                _protobufBase64 = null;
                OnPropertyChanged(nameof(ProtobufBase64));
            }
        }
    }

    public string? ProtobufBase64 => ProtobufContent is null
        ? null
        : _protobufBase64 ??= Convert.ToBase64String(ProtobufContent);

    public string? AvatarUrl => !string.IsNullOrWhiteSpace(CachedAvatarUrl)
        ? CachedAvatarUrl
        : SenderId == 0
            ? null
            : $"http://q1.qlogo.cn/g?b=qq&nk={SenderId}&s=100";

    public string? AvatarLocalPath => CachedAvatarLocalPath;

    public bool IsForwardedCardOnly =>
        Segments.Count == 1 && Segments[0].Type == AvaQQMessageSegmentType.ForwardedMessage;

    public bool IsCardOnly =>
        Segments.Count == 1 &&
        Segments[0].Type is AvaQQMessageSegmentType.ForwardedMessage
            or AvaQQMessageSegmentType.SharedContact
            or AvaQQMessageSegmentType.MiniApp;

    public bool IsVoiceOnly =>
        Segments.Count == 1 && Segments[0].Type == AvaQQMessageSegmentType.Voice;

    public bool IsVideoOnly =>
        Segments.Count == 1 && Segments[0].Type == AvaQQMessageSegmentType.Video;

    public bool HasReply => Reply is not null;

    public bool CanSelect => !IsSystemHint;

    public bool IsNormalMessage => !IsSystemHint;

    public bool HasSystemHintActionImage => !string.IsNullOrWhiteSpace(SystemHintActionImageUrl);

    public bool HasSystemHintAction => !string.IsNullOrWhiteSpace(SystemHintAction);

    public bool HasSystemHintUserSourceName => !string.IsNullOrWhiteSpace(SystemHintSourceName) && SystemHintSourceIsUser;

    public bool HasSystemHintSourceQqId => !string.IsNullOrWhiteSpace(SystemHintSourceUin);

    public bool HasSystemHintResolvedSourceName => !string.IsNullOrWhiteSpace(SystemHintSourceName) && HasSystemHintSourceQqId;

    public bool HasSystemHintPlainSourceName => !string.IsNullOrWhiteSpace(SystemHintSourceName) && !SystemHintSourceIsUser && !HasSystemHintSourceQqId;

    public bool HasSystemHintTargetName => !string.IsNullOrWhiteSpace(SystemHintTargetName);

    public bool HasSystemHintUserTargetName => HasSystemHintTargetName && SystemHintTargetIsUser;

    public bool HasSystemHintPlainTargetName => HasSystemHintTargetName && !SystemHintTargetIsUser;

    public bool HasSystemHintSuffix => !string.IsNullOrWhiteSpace(SystemHintSuffix);

    public bool HasSystemHintFace => !string.IsNullOrWhiteSpace(SystemHintFaceAssetPath);

    public bool CanJumpToSystemHintTarget => SystemHintTargetMessageSeq > 0;

    public bool HasReactions => Reactions.Count > 0;

    public double MessageMinHeight => IsSystemHint ? 28 : 42;

    public Thickness MessageContentPadding => IsCardOnly
        ? new Thickness(0)
        : IsVideoOnly
            ? new Thickness(6)
        : IsVoiceOnly
            ? new Thickness(10, 4)
            : new Thickness(10, 8);

    public string HoverTimeText
    {
        get
        {
            if (MessageTime <= 0)
                return string.Empty;

            var messageTime = DateTimeOffset.FromUnixTimeSeconds(MessageTime).LocalDateTime;
            var now = DateTime.Now;
            return messageTime.Year == now.Year
                ? messageTime.ToString("MM-dd HH:mm:ss")
                : messageTime.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }

    [ObservableProperty]
    public partial bool IsHoverTimeVisible { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsJumpHighlightVisible { get; set; }
}
