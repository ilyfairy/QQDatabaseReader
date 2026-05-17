using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QQDatabaseExplorer.Models;

public partial class AvaQQGroup : ObservableObject
{
    [ObservableProperty]
    public partial AvaConversationType ConversationType { get; set; } = AvaConversationType.Group;

    [ObservableProperty]
    public partial uint GroupId { get; set; }

    [ObservableProperty]
    public partial long PrivateConversationId { get; set; }

    [ObservableProperty]
    public partial uint PrivateUin { get; set; }

    [ObservableProperty]
    public partial string? PrivateUid { get; set; }

    [ObservableProperty]
    public partial string? PCQQTableName { get; set; }

    [ObservableProperty]
    public partial bool PCQQHasInfo { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    public string ConversationKey => ConversationType switch
    {
        AvaConversationType.Group => $"group:{GroupId}",
        AvaConversationType.Private => $"private:{PrivateConversationId}",
        AvaConversationType.PCQQGroup => $"pcqq-group:{GroupId}",
        AvaConversationType.PCQQPrivate => $"pcqq-private:{PrivateUin}",
        _ => $"{ConversationType}:{GroupId}:{PrivateConversationId}",
    };

    public string DisplayName
    {
        get
        {
            var fallback = ConversationType == AvaConversationType.Private
                ? PrivateUin != 0 ? PrivateUin.ToString() : PrivateUid
                : ConversationType == AvaConversationType.PCQQPrivate
                    ? PrivateUin != 0 ? PrivateUin.ToString() : null
                    : GroupId != 0 ? GroupId.ToString() : null;
            return GroupName | fallback ?? string.Empty;
        }
    }

    public string ListDisplayName => ListDisplayText.SingleLine(DisplayName, 48);

    public string ListLatestMessageText => ListDisplayText.SingleLine(LatestMessageText, 96);

    public string? AvatarUrl => ConversationType switch
    {
        AvaConversationType.Group => GroupId == 0 ? null : $"https://p.qlogo.cn/gh/{GroupId}/{GroupId}/640/",
        AvaConversationType.PCQQGroup => !PCQQHasInfo || GroupId == 0 ? null : $"https://p.qlogo.cn/gh/{GroupId}/{GroupId}/640/",
        AvaConversationType.Private or AvaConversationType.PCQQPrivate => PrivateUin == 0 ? null : $"http://q1.qlogo.cn/g?b=qq&nk={PrivateUin}&s=100",
        _ => null,
    };

    public string LatestMessageTimeText
    {
        get
        {
            if (LatestMessageTime <= 0)
                return string.Empty;

            var messageTime = DateTimeOffset.FromUnixTimeSeconds(LatestMessageTime).LocalDateTime;
            var now = DateTime.Now;
            if (messageTime.Date == now.Date)
                return messageTime.ToString("HH:mm");

            return messageTime.Year == now.Year
                ? messageTime.ToString("MM/dd HH:mm")
                : messageTime.ToString("yyyy/MM/dd HH:mm");
        }
    }

    [ObservableProperty]
    public partial string? GroupName { get; set; }

    [ObservableProperty]
    public partial string LatestMessageText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int LatestMessageTime { get; set; }

    partial void OnGroupIdChanged(uint value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ListDisplayName));
        OnPropertyChanged(nameof(AvatarUrl));
        OnPropertyChanged(nameof(ConversationKey));
    }

    partial void OnConversationTypeChanged(AvaConversationType value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ListDisplayName));
        OnPropertyChanged(nameof(AvatarUrl));
        OnPropertyChanged(nameof(ConversationKey));
    }

    partial void OnPrivateConversationIdChanged(long value)
    {
        OnPropertyChanged(nameof(ConversationKey));
    }

    partial void OnPrivateUinChanged(uint value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ListDisplayName));
        OnPropertyChanged(nameof(AvatarUrl));
    }

    partial void OnPrivateUidChanged(string? value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ListDisplayName));
    }

    partial void OnPCQQTableNameChanged(string? value)
    {
        OnPropertyChanged(nameof(ConversationKey));
    }

    partial void OnPCQQHasInfoChanged(bool value)
    {
        OnPropertyChanged(nameof(AvatarUrl));
    }

    partial void OnGroupNameChanged(string? value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ListDisplayName));
    }

    partial void OnLatestMessageTextChanged(string value)
    {
        OnPropertyChanged(nameof(ListLatestMessageText));
    }

    partial void OnLatestMessageTimeChanged(int value)
    {
        OnPropertyChanged(nameof(LatestMessageTimeText));
    }
}

public enum AvaConversationType
{
    Group,
    Private,
    PCQQGroup,
    PCQQPrivate,
}
