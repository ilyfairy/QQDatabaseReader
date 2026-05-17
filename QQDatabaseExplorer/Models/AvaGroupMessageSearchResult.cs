using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QQDatabaseExplorer.Models;

public sealed class AvaGroupMessageSearchResult
{
    public long MessageId { get; init; }
    public long MessageSeq { get; init; }
    public int MessageTime { get; init; }
    public uint GroupId { get; init; }
    public string? GroupName { get; init; }
    public string? PeerUid { get; init; }
    public string? SenderUid { get; init; }
    public uint SenderId { get; init; }
    public string? SenderName { get; init; }
    public string PreviewText { get; init; } = string.Empty;

    public string GroupText
    {
        get
        {
            if (GroupId != 0)
                return GroupId.ToString();

            return string.IsNullOrWhiteSpace(PeerUid) ? "未知群" : PeerUid;
        }
    }

    public string GroupDisplayName => string.IsNullOrWhiteSpace(GroupName)
        ? GroupText
        : GroupName;

    public string GroupListDisplayName => ListDisplayText.SingleLine(GroupDisplayName, 54);

    public string GroupAvatarUrl => GroupId == 0
        ? string.Empty
        : $"https://p.qlogo.cn/gh/{GroupId}/{GroupId}/640/";

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

    public string GroupMetaText => GroupId == 0
        ? DetailText
        : $"{GroupId} | {DetailText}";

    public string GroupMetaDisplayText => ListDisplayText.SingleLine(GroupMetaText, 96);
}

public sealed class AvaGroupMessageSearchGroup : ObservableObject
{
    private int _matchCount;
    private bool _isCounting;

    public uint GroupId { get; init; }
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
            if (!string.IsNullOrWhiteSpace(GroupName))
                return GroupName;

            if (GroupId != 0)
                return GroupId.ToString();

            return string.IsNullOrWhiteSpace(PeerUid) ? "未知群" : PeerUid;
        }
    }

    public string ListDisplayName => ListDisplayText.SingleLine(DisplayName, 54);

    public string AvatarUrl => GroupId == 0
        ? string.Empty
        : $"https://p.qlogo.cn/gh/{GroupId}/{GroupId}/640/";

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
