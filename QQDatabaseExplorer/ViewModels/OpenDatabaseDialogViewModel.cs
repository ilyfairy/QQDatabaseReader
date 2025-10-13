using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Models.Messenger;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using QQDatabaseReader;
using QQNTDatabaseKeyFinder;
using Ursa.Controls;

namespace QQDatabaseExplorer.ViewModels;

public partial class OpenDatabaseDialogViewModel : ViewModelBase
{
    private readonly IMessenger _messenger;

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


    public OpenDatabaseDialogViewModel(IMessenger messenger)
    {
        _messenger = messenger;
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

    public void Ok()
    {
        IsOpen = true;
        _messenger.Send<CloseDatabaseDialogMessage>();
    }

    public void Cancel()
    {
        IsOpen = false;
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
            await MessageBox.ShowOverlayAsync("没有找到QQ.exe");
            return;
        }

        await MessageBox.ShowAsync("请登录QQ, 登录后会自动获取key");

        IReadOnlyCollection<string>? keys = null;
        await Task.Run(() =>
        {
            keys = QQDebugger.NewProcess(qqntFilePath);
        });

        var key = keys?.FirstOrDefault(v => v.Any(v => char.IsSymbol(v)));
        if(key == null)
        {
            await MessageBox.ShowOverlayAsync("Key获取失败");
        }
        else
        {
            Key = key;
        }
        
    }
}
