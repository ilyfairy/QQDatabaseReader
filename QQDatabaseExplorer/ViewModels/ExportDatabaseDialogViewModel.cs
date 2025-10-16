
using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Models.Messenger;
using QQDatabaseReader.Database;
using Ursa.Controls;

namespace QQDatabaseExplorer.ViewModels;

public partial class ExportDatabaseDialogViewModel : ViewModelBase
{
    private readonly IMessenger _messenger;

    public IQQDatabase? Database { get; set; }

    public string ExportFilePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double Progress { get; set; }
    public MessageBoxToken MessageBoxToken { get; }

    public ExportDatabaseDialogViewModel(IMessenger messenger, MessageBoxToken messageBoxToken)
    {
        _messenger = messenger;
        MessageBoxToken = messageBoxToken;
    }

    [RelayCommand]
    public async Task Export()
    {
        if (string.IsNullOrWhiteSpace(ExportFilePath))
        {
            await _messenger.Send(new ShowMessageBoxMessage("请输入文件名", "错误", MessageBoxToken, new TaskCompletionSource())).TaskCompletionSource.Task;
            return;
        }

        if (!File.Exists(ExportFilePath))
        {
            await _messenger.Send(new ShowMessageBoxMessage("文件不存在", "错误", MessageBoxToken, new TaskCompletionSource())).TaskCompletionSource.Task;
            return;
        }

        if (Database is null)
        {
            await _messenger.Send(new ShowMessageBoxMessage("数据库未选择", "错误", MessageBoxToken, new TaskCompletionSource())).TaskCompletionSource.Task;
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
        await _messenger.Send(new ShowMessageBoxMessage("导出成功", null, MessageBoxToken, new TaskCompletionSource())).TaskCompletionSource.Task;
        _messenger.Send<CloseExportDatabaseDialogMessage>();
    }
}
