using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using QQDatabaseExplorer.ViewModels;
using QQDatabaseExplorer.Services;
using CommunityToolkit.Mvvm.Messaging;
using QQDatabaseExplorer.Models.Messenger;

namespace QQDatabaseExplorer.Views;

public partial class MainView : UserControl
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IMessenger _messenger;

    public MainView(MainViewModel viewModel, IServiceProvider serviceProvider, IMessenger messenger, MessageTab messageTab, DatabaseTab databaseTab, ViewModelTokenService viewModelTokenService)
    {
        viewModelTokenService.AutoRegister(viewModel.ViewModelToken, this);
        DataContext = viewModel;
        _serviceProvider = serviceProvider;
        _messenger = messenger;
        InitializeComponent();

        this.messageTab.Content = messageTab;
        this.databaseTab.Content = databaseTab;
    }


    //// 滚动事件处理
    //private async void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    //{
    //    if (sender is not ScrollViewer scrollViewer) return;

    //    const double threshold = 50; // 距离边缘的阈值

    //    // 检查是否滚动到顶部附近（加载历史消息）
    //    if (scrollViewer.Offset.Y <= threshold && !ViewModel.IsLoadingPrevious)
    //    {
    //        // 记录当前滚动位置
    //        var currentOffset = scrollViewer.Offset.Y;
    //        var currentExtentHeight = scrollViewer.Extent.Height;

    //        await ViewModel.LoadPreviousMessages();

    //        // 加载完成后，保持用户的相对位置
    //        // 新消息被插入到顶部，所以需要调整滚动位置
    //        var newExtentHeight = scrollViewer.Extent.Height;
    //        var heightDifference = newExtentHeight - currentExtentHeight;

    //        if (heightDifference > 0)
    //        {
    //            // 调整滚动位置以保持视觉连续性
    //            await Task.Delay(50); // 稍等一下让UI更新完成
    //            scrollViewer.Offset = scrollViewer.Offset.WithY(currentOffset + heightDifference);
    //        }
    //    }

    //    // 检查是否滚动到底部附近（加载新消息）
    //    else if (scrollViewer.Offset.Y >= scrollViewer.Extent.Height - scrollViewer.Viewport.Height - threshold 
    //             && !ViewModel.IsLoadingNext)
    //    {
    //        await ViewModel.LoadNextMessages();
    //    }
    //}


    private void UserControl_Drop(object? sender, DragEventArgs e)
    {
        var file = e.DataTransfer.TryGetFiles()?.FirstOrDefault();
        if (file is { })
        {
            var filePath = file.Path.LocalPath;
            if (!File.Exists(filePath))
                return;

            _messenger.Send(new ShowOpenDatabaseDialogMessage(filePath, null, new()));
        }
    }
}
