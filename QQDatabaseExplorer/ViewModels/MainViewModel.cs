using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using QQDatabaseExplorer.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using ObservableCollections;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial int TabIndex { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<QQGroup> Groups { get; set; } = new();

    public NotifyCollectionChangedSynchronizedViewList<AvaQQMessage> Messages { get; }


    /// <summary>
    /// 当前在聊天栏中显示的消息
    /// </summary>
    private readonly ObservableRingBuffer<AvaQQMessage> _messages = new();

    [ObservableProperty]
    public partial QQGroup? SelectedGroup { get; set; }

    private QQGroupInfoReader? _groupInfoDatabase = null;
    private QQMessageReader? _messageDatabase = null;
    
    [ObservableProperty]
    public partial bool IsLoadingPrevious { get; set; }
    
    [ObservableProperty]
    public partial bool IsLoadingNext { get; set; }
    
    private const int PageSize = 20;

    public int HistoryMessageSeq { get; set; }

    public MainViewModel()
    {
        Messages = _messages.ToNotifyCollectionChanged();
    }

    partial void OnSelectedGroupChanged(QQGroup? value)
    {
        if (value is null || _messageDatabase is null)
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
        if (_messageDatabase is null) return;

        _messages.Clear();
        
        var messages = _messageDatabase.DbContext.GroupMessages
            .Where(v => v.GroupId == groupId)
            .OrderByDescending(v => v.MessageSeq)
            .Where(v => v.MessageType != MessageType.Empty)
            .Take(PageSize)
            .OrderBy(v => v.MessageSeq)
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

    public void OpenGroupInfoDatabase(string grouInfoDb, string? password = null)
    {
        _groupInfoDatabase?.Dispose();
        if (password is null)
        {
            _groupInfoDatabase = new(grouInfoDb);
        }
        else
        {
            _groupInfoDatabase = new(grouInfoDb, password);
        }
        _groupInfoDatabase.Initialize();
        Groups = new(_groupInfoDatabase.DbContext.GroupList);
    }

    public void OpenMessageDatabase(string messageDb, string? password = null)
    {
        _messageDatabase?.Dispose();
        if (password is null)
        {
            _messageDatabase = new(messageDb);
        }
        else
        {
            _messageDatabase = new(messageDb, password);
        }
        _messageDatabase.Initialize();
    }

}
