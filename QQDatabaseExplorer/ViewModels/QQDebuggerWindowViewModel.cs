using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;
using QQDatabaseKeyDump;

namespace QQDatabaseExplorer.ViewModels;

public partial class QQDebuggerWindowViewModel : ViewModelBase
{
    private readonly IDialogService _dialogService;

    public ViewModelToken ViewModelToken { get; } = new();

    [ObservableProperty]
    public partial string QQFilePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Key { get; set; } = string.Empty;

    public QQDebuggerWindowViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;

        HashSet<string> qqFilePaths = new();

        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Tencent\QQNT") is { } qqNTRegistry)
                {
                    if (qqNTRegistry.GetValue("Install") is string qqInstallDirectory)
                    {
                        qqFilePaths.Add(Path.Combine(qqInstallDirectory, "QQ.exe"));
                    }
                }
            }
            catch { }

            qqFilePaths.Add(Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "Tencent", "QQNT", "QQ.exe"));
            qqFilePaths.Add(@"C:\Program Files\Tencent\QQNT\QQ.exe");
            qqFilePaths.Add(@"D:\Program Files\Tencent\QQNT\QQ.exe");
        }

        foreach (var item in qqFilePaths)
        {
            if (File.Exists(item))
            {
                QQFilePath = item;
                break;
            }
        }
    }

    [RelayCommand]
    public async Task StartDebug()
    {
        if (!File.Exists(QQFilePath))
        {
            await _dialogService.ShowMessageBox("QQ不存在", "错误", ViewModelToken);
            return;
        }

        await _dialogService.ShowMessageBox("接下来打开QQ, 登录后会自动获取key", ownerToken: ViewModelToken);

        await Task.Run(() =>
        {
            Key = QQDebugger.NewProcess(QQFilePath) ?? string.Empty;
        });

        if (string.IsNullOrWhiteSpace(Key))
        {
            await _dialogService.ShowMessageBox("Key获取失败", "错误", ViewModelToken);
        }
    }

    [RelayCommand]
    public void Close()
    {
        _dialogService.Close(ViewModelToken);
    }
}
