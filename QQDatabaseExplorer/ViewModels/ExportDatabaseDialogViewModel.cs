using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Models.Messenger;
using QQDatabaseExplorer.Services;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels;

public partial class ExportDatabaseDialogViewModel : ViewModelBase
{
    private readonly IMessenger _messenger;
    private readonly MessageBoxService _messageBoxService;

    public IQQDatabase? Database { get; set; }

    public string ExportFilePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double Progress { get; set; }
    public ViewModelToken MessageBoxToken { get; }

    public ViewModelToken ViewModelToken { get; } = new();

    public ExportDatabaseDialogViewModel(IMessenger messenger, ViewModelToken messageBoxToken, MessageBoxService messageBoxService)
    {
        _messenger = messenger;
        MessageBoxToken = messageBoxToken;
        _messageBoxService = messageBoxService;
    }

    [RelayCommand]
    public async Task Export()
    {
        if (string.IsNullOrWhiteSpace(ExportFilePath))
        {
            await _messageBoxService.ShowAsync("请输入文件名", "错误", MessageBoxToken);
            return;
        }

        if (!File.Exists(ExportFilePath))
        {
            await _messageBoxService.ShowAsync("文件不存在", "错误", MessageBoxToken);
            return;
        }

        if (Database is null)
        {
            await _messageBoxService.ShowAsync("数据库未选择", "错误", MessageBoxToken);
            return;
        }

        long totalSize = 0;
        await Task.Run(() =>
        {
            Database.RawDatabase.ExportToNewDatabase(ExportFilePath, reportTotalRows: v => totalSize = v, progress: new Progress<long>(v =>
            {
                var progress = (double)v / totalSize * 100;
                if(progress >= 100)
                {
                    progress = 99;
                }
                Progress = progress;
            }));
        });
        Progress = 100;
        await _messageBoxService.ShowAsync("导出成功", null, MessageBoxToken);
        _messenger.Send<CloseExportDatabaseDialogMessage>();
        
    }
}
