using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels;

public partial class ExportDatabaseDialogViewModel : ViewModelBase
{
    private readonly IDialogService _dialogService;

    public IQQDatabase? Database { get; set; }

    public string ExportFilePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double Progress { get; set; }
    
    public ViewModelToken ViewModelToken { get; } = new();

    public ExportDatabaseDialogViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
    }

    [RelayCommand]
    public async Task Export()
    {
        if (string.IsNullOrWhiteSpace(ExportFilePath))
        {
            await _dialogService.ShowMessageBox("请输入文件名", "错误", ViewModelToken);
            return;
        }

        if (Database is null)
        {
            await _dialogService.ShowMessageBox("数据库未选择", "错误", ViewModelToken);
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
        await _dialogService.ShowMessageBox("导出成功", ownerToken: ViewModelToken);
        _dialogService.Close(ViewModelToken);
    }
}
