
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Win32;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Models.Messenger;
using QQDatabaseExplorer.Services;
using QQDatabaseKeyFinder;

namespace QQDatabaseExplorer.ViewModels;

public partial class QQDebuggerWindowViewModel : ViewModelBase
{
    private readonly IMessenger _messenger;
    private readonly MessageBoxService _messageBoxService;

    public ViewModelToken ViewModelToken { get; } = new();

    [ObservableProperty]
    public partial string QQFilePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Key { get; set; } = string.Empty;

    public QQDebuggerWindowViewModel(IMessenger messenger, MessageBoxService messageBoxService)
    {
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

        _messenger = messenger;
        _messageBoxService = messageBoxService;
    }


    [RelayCommand]
    public async Task StartDebug()
    {
        if (!File.Exists(QQFilePath))
        {
            await _messageBoxService.ShowAsync("QQ不存在", "错误", ViewModelToken);
            return;
        }

        await _messageBoxService.ShowAsync("接下来请登录QQ, 登录后会自动获取key", null, ViewModelToken);

        await Task.Run(() =>
        {
            Key = QQDebugger.NewProcess(QQFilePath) ?? string.Empty;
        });

        if (string.IsNullOrWhiteSpace(Key))
        {
            await _messageBoxService.ShowAsync("Key获取失败", "错误", ViewModelToken);
        }
    }

    [RelayCommand]
    public void Close()
    {
        _messenger.Send<CloseQQDebuggerWindowMessage>();
    }
}
