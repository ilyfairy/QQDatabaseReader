using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QQDatabaseExplorer.Models;

public sealed class AvaGroupMessageSearchResult
{
    public long MessageId { get; init; }
    public long MessageSeq { get; init; }
    public int MessageTime { get; init; }
    public uint GroupId { get; init; }
    public long PrivateConversationId { get; init; }
    public uint PeerUin { get; init; }
    public long IcalinguaRoomId { get; init; }
    public string? GroupName { get; init; }
    public string? PeerUid { get; init; }
    public string? SenderUid { get; init; }
    public uint SenderId { get; init; }
    public string? SenderName { get; init; }
    public string PreviewText { get; init; } = string.Empty;

    public bool CanLocate => MessageSeq > 0 && (GroupId != 0 || PrivateConversationId != 0 || IcalinguaRoomId != 0);

    public string ConversationTypeText
    {
        get
        {
            if (GroupId != 0)
                return "群";

            if (PrivateConversationId != 0)
                return "好友";

            if (IcalinguaRoomId < 0)
                return "群";

            if (IcalinguaRoomId > 0)
                return "好友";

            return string.Empty;
        }
    }

    public string GroupText
    {
        get
        {
            if (GroupId != 0)
                return GroupId.ToString();

            if (PrivateConversationId != 0)
            {
                if (PeerUin != 0)
                    return PeerUin.ToString();

                if (!string.IsNullOrWhiteSpace(PeerUid))
                    return PeerUid;

                return PrivateConversationId.ToString();
            }

            if (IcalinguaRoomId != 0)
                return GetIcalinguaDisplayId(IcalinguaRoomId);

            return string.IsNullOrWhiteSpace(PeerUid) ? "未知群" : PeerUid;
        }
    }

    public string GroupDisplayName
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(GroupName)
                ? GroupText
                : GroupName;
            return string.IsNullOrWhiteSpace(ConversationTypeText)
                ? text
                : $"{ConversationTypeText} {text}";
        }
    }

    public string GroupListDisplayName => ListDisplayText.SingleLine(GroupDisplayName, 54);

    public string GroupAvatarUrl
    {
        get
        {
            if (GroupId != 0)
                return $"https://p.qlogo.cn/gh/{GroupId}/{GroupId}/640/";

            if (PrivateConversationId != 0 && PeerUin != 0)
                return $"http://q1.qlogo.cn/g?b=qq&nk={PeerUin}&s=100";

            if (IcalinguaRoomId < 0)
                return $"https://p.qlogo.cn/gh/{-IcalinguaRoomId}/{-IcalinguaRoomId}/640/";

            if (IcalinguaRoomId > 0)
                return $"http://q1.qlogo.cn/g?b=qq&nk={IcalinguaRoomId}&s=100";

            return string.Empty;
        }
    }

    public string? SenderAvatarUrl => SenderId == 0
        ? null
        : $"http://q1.qlogo.cn/g?b=qq&nk={SenderId}&s=100";

    public string SenderText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SenderName))
                return SenderName;

            if (SenderId != 0)
                return SenderId.ToString();

            return string.IsNullOrWhiteSpace(SenderUid)
                ? "未知发送者"
                : SenderUid;
        }
    }

    public string SenderDisplayText => ListDisplayText.SingleLine(SenderText, 48);

    public string MessageTimeText
    {
        get
        {
            if (MessageTime <= 0)
                return string.Empty;

            var messageTime = DateTimeOffset.FromUnixTimeSeconds(MessageTime).LocalDateTime;
            var now = DateTime.Now;
            if (messageTime.Date == now.Date)
                return messageTime.ToString("HH:mm:ss");

            return messageTime.Year == now.Year
                ? messageTime.ToString("MM/dd HH:mm:ss")
                : messageTime.ToString("yyyy/MM/dd HH:mm:ss");
        }
    }

    public string DetailText => $"{SenderText} | seq {MessageSeq}";

    public string GroupMetaText => GroupId == 0 && PrivateConversationId == 0 && IcalinguaRoomId == 0
        ? DetailText
        : $"{GroupDisplayName} | {DetailText}";

    public string GroupMetaDisplayText => ListDisplayText.SingleLine(GroupMetaText, 96);

    private static string GetIcalinguaDisplayId(long roomId)
    {
        return roomId < 0
            ? (-roomId).ToString()
            : roomId.ToString();
    }
}

public sealed class AvaGroupMessageSearchGroup : ObservableObject
{
    private int _matchCount;
    private bool _isCounting;

    public uint GroupId { get; init; }
    public long PrivateConversationId { get; init; }
    public uint PeerUin { get; init; }
    public long IcalinguaRoomId { get; init; }
    public string? PeerUid { get; init; }
    public string? GroupName { get; init; }
    public string QueryText { get; init; } = string.Empty;

    public int MatchCount
    {
        get => _matchCount;
        set
        {
            if (SetProperty(ref _matchCount, value))
            {
                OnPropertyChanged(nameof(SummaryText));
                OnPropertyChanged(nameof(ListSummaryText));
            }
        }
    }

    public bool IsCounting
    {
        get => _isCounting;
        set
        {
            if (SetProperty(ref _isCounting, value))
            {
                OnPropertyChanged(nameof(SummaryText));
                OnPropertyChanged(nameof(ListSummaryText));
            }
        }
    }

    public string DisplayName
    {
        get
        {
            if (GroupId != 0)
                return string.IsNullOrWhiteSpace(GroupName) ? $"群 {GroupId}" : $"群 {GroupName}";

            if (PrivateConversationId != 0)
            {
                var text = !string.IsNullOrWhiteSpace(GroupName)
                    ? GroupName
                    : PeerUin != 0
                        ? PeerUin.ToString()
                        : !string.IsNullOrWhiteSpace(PeerUid)
                            ? PeerUid
                            : PrivateConversationId.ToString();
                return $"好友 {text}";
            }

            if (IcalinguaRoomId != 0)
            {
                var text = !string.IsNullOrWhiteSpace(GroupName)
                    ? GroupName
                    : IcalinguaRoomId < 0
                        ? (-IcalinguaRoomId).ToString()
                        : IcalinguaRoomId.ToString();
                return IcalinguaRoomId < 0 ? $"群 {text}" : $"好友 {text}";
            }

            return string.IsNullOrWhiteSpace(PeerUid) ? "未知群" : PeerUid;
        }
    }

    public string ListDisplayName => ListDisplayText.SingleLine(DisplayName, 54);

    public string AvatarUrl
    {
        get
        {
            if (GroupId != 0)
                return $"https://p.qlogo.cn/gh/{GroupId}/{GroupId}/640/";

            if (PrivateConversationId != 0 && PeerUin != 0)
                return $"http://q1.qlogo.cn/g?b=qq&nk={PeerUin}&s=100";

            if (IcalinguaRoomId < 0)
                return $"https://p.qlogo.cn/gh/{-IcalinguaRoomId}/{-IcalinguaRoomId}/640/";

            if (IcalinguaRoomId > 0)
                return $"http://q1.qlogo.cn/g?b=qq&nk={IcalinguaRoomId}&s=100";

            return string.Empty;
        }
    }

    public string GroupAvatarUrl => AvatarUrl;

    public string SummaryText
    {
        get
        {
            if (IsCounting)
            {
                return string.IsNullOrWhiteSpace(QueryText)
                    ? "正在统计相关记录"
                    : $"正在统计与“{QueryText}”相关的记录";
            }

            return string.IsNullOrWhiteSpace(QueryText)
                ? $"{MatchCount} 条相关记录"
                : $"{MatchCount} 条与“{QueryText}”相关的记录";
        }
    }

    public string ListSummaryText => ListDisplayText.SingleLine(SummaryText, 72);
}
