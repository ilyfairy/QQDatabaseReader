using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using ObservableCollections;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Models.Messenger;
using QQDatabaseExplorer.Services;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels;

public partial class MessageTabViewModel : ViewModelBase, IRecipient<AddDatabaseMessage>, IRecipient<RemoveDatabaseMessage>
{
    private readonly QQDatabaseService _qqDatabaseService;
    private readonly IMessenger _messenger;
    private readonly ObservableList<AvaQQGroup> _groups = new();

    /// <summary>
    /// 当前在聊天栏中显示的消息
    /// </summary>
    private readonly ObservableRingBuffer<AvaQQMessage> _messages = new();



    public NotifyCollectionChangedSynchronizedViewList<AvaQQGroup> Groups { get; }
    public NotifyCollectionChangedSynchronizedViewList<AvaQQMessage> Messages { get; }


    [ObservableProperty]
    public partial AvaQQGroup? SelectedGroup { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingPrevious { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingNext { get; set; }

    private const int PageSize = 20;

    public int HistoryMessageSeq { get; set; }

    public MessageTabViewModel(QQDatabaseService qqDatabaseService, IMessenger messenger)
    {
        Groups = _groups.ToNotifyCollectionChanged();
        Messages = _messages.ToNotifyCollectionChanged();
        _qqDatabaseService = qqDatabaseService;
        _messenger = messenger;
        _messenger.Register<AddDatabaseMessage>(this);
        _messenger.Register<RemoveDatabaseMessage>(this);
    }


    partial void OnSelectedGroupChanged(AvaQQGroup? value)
    {
        if (value is null)
        {
            Messages.Clear();
        }
        else
        {
            LoadInitialMessages(value.GroupId);
        }
    }


    private void LoadInitialMessages(int groupId)
    {
        if (_qqDatabaseService.MessageDatabase is null)
            return;

        _messages.Clear();

        var messages = _qqDatabaseService.MessageDatabase.DbContext.GroupMessages
                .Where(v => v.GroupId == groupId)
                .OrderByDescending(v => v.MessageSeq)
                .Where(v => v.MessageType != MessageType.Empty)
                .OrderBy(v => v.MessageSeq)
                .Take(PageSize)
                .ToList();

        foreach (var item in messages)
        {
            _messages.AddLast(CreateAvaQQMessage(item));
        }
    }


    //public async Task LoadPreviousMessages()
    //{
    //    if (IsLoadingPrevious || SelectedGroup is null || _messageDatabase is null) 
    //        return;

    //    try
    //    {
    //        IsLoadingPrevious = true;

    //        // 获取当前最早消息的时间
    //        var earliestMessage = Messages.FirstOrDefault();
    //        if (earliestMessage is null) return;

    //        var earliestTime = earliestMessage.MessageTime;

    //        var previousMessages = _messageDatabase.DbContext.GroupMessages
    //            .Where(v => v.GroupId == SelectedGroup.GroupId)
    //            .Where(v => v.MessageType != MessageType.Empty)
    //            .Where(v => v.MessageTime < earliestTime)
    //            .OrderByDescending(v => v.MessageTime)
    //            .Take(PageSize)
    //            .OrderBy(v => v.MessageTime) // 正序排列
    //            .ToList();

    //        if (previousMessages.Any())
    //        {
    //            var avaMessages = previousMessages.Select(CreateAvaQQMessage);
    //            foreach (var item in avaMessages)
    //            {
    //                Messages.AddFirst(item);
    //            }
    //        }
    //    }
    //    finally
    //    {
    //        IsLoadingPrevious = false;
    //    }
    //}

    ///// <summary>
    ///// 加载更新的消息（用户向下滚动时调用）
    ///// </summary>
    //public async Task LoadNextMessages()
    //{
    //    if (IsLoadingNext || SelectedGroup is null || _messageDatabase is null) 
    //        return;

    //    try
    //    {
    //        IsLoadingNext = true;

    //        // 获取当前最新消息的时间
    //        var latestMessage = Messages.LastOrDefault();
    //        if (latestMessage is null) return;

    //        var latestTime = latestMessage.MessageTime;

    //        var nextMessages = _messageDatabase.DbContext.GroupMessages
    //            .Where(v => v.GroupId == SelectedGroup.GroupId)
    //            .Where(v => v.MessageType != MessageType.Empty)
    //            .Where(v => v.MessageTime > latestTime)
    //            .OrderBy(v => v.MessageTime)
    //            .Take(PageSize)
    //            .ToList();

    //        if (nextMessages.Any())
    //        {
    //            var avaMessages = nextMessages.Select(CreateAvaQQMessage);
    //            foreach (var item in avaMessages)
    //            {
    //                Messages.AddLast(item);
    //            }
    //        }
    //    }
    //    finally
    //    {
    //        IsLoadingNext = false;
    //    }
    //}

    /// <summary>
    /// 从数据库消息创建 AvaQQMessage
    /// </summary>
    private AvaQQMessage CreateAvaQQMessage(GroupMessage item)
    {
        var displayText = item.GetText();
        if (item.MessageType is MessageType.Reply)
        {
            displayText = $"[回复] {displayText}";
        }
        if (item.SubMessageType is SubMessageType.Sticker)
        {
            displayText = $"[动画表情]";
        }

        return new AvaQQMessage()
        {
            MessageId = item.MessageSeq, // 假设这是唯一ID
            DisplayText = displayText,
            Name = item.SendNickName | item.SendMemberName ?? string.Empty,
            MessageTime = item.MessageTime
        };
    }

    public void Receive(AddDatabaseMessage e)
    {
        if (e.Database is QQMessageReader messageDatabase)
        {
            // 创建Group列表, 但是只有群Id
            var groupIds = messageDatabase.DbContext.GroupMessages.Select(v => v.GroupId).Distinct();
            var newGroups = groupIds.Except(_groups.Select(v => v.GroupId))
                .Select(v => new AvaQQGroup() { GroupId = v });
            _groups.AddRange(newGroups);
        }
        else if (e.Database is QQGroupInfoReader groupDatabase)
        {
            var rawGroups = groupDatabase.DbContext.GroupList.ToList();

            // 给现有的Groups加上群名
            foreach (var item in _groups.Join(rawGroups,
                v => v.GroupId,
                v => v.GroupId,
                (group, rawGroup) => (group, rawGroup)))
            {
                item.group.GroupName = item.rawGroup.GroupName;
            }

            // 新增的Groups
            var newGroups = rawGroups.ExceptBy(_groups.Select(v => v.GroupId), v => v.GroupId)
                .Select(v => new AvaQQGroup()
                {
                    GroupId = v.GroupId,
                    GroupName = v.GroupName,
                });
            _groups.AddRange(newGroups);
        }
    }

    public void Receive(RemoveDatabaseMessage e)
    {
        if (e.Database is QQMessageReader)
        {
            _messages.Clear();
            if (_qqDatabaseService.GroupInfoDatabase is null)
            {
                _groups.Clear();
            }
        }
        else if (e.Database is QQGroupInfoReader)
        {
            if (_qqDatabaseService.MessageDatabase is null)
            {
                _groups.Clear();
            }
        }
    }
}
