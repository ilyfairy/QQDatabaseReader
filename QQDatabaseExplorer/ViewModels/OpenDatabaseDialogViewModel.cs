using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using QQDatabaseReader;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.Models.Messenger;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.ViewModels;

public partial class OpenDatabaseDialogViewModel : ViewModelBase
{
    private readonly IMessenger _messenger;
    private readonly QQDatabaseService _qqDatabaseService;
    private readonly IServiceProvider _serviceProvider;

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

    public ViewModelToken ViewModelToken { get; }

    public OpenDatabaseDialogViewModel(IMessenger messenger, QQDatabaseService qqDatabaseService, ViewModelToken messageBoxToken, IServiceProvider serviceProvider)
    {
        _messenger = messenger;
        _qqDatabaseService = qqDatabaseService;
        ViewModelToken = messageBoxToken;
        _serviceProvider = serviceProvider;
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
            await _messenger.Send(new ShowMessageBoxMessage("请输入文件名", "错误", ViewModelToken, new TaskCompletionSource())).Completion.Task;
            return;
        }

        if (!File.Exists(DatabaseFilePath))
        {
            await _messenger.Send(new ShowMessageBoxMessage("文件不存在", "错误", ViewModelToken, new TaskCompletionSource())).Completion.Task;
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
        var resultViewModel = await _messenger.Send(new ShowQQDebuggerWindowMessage(ViewModelToken, new TaskCompletionSource<QQDebuggerWindowViewModel>()))
            .Completion.Task;
        Key = resultViewModel.Key;
    }
}
