using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QQDatabaseExplorer.Models;

public sealed class AvaGroupMessageSearchResult
{
    public AvaConversationType ConversationType { get; init; } = AvaConversationType.Group;
    public long MessageId { get; init; }
    public long MessageSeq { get; init; }
    public int MessageTime { get; init; }
    public uint GroupId { get; init; }
    public long PrivateConversationId { get; init; }
    public uint PeerUin { get; init; }
    public long IcalinguaRoomId { get; init; }
    public string? PCQQTableName { get; init; }
    public string? AndroidMobileQQTableName { get; init; }
    public string? AndroidMobileQQPeerUin { get; init; }
    public string? GroupName { get; init; }
    public string? PeerUid { get; init; }
    public string? SenderUid { get; init; }
    public uint SenderId { get; init; }
    public string? SenderName { get; init; }
    public string PreviewText { get; init; } = string.Empty;

    public bool CanLocate => MessageSeq > 0 && ConversationType switch
    {
        AvaConversationType.Group => GroupId != 0,
        AvaConversationType.Private => PrivateConversationId != 0,
        AvaConversationType.PCQQGroup => GroupId != 0 && !string.IsNullOrWhiteSpace(PCQQTableName),
        AvaConversationType.PCQQPrivate => PeerUin != 0 && !string.IsNullOrWhiteSpace(PCQQTableName),
        AvaConversationType.AndroidMobileQQGroup or AvaConversationType.AndroidMobileQQPrivate =>
            !string.IsNullOrWhiteSpace(AndroidMobileQQPeerUin) &&
            !string.IsNullOrWhiteSpace(AndroidMobileQQTableName),
        AvaConversationType.Icalingua => IcalinguaRoomId != 0,
        _ => false,
    };

    public string ConversationTypeText
    {
        get
        {
            return ConversationType switch
            {
                AvaConversationType.Group or AvaConversationType.PCQQGroup or AvaConversationType.AndroidMobileQQGroup => "群",
                AvaConversationType.Private or AvaConversationType.PCQQPrivate or AvaConversationType.AndroidMobileQQPrivate => "好友",
                AvaConversationType.Icalingua when IcalinguaRoomId < 0 => "群",
                AvaConversationType.Icalingua when IcalinguaRoomId > 0 => "好友",
                _ => string.Empty,
            };
        }
    }

    public string GroupText
    {
        get
        {
            return ConversationType switch
            {
                AvaConversationType.Group or AvaConversationType.PCQQGroup => GroupId == 0 ? "未知群" : GroupId.ToString(),
                AvaConversationType.Private => GetPrivateDisplayId(),
                AvaConversationType.PCQQPrivate => PeerUin == 0 ? "未知好友" : PeerUin.ToString(),
                AvaConversationType.AndroidMobileQQGroup or AvaConversationType.AndroidMobileQQPrivate =>
                    string.IsNullOrWhiteSpace(AndroidMobileQQPeerUin) ? "未知会话" : AndroidMobileQQPeerUin,
                AvaConversationType.Icalingua => GetIcalinguaDisplayId(IcalinguaRoomId),
                _ => string.IsNullOrWhiteSpace(PeerUid) ? "未知会话" : PeerUid,
            };
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
            return ConversationType switch
            {
                AvaConversationType.Group or AvaConversationType.PCQQGroup when GroupId != 0 =>
                    $"https://p.qlogo.cn/gh/{GroupId}/{GroupId}/640/",
                AvaConversationType.Private or AvaConversationType.PCQQPrivate when PeerUin != 0 =>
                    $"http://q1.qlogo.cn/g?b=qq&nk={PeerUin}&s=100",
                AvaConversationType.AndroidMobileQQGroup when !string.IsNullOrWhiteSpace(AndroidMobileQQPeerUin) =>
                    $"https://p.qlogo.cn/gh/{AndroidMobileQQPeerUin}/{AndroidMobileQQPeerUin}/640/",
                AvaConversationType.AndroidMobileQQPrivate when !string.IsNullOrWhiteSpace(AndroidMobileQQPeerUin) =>
                    $"http://q1.qlogo.cn/g?b=qq&nk={AndroidMobileQQPeerUin}&s=100",
                AvaConversationType.Icalingua when IcalinguaRoomId < 0 =>
                    $"https://p.qlogo.cn/gh/{-IcalinguaRoomId}/{-IcalinguaRoomId}/640/",
                AvaConversationType.Icalingua when IcalinguaRoomId > 0 =>
                    $"http://q1.qlogo.cn/g?b=qq&nk={IcalinguaRoomId}&s=100",
                _ => string.Empty,
            };
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

    private string GetPrivateDisplayId()
    {
        if (PeerUin != 0)
            return PeerUin.ToString();

        if (!string.IsNullOrWhiteSpace(PeerUid))
            return PeerUid;

        return PrivateConversationId == 0 ? "未知好友" : PrivateConversationId.ToString();
    }
}

public sealed class AvaGroupMessageSearchGroup : ObservableObject
{
    private int _matchCount;
    private bool _isCounting;

    public AvaConversationType ConversationType { get; init; } = AvaConversationType.Group;
    public uint GroupId { get; init; }
    public long PrivateConversationId { get; init; }
    public uint PeerUin { get; init; }
    public long IcalinguaRoomId { get; init; }
    public string? PCQQTableName { get; init; }
    public string? AndroidMobileQQTableName { get; init; }
    public string? AndroidMobileQQPeerUin { get; init; }
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
            return ConversationType switch
            {
                AvaConversationType.Group or AvaConversationType.PCQQGroup => FirstNonEmpty(GroupName, GroupId == 0 ? null : GroupId.ToString()),
                AvaConversationType.Private => GetPrivateDisplayId(),
                AvaConversationType.PCQQPrivate => FirstNonEmpty(GroupName, PeerUin == 0 ? null : PeerUin.ToString()),
                AvaConversationType.AndroidMobileQQGroup => FirstNonEmpty(GroupName, AndroidMobileQQPeerUin),
                AvaConversationType.AndroidMobileQQPrivate => FirstNonEmpty(GroupName, AndroidMobileQQPeerUin),
                AvaConversationType.Icalingua when IcalinguaRoomId < 0 =>
                    FirstNonEmpty(GroupName, (-IcalinguaRoomId).ToString()),
                AvaConversationType.Icalingua when IcalinguaRoomId > 0 =>
                    FirstNonEmpty(GroupName, IcalinguaRoomId.ToString()),
                _ => FirstNonEmpty(GroupName, PeerUid, "未知会话"),
            };
        }
    }

    public string ListDisplayName => ListDisplayText.SingleLine(DisplayName, 54);

    public string AvatarUrl
    {
        get
        {
            return ConversationType switch
            {
                AvaConversationType.Group or AvaConversationType.PCQQGroup when GroupId != 0 =>
                    $"https://p.qlogo.cn/gh/{GroupId}/{GroupId}/640/",
                AvaConversationType.Private or AvaConversationType.PCQQPrivate when PeerUin != 0 =>
                    $"http://q1.qlogo.cn/g?b=qq&nk={PeerUin}&s=100",
                AvaConversationType.AndroidMobileQQGroup when !string.IsNullOrWhiteSpace(AndroidMobileQQPeerUin) =>
                    $"https://p.qlogo.cn/gh/{AndroidMobileQQPeerUin}/{AndroidMobileQQPeerUin}/640/",
                AvaConversationType.AndroidMobileQQPrivate when !string.IsNullOrWhiteSpace(AndroidMobileQQPeerUin) =>
                    $"http://q1.qlogo.cn/g?b=qq&nk={AndroidMobileQQPeerUin}&s=100",
                AvaConversationType.Icalingua when IcalinguaRoomId < 0 =>
                    $"https://p.qlogo.cn/gh/{-IcalinguaRoomId}/{-IcalinguaRoomId}/640/",
                AvaConversationType.Icalingua when IcalinguaRoomId > 0 =>
                    $"http://q1.qlogo.cn/g?b=qq&nk={IcalinguaRoomId}&s=100",
                _ => string.Empty,
            };
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

    private string GetPrivateDisplayId()
    {
        return FirstNonEmpty(
            GroupName,
            PeerUin == 0 ? null : PeerUin.ToString(),
            PeerUid,
            PrivateConversationId == 0 ? null : PrivateConversationId.ToString(),
            "未知好友");
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
