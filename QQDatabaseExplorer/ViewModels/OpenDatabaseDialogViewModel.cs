using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using QQDatabaseReader;
using QQDatabaseKeyFinder;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.Models.Messenger;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.ViewModels;

public partial class OpenDatabaseDialogViewModel : ViewModelBase
{
    private readonly IMessenger _messenger;
    private readonly QQDatabaseService _qqDatabaseService;

    [ObservableProperty]
    public partial string DatabaseFilePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Key { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NtUid { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Rand { get; set; } = string.Empty;

    [ObservableProperty]
    public partial QQDatabaseType DatabaseType { get; set; } = QQDatabaseType.Message;

    public Dictionary<QQDatabaseType, string> DatabaseTypes { get; } = new()
    {
        { QQDatabaseType.Message, "nt_msg.db" },
        { QQDatabaseType.GroupInfo, "group_info.db" },
    };

    public bool IsOpen { get; set; }
    public MessageBoxToken MessageBoxToken { get; }

    public OpenDatabaseDialogViewModel(IMessenger messenger, QQDatabaseService qqDatabaseService, MessageBoxToken messageBoxToken)
    {
        _messenger = messenger;
        _qqDatabaseService = qqDatabaseService;
        MessageBoxToken = messageBoxToken;
    }

    partial void OnDatabaseFilePathChanged(string value)
    {
        if (File.Exists(value))
        {
            if (RawDatabase.GetRand(value) is string rand)
            {
                Rand = rand;
            }
        }

        var name = Path.GetFileNameWithoutExtension(value);
        if (string.Equals(name, "nt_msg", StringComparison.OrdinalIgnoreCase))
        {
            DatabaseType = QQDatabaseType.Message;
        }
        else if (string.Equals(name, "group_info", StringComparison.OrdinalIgnoreCase))
        {
            DatabaseType = QQDatabaseType.GroupInfo;
        }
    }


    partial void OnNtUidChanged(string value)
    {
        EnsureKey();
    }

    partial void OnRandChanged(string value)
    {
        EnsureKey();
    }


    public void EnsureKey()
    {
        if (!string.IsNullOrWhiteSpace(NtUid) && !string.IsNullOrWhiteSpace(Rand))
        {
            Key = RawDatabase.GetQQKey(NtUid, Rand);
        }
    }

    public async Task Ok()
    {
        if (string.IsNullOrWhiteSpace(DatabaseFilePath))
        {
            await _messenger.Send(new ShowMessageBoxMessage("请输入文件名", "错误", MessageBoxToken, new TaskCompletionSource())).TaskCompletionSource.Task;
            return;
        }

        if (!File.Exists(DatabaseFilePath))
        {
            await _messenger.Send(new ShowMessageBoxMessage("文件不存在", "错误", MessageBoxToken, new TaskCompletionSource())).TaskCompletionSource.Task;
            return;
        }

        EnsureKey();

        if (DatabaseType is QQDatabaseType.Message)
        {
            if (string.IsNullOrWhiteSpace(Key))
            {
                _qqDatabaseService.LoadMessageDatabase(DatabaseFilePath);
            }
            else
            {
                _qqDatabaseService.LoadMessageDatabase(DatabaseFilePath, Key);
            }
        }
        else if (DatabaseType is QQDatabaseType.GroupInfo)
        {
            if (string.IsNullOrWhiteSpace(Key))
            {
                _qqDatabaseService.LoadGroupInfoDatabase(DatabaseFilePath);
            }
            else
            {
                _qqDatabaseService.LoadGroupInfoDatabase(DatabaseFilePath, Key);
            }
        }
        _messenger.Send<CloseDatabaseDialogMessage>();
    }

    public void Cancel()
    {
        _messenger.Send<CloseDatabaseDialogMessage>();
    }

    [RelayCommand]
    public async Task FindDatabaseKey()
    {
        HashSet<string> qqFilePaths =
        [
            Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "Tencent", "QQNT", "QQ.exe"),
            @"C:\Program Files\Tencent\QQNT\QQ.exe",
            @"D:\Program Files\Tencent\QQNT\QQ.exe",
        ];
        string? qqntFilePath = null;
        foreach (var item in qqFilePaths)
        {
            if (File.Exists(item))
            {
                qqntFilePath = item;
                break;
            }
        }

        if (qqntFilePath == null)
        {
            await _messenger.Send(new ShowMessageBoxMessage("没有找到QQ.exe", "错误", MessageBoxToken, new TaskCompletionSource())).TaskCompletionSource.Task;
            return;
        }

        await _messenger.Send(new ShowMessageBoxMessage("请登录QQ, 登录后会自动获取key", null, MessageBoxToken, new TaskCompletionSource())).TaskCompletionSource.Task;

        string? key = null;
        await Task.Run(() =>
        {
            key = QQDebugger.NewProcess(qqntFilePath);
        });

        if(key == null)
        {
            await _messenger.Send(new ShowMessageBoxMessage("Key获取失败", "错误", MessageBoxToken, new TaskCompletionSource())).TaskCompletionSource.Task;
        }
        else
        {
            Key = key;
        }
        
    }
}
