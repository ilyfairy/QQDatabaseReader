using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using QQDatabaseExplorer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using QQDatabaseReader;

namespace QQDatabaseExplorer.Views;

public partial class MainView : UserControl
{
    private readonly IServiceProvider _serviceProvider;

    public MainViewModel ViewModel { get; }

    public MainView(MainViewModel mainViewModel, IServiceProvider serviceProvider)
    {
        ViewModel = mainViewModel;
        _serviceProvider = serviceProvider;
        DataContext = mainViewModel;
        InitializeComponent();
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

    private async void UserControl_Drop(object? sender, DragEventArgs e)
    {
        var file = e.DataTransfer.TryGetFiles()?.FirstOrDefault();
        if (file is { })
        {
            if (!File.Exists(file.Path.LocalPath))
                return;

            using var scope = _serviceProvider.CreateScope();
            var dialog = scope.ServiceProvider.GetRequiredService<OpenDatabaseDialog>();
            var vm = scope.ServiceProvider.GetRequiredService<OpenDatabaseDialogViewModel>();
            vm.DatabaseFilePath = file.Path.LocalPath;

            await dialog.ShowDialog(TopLevel.GetTopLevel(this) as Window);

            if (!vm.IsOpen)
                return;

            if (string.IsNullOrWhiteSpace(vm.Key))
            {
                if(!string.IsNullOrWhiteSpace(vm.NtUid) && !string.IsNullOrWhiteSpace(vm.Rand))
                {
                    vm.Key = RawDatabase.GetQQKey(vm.NtUid, vm.Rand);
                }
            }

            if (vm.DatabaseType is Models.QQDatabaseType.Message)
            {
                if (string.IsNullOrWhiteSpace(vm.Key))
                {
                    ViewModel.OpenMessageDatabase(vm.DatabaseFilePath);
                }
                else
                {
                    ViewModel.OpenMessageDatabase(vm.DatabaseFilePath, vm.Key);
                }
            }
            else if (vm.DatabaseType is Models.QQDatabaseType.GroupInfo)
            {
                if (string.IsNullOrWhiteSpace(vm.Key))
                {
                    ViewModel.OpenGroupInfoDatabase(vm.DatabaseFilePath);
                }
                else
                {
                    ViewModel.OpenGroupInfoDatabase(vm.DatabaseFilePath, vm.Key);
                }
            }
        }
    }
}
