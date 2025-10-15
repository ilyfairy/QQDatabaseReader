
using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
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

    public ExportDatabaseDialogViewModel(IMessenger messenger)
    {
        _messenger = messenger;
    }

    [RelayCommand]
    public async Task Export()
    {
        if (string.IsNullOrWhiteSpace(ExportFilePath))
        {
            await MessageBox.ShowAsync("请输入文件名");
            return;
        }
        if(Database is null)
        {
            await MessageBox.ShowAsync("数据库未选择");
            return;
        }

        ExportFilePath = ExportFilePath.Trim();
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
        await MessageBox.ShowAsync("导出成功");
        _messenger.Send<CloseExportDatabaseDialogMessage>();
    }
}
