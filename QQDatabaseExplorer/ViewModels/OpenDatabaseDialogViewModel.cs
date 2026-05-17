using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QQDatabaseReader;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;

namespace QQDatabaseExplorer.ViewModels;

public partial class OpenDatabaseDialogViewModel : ViewModelBase
{
    private readonly QQDatabaseService _qqDatabaseService;
    private readonly IDialogService _dialogService;

    [ObservableProperty]
    public partial DatabasePlatformType PlatformType { get; set; } = DatabasePlatformType.QQNT;

    [ObservableProperty]
    public partial string NtMessageDbPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NtGroupInfoDbPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NtGroupMessageFtsDbPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NtProfileInfoDbPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NtDataPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MobileQQPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Key { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GroupInfoDbKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GroupMessageFtsDbKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProfileInfoDbKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PCQQMessageDbPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PCQQInfoDbPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PCQQInfoDbKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PCQQDataPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NtUid { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Rand { get; set; } = string.Empty;

    public bool IsQQNT => PlatformType is DatabasePlatformType.QQNT;

    public bool IsAndroidQQNT => PlatformType is DatabasePlatformType.AndroidQQNT;

    public bool IsNtPlatform => PlatformType is DatabasePlatformType.QQNT or DatabasePlatformType.AndroidQQNT;

    public bool IsPCQQ => PlatformType is DatabasePlatformType.PCQQ;

    public string NtDataPathLabel => IsAndroidQQNT ? "MobileQQ:" : "nt_data:";

    public string NtDataPathToolTip => IsAndroidQQNT
        ? "[AndroidQQNT] MobileQQ 目录，用于读取本地图片"
        : "[QQNT] nt_data 目录，用于读取本地图片和图片表情";

    public string FindDatabaseKeyButtonText => IsPCQQ
        ? "登录PCQQ自动获取Key"
        : "登录QQ自动获取Key";

    public Dictionary<DatabasePlatformType, string> PlatformTypes { get; } = new()
    {
        { DatabasePlatformType.PCQQ, "PCQQ" },
        { DatabasePlatformType.QQNT, "QQNT" },
        { DatabasePlatformType.AndroidQQNT, "AndroidQQNT" },
    };

    public ViewModelToken ViewModelToken { get; } = new();

    public DatabaseConfig? ResultConfig { get; private set; }

    private bool _deferLoadToCaller;

    public OpenDatabaseDialogViewModel(QQDatabaseService qqDatabaseService, IDialogService dialogService)
    {
        _qqDatabaseService = qqDatabaseService;
        _dialogService = dialogService;
    }

    public void SetInitialDatabaseFile(string databaseFilePath)
    {
        var name = Path.GetFileNameWithoutExtension(databaseFilePath);
        if (string.Equals(name, "Msg3.0", StringComparison.OrdinalIgnoreCase))
        {
            PlatformType = DatabasePlatformType.PCQQ;
            PCQQMessageDbPath = databaseFilePath;
            EnsurePCQQDataPath(databaseFilePath);
            return;
        }

        PlatformType = DatabasePlatformType.QQNT;
        if (string.Equals(name, "nt_msg", StringComparison.OrdinalIgnoreCase))
        {
            NtMessageDbPath = databaseFilePath;
            EnsureNtDataPath(databaseFilePath);
        }
        else if (string.Equals(name, "group_info", StringComparison.OrdinalIgnoreCase))
        {
            NtGroupInfoDbPath = databaseFilePath;
        }
        else if (string.Equals(name, "group_msg_fts", StringComparison.OrdinalIgnoreCase))
        {
            NtGroupMessageFtsDbPath = databaseFilePath;
        }
        else if (string.Equals(name, "profile_info", StringComparison.OrdinalIgnoreCase))
        {
            NtProfileInfoDbPath = databaseFilePath;
        }
        else
        {
            NtMessageDbPath = databaseFilePath;
            EnsureNtDataPath(databaseFilePath);
        }
    }

    public void SetInitialPlatform(DatabasePlatformType platformType)
    {
        PlatformType = platformType;
    }

    public void SetInitialConfig(DatabaseConfig config)
    {
        _deferLoadToCaller = true;
        PlatformType = config.Type;
        switch (config.Type)
        {
            case DatabasePlatformType.PCQQ when config.PCQQ is { } pcqq:
                PCQQMessageDbPath = pcqq.MessageDbPath ?? string.Empty;
                Key = pcqq.MessageDbKey ?? string.Empty;
                PCQQInfoDbPath = pcqq.InfoDbPath ?? string.Empty;
                PCQQInfoDbKey = pcqq.InfoDbKey ?? string.Empty;
                PCQQDataPath = pcqq.DataPath ?? string.Empty;
                break;
            case DatabasePlatformType.AndroidQQNT when config.AndroidQQNT is { } android:
                SetQQNTFields(android);
                NtUid = android.NtUid ?? string.Empty;
                Rand = android.Rand ?? string.Empty;
                break;
            case DatabasePlatformType.QQNT when config.QQNT is { } qqnt:
                SetQQNTFields(qqnt);
                break;
        }
    }

    partial void OnPlatformTypeChanged(DatabasePlatformType value)
    {
        OnPropertyChanged(nameof(IsQQNT));
        OnPropertyChanged(nameof(IsAndroidQQNT));
        OnPropertyChanged(nameof(IsNtPlatform));
        OnPropertyChanged(nameof(IsPCQQ));
        OnPropertyChanged(nameof(NtDataPathLabel));
        OnPropertyChanged(nameof(NtDataPathToolTip));
        OnPropertyChanged(nameof(FindDatabaseKeyButtonText));
    }

    partial void OnNtMessageDbPathChanged(string value)
    {
        EnsureRand(value);
        EnsureNtDataPath(value);
    }

    partial void OnPCQQMessageDbPathChanged(string value)
    {
        EnsurePCQQDataPath(value);
    }

    partial void OnNtUidChanged(string value)
    {
        EnsureAndroidQQNTKey();
    }

    partial void OnRandChanged(string value)
    {
        EnsureAndroidQQNTKey();
    }

    public async Task Ok()
    {
        try
        {
            if (IsPCQQ)
            {
                if (!await ValidateRequiredFileAsync(PCQQMessageDbPath, "请输入 Msg3.0.db 路径", "Msg3.0.db 文件不存在"))
                    return;

                if (string.IsNullOrWhiteSpace(Key))
                {
                    await _dialogService.ShowMessageBox("PCQQ Msg3.0.db 需要填写密钥", "错误", ViewModelToken);
                    return;
                }

                ResultConfig = CreatePCQQConfig();
                if (!_deferLoadToCaller)
                    _qqDatabaseService.LoadDatabaseConfig(ResultConfig);
            }
            else
            {
                if (IsAndroidQQNT)
                    EnsureAndroidQQNTKey();

                if (!await ValidateNtDatabasesAsync())
                    return;

                ResultConfig = CreateQQNTConfig();
                if (!_deferLoadToCaller)
                    _qqDatabaseService.LoadDatabaseConfig(ResultConfig);
            }
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageBox($"打开异常: {ex}", "错误", ViewModelToken);
            return;
        }

        _dialogService.Close(ViewModelToken);
    }

    public void Cancel()
    {
        _dialogService.Close(ViewModelToken);
    }

    [RelayCommand]
    public async Task FindDatabaseKey()
    {
        if (IsPCQQ)
        {
            _dialogService.ShowPCQQKeyDumpWindow(ViewModelToken);
            return;
        }

        Key = await _dialogService.ShowQQDebuggerWindow(ViewModelToken);
    }

    private async Task<bool> ValidateNtDatabasesAsync()
    {
        if (string.IsNullOrWhiteSpace(NtMessageDbPath) &&
            string.IsNullOrWhiteSpace(NtGroupInfoDbPath) &&
            string.IsNullOrWhiteSpace(NtGroupMessageFtsDbPath) &&
            string.IsNullOrWhiteSpace(NtProfileInfoDbPath))
        {
            await _dialogService.ShowMessageBox("请至少填写一个 QQNT 数据库路径", "错误", ViewModelToken);
            return false;
        }

        return await ValidateOptionalFileAsync(NtGroupInfoDbPath, "group_info.db 文件不存在") &&
               await ValidateOptionalFileAsync(NtProfileInfoDbPath, "profile_info.db 文件不存在") &&
               await ValidateOptionalFileAsync(NtMessageDbPath, "nt_msg.db 文件不存在") &&
               await ValidateOptionalFileAsync(NtGroupMessageFtsDbPath, "group_msg_fts.db 文件不存在");
    }

    private async Task<bool> ValidateRequiredFileAsync(string path, string emptyMessage, string missingMessage)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            await _dialogService.ShowMessageBox(emptyMessage, "错误", ViewModelToken);
            return false;
        }

        return await ValidateOptionalFileAsync(path, missingMessage);
    }

    private async Task<bool> ValidateOptionalFileAsync(string path, string missingMessage)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;

        if (File.Exists(path))
            return true;

        await _dialogService.ShowMessageBox(missingMessage, "错误", ViewModelToken);
        return false;
    }

    private void EnsureAndroidQQNTKey()
    {
        if (IsAndroidQQNT && !string.IsNullOrWhiteSpace(NtUid) && !string.IsNullOrWhiteSpace(Rand))
            Key = RawDatabase.GetQQKey(NtUid, Rand);
    }

    private void EnsureRand(string databaseFilePath)
    {
        if (File.Exists(databaseFilePath) && RawDatabase.GetRand(databaseFilePath) is string rand)
            Rand = rand;
    }

    private void EnsureNtDataPath(string databaseFilePath)
    {
        if (IsAndroidQQNT)
            return;

        if (!string.IsNullOrWhiteSpace(NtDataPath))
            return;

        var directory = Path.GetDirectoryName(databaseFilePath);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (string.Equals(Path.GetFileName(directory), "nt_db", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(directory);
                if (parent is null)
                    return;

                var ntDataPath = Path.Combine(parent, "nt_data");
                if (Directory.Exists(ntDataPath))
                    NtDataPath = ntDataPath;

                return;
            }

            directory = Path.GetDirectoryName(directory);
        }
    }

    private void EnsurePCQQDataPath(string databaseFilePath)
    {
        if (!string.IsNullOrWhiteSpace(PCQQDataPath))
            return;

        var directory = Path.GetDirectoryName(databaseFilePath);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        if (Directory.Exists(Path.Combine(directory, "Image")))
            PCQQDataPath = directory;
    }

    private static string? EmptyToNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private void SetQQNTFields(QQNTDatabaseConfig config)
    {
        NtMessageDbPath = config.MessageDbPath ?? string.Empty;
        Key = config.MessageDbPassword ?? string.Empty;
        NtGroupInfoDbPath = config.GroupInfoDbPath ?? string.Empty;
        GroupInfoDbKey = config.GroupInfoDbPassword ?? string.Empty;
        NtGroupMessageFtsDbPath = config.GroupMessageFtsDbPath ?? string.Empty;
        GroupMessageFtsDbKey = config.GroupMessageFtsDbPassword ?? string.Empty;
        NtProfileInfoDbPath = config.ProfileInfoDbPath ?? string.Empty;
        ProfileInfoDbKey = config.ProfileInfoDbPassword ?? string.Empty;
        NtDataPath = config.NtDataPath ?? string.Empty;
        MobileQQPath = config is AndroidQQNTDatabaseConfig android
            ? android.MobileQQPath ?? string.Empty
            : string.Empty;
    }

    private DatabaseConfig CreatePCQQConfig()
    {
        return new DatabaseConfig
        {
            Type = DatabasePlatformType.PCQQ,
            PCQQ = new PCQQDatabaseConfig
            {
                MessageDbPath = EmptyToNull(PCQQMessageDbPath),
                MessageDbKey = EmptyToNull(Key),
                InfoDbPath = EmptyToNull(PCQQInfoDbPath),
                InfoDbKey = EmptyToNull(PCQQInfoDbKey),
                DataPath = EmptyToNull(PCQQDataPath),
            },
        };
    }

    private DatabaseConfig CreateQQNTConfig()
    {
        var config = new QQNTDatabaseConfig
        {
            NtDataPath = IsAndroidQQNT ? null : EmptyToNull(NtDataPath),
            MessageDbPath = EmptyToNull(NtMessageDbPath),
            MessageDbPassword = EmptyToNull(Key),
            GroupInfoDbPath = EmptyToNull(NtGroupInfoDbPath),
            GroupInfoDbPassword = EmptyToNull(GroupInfoDbKey),
            GroupMessageFtsDbPath = EmptyToNull(NtGroupMessageFtsDbPath),
            GroupMessageFtsDbPassword = EmptyToNull(GroupMessageFtsDbKey),
            ProfileInfoDbPath = EmptyToNull(NtProfileInfoDbPath),
            ProfileInfoDbPassword = EmptyToNull(ProfileInfoDbKey),
        };

        if (!IsAndroidQQNT)
        {
            return new DatabaseConfig
            {
                Type = DatabasePlatformType.QQNT,
                QQNT = config,
            };
        }

        return new DatabaseConfig
        {
            Type = DatabasePlatformType.AndroidQQNT,
            AndroidQQNT = new AndroidQQNTDatabaseConfig
            {
                NtDataPath = config.NtDataPath,
                MobileQQPath = EmptyToNull(MobileQQPath),
                MessageDbPath = config.MessageDbPath,
                MessageDbPassword = config.MessageDbPassword,
                GroupInfoDbPath = config.GroupInfoDbPath,
                GroupInfoDbPassword = config.GroupInfoDbPassword,
                GroupMessageFtsDbPath = config.GroupMessageFtsDbPath,
                GroupMessageFtsDbPassword = config.GroupMessageFtsDbPassword,
                ProfileInfoDbPath = config.ProfileInfoDbPath,
                ProfileInfoDbPassword = config.ProfileInfoDbPassword,
                NtUid = EmptyToNull(NtUid),
                Rand = EmptyToNull(Rand),
            },
        };
    }
}
