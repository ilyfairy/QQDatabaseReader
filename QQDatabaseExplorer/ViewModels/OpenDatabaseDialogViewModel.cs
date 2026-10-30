using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QQDatabaseReader;
using QQDatabaseReader.Archives;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;

namespace QQDatabaseExplorer.ViewModels;

public partial class OpenDatabaseDialogViewModel : ViewModelBase
{
    private const string NtMessageDbFileName = "nt_msg.db";
    private const string NtGroupInfoDbFileName = "group_info.db";
    private const string NtGroupMessageFtsDbFileName = "group_msg_fts.db";
    private const string NtBuddyMessageFtsDbFileName = "buddy_msg_fts.db";
    private const string NtProfileInfoDbFileName = "profile_info.db";
    private const string PCQQMessageDbFileName = "Msg3.0.db";
    private const string PCQQInfoDbFileName = "Info.db";
    private const string IcalinguaDatabasePrefix = "eqq";

    private readonly IDialogService _dialogService;
    private bool _syncingNtKeys;

    [ObservableProperty]
    public partial DatabasePlatformType PlatformType { get; set; } = DatabasePlatformType.QQNT;

    [ObservableProperty]
    public partial string NtMessageDbPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NtGroupInfoDbPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NtGroupMessageFtsDbPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NtBuddyMessageFtsDbPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NtProfileInfoDbPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NtDataPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MobileQQPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ChatPicPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AndroidMobileQQRootPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AndroidMobileQQSelfUin { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AndroidMobileQQMediaPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AndroidMobileQQChatPicPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Key { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GroupInfoDbKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GroupMessageFtsDbKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BuddyMessageFtsDbKey { get; set; } = string.Empty;

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
    public partial string IcalinguaDatabasePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IcalinguaDataPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NtUid { get; set; } = string.Empty;

    /// <summary>选择 nt_msg.db 时按数据库头部自动检测, 加载已保存的配置时以配置为准</summary>
    [ObservableProperty]
    public partial QQNtHmacAlgorithm CipherHmacAlgorithm { get; set; } = QQNtHmacAlgorithm.HmacSha1;

    [ObservableProperty]
    public partial string Rand { get; set; } = string.Empty;

    public bool IsQQNT => PlatformType is DatabasePlatformType.QQNT;

    public bool IsAndroidQQNT => PlatformType is DatabasePlatformType.AndroidQQNT;

    public bool IsAndroidMobileQQ => PlatformType is DatabasePlatformType.AndroidMobileQQ;

    public bool IsNtPlatform => PlatformType is DatabasePlatformType.QQNT or DatabasePlatformType.AndroidQQNT;

    public bool IsPCQQ => PlatformType is DatabasePlatformType.PCQQ;

    public bool IsIcalingua => PlatformType is DatabasePlatformType.Icalingua;

    public bool CanFindDatabaseKey => PlatformType is DatabasePlatformType.QQNT or DatabasePlatformType.PCQQ;

    public string NtDataPathLabel => IsAndroidQQNT ? "MobileQQ:" : "nt_data:";

    public string NtDataPathToolTip => IsAndroidQQNT
        ? "MobileQQ 目录，用于读取本地媒体文件"
        : "nt_data 目录，用于读取本地图片和图片表情";

    public string NtUidToolTip =>
        "nt_uid 在 /data/data/com.tencent.mobileqq/files/uid 中\n" +
        "文件名格式类似 QQ号###u_xxx，### 后面的 u_xxx 就是 nt_uid";

    public string FindDatabaseKeyButtonText => IsPCQQ
        ? "登录PCQQ自动获取Key"
        : "登录QQ自动获取Key";

    public Dictionary<QQNtHmacAlgorithm, string> HmacAlgorithms { get; } = new()
    {
        { QQNtHmacAlgorithm.HmacSha1, "HMAC_SHA1" },
        { QQNtHmacAlgorithm.HmacSha512, "HMAC_SHA512" },
    };

    public string HmacAlgorithmToolTip =>
        "SQLCipher 的 HMAC 算法, 这一组数据库共用. 选择 nt_msg.db 时会按数据库头部自动检测:\n" +
        "新版 QQNT 在头部写明 HMAC_SHA1, 旧版 PC QQNT 头部没有写, 用的是 HMAC_SHA512";

    public Dictionary<DatabasePlatformType, string> PlatformTypes { get; } = new()
    {
        { DatabasePlatformType.PCQQ, "PCQQ" },
        { DatabasePlatformType.QQNT, "QQNT" },
        { DatabasePlatformType.AndroidMobileQQ, "AndroidQQ" },
        { DatabasePlatformType.AndroidQQNT, "AndroidQQNT" },
        { DatabasePlatformType.Icalingua, "Icalingua" },
    };

    public ViewModelToken ViewModelToken { get; } = new();

    public DatabaseConfig? ResultConfig { get; private set; }

    public OpenDatabaseDialogViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
    }

    public void SetInitialDatabaseFile(string databaseFilePath)
    {
        var fileName = Path.GetFileName(databaseFilePath);
        if (string.Equals(fileName, PCQQMessageDbFileName, StringComparison.OrdinalIgnoreCase))
        {
            PlatformType = DatabasePlatformType.PCQQ;
            PCQQMessageDbPath = databaseFilePath;
            return;
        }

        if (fileName.StartsWith(IcalinguaDatabasePrefix, StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            PlatformType = DatabasePlatformType.Icalingua;
            IcalinguaDatabasePath = databaseFilePath;
            return;
        }

        // 和手动选择一样, 顺带补全同目录的其它数据库和 nt_data
        PlatformType = DatabasePlatformType.QQNT;
        if (string.Equals(fileName, NtMessageDbFileName, StringComparison.OrdinalIgnoreCase))
        {
            UsePickedNtMessageDbPath(databaseFilePath);
        }
        else if (string.Equals(fileName, NtGroupInfoDbFileName, StringComparison.OrdinalIgnoreCase))
        {
            UsePickedNtGroupInfoDbPath(databaseFilePath);
        }
        else if (string.Equals(fileName, NtGroupMessageFtsDbFileName, StringComparison.OrdinalIgnoreCase))
        {
            UsePickedNtGroupMessageFtsDbPath(databaseFilePath);
        }
        else if (string.Equals(fileName, NtBuddyMessageFtsDbFileName, StringComparison.OrdinalIgnoreCase))
        {
            UsePickedNtBuddyMessageFtsDbPath(databaseFilePath);
        }
        else if (string.Equals(fileName, NtProfileInfoDbFileName, StringComparison.OrdinalIgnoreCase))
        {
            UsePickedNtProfileInfoDbPath(databaseFilePath);
        }
        else
        {
            NtMessageDbPath = databaseFilePath;
        }
    }

    public void SetInitialPlatform(DatabasePlatformType platformType)
    {
        PlatformType = platformType;
    }

    public void SetInitialConfig(DatabaseConfig config)
    {
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
            case DatabasePlatformType.AndroidMobileQQ when config.AndroidMobileQQ is { } androidMobile:
                AndroidMobileQQRootPath = androidMobile.RootPath ?? string.Empty;
                AndroidMobileQQSelfUin = androidMobile.SelfUin ?? string.Empty;
                AndroidMobileQQMediaPath = androidMobile.MobileQQPath ?? string.Empty;
                AndroidMobileQQChatPicPath = androidMobile.ChatPicPath ?? string.Empty;
                break;
            case DatabasePlatformType.QQNT when config.QQNT is { } qqnt:
                SetQQNTFields(qqnt);
                break;
            case DatabasePlatformType.Icalingua when config.Icalingua is { } icalingua:
                IcalinguaDatabasePath = icalingua.DatabasePath ?? string.Empty;
                IcalinguaDataPath = icalingua.DataPath ?? string.Empty;
                break;
        }
    }

    partial void OnPlatformTypeChanged(DatabasePlatformType value)
    {
        OnPropertyChanged(nameof(IsQQNT));
        OnPropertyChanged(nameof(IsAndroidQQNT));
        OnPropertyChanged(nameof(IsAndroidMobileQQ));
        OnPropertyChanged(nameof(IsNtPlatform));
        OnPropertyChanged(nameof(IsPCQQ));
        OnPropertyChanged(nameof(IsIcalingua));
        OnPropertyChanged(nameof(CanFindDatabaseKey));
        OnPropertyChanged(nameof(NtDataPathLabel));
        OnPropertyChanged(nameof(NtDataPathToolTip));
        OnPropertyChanged(nameof(FindDatabaseKeyButtonText));
        DetectHmacAlgorithm(NtMessageDbPath);
    }

    partial void OnNtMessageDbPathChanged(string value)
    {
        DetectHmacAlgorithm(value);
    }

    private void DetectHmacAlgorithm(string databaseFilePath)
    {
        if (!IsNtPlatform || string.IsNullOrWhiteSpace(databaseFilePath))
            return;
        if (RawDatabase.DetectQQNtHmacAlgorithm(databaseFilePath, IsAndroidQQNT).ToQQNtHmacAlgorithm() is { } detected)
            CipherHmacAlgorithm = detected;
    }

    public void UsePickedNtMessageDbPath(string databaseFilePath)
    {
        NtMessageDbPath = databaseFilePath;
        TryCompleteNtDatabasePaths(databaseFilePath, NtMessageDbFileName);
    }

    public void UsePickedNtGroupInfoDbPath(string databaseFilePath)
    {
        NtGroupInfoDbPath = databaseFilePath;
        TryCompleteNtDatabasePaths(databaseFilePath, NtGroupInfoDbFileName);
    }

    public void UsePickedNtGroupMessageFtsDbPath(string databaseFilePath)
    {
        NtGroupMessageFtsDbPath = databaseFilePath;
        TryCompleteNtDatabasePaths(databaseFilePath, NtGroupMessageFtsDbFileName);
    }

    public void UsePickedNtBuddyMessageFtsDbPath(string databaseFilePath)
    {
        NtBuddyMessageFtsDbPath = databaseFilePath;
        TryCompleteNtDatabasePaths(databaseFilePath, NtBuddyMessageFtsDbFileName);
    }

    public void UsePickedNtProfileInfoDbPath(string databaseFilePath)
    {
        NtProfileInfoDbPath = databaseFilePath;
        TryCompleteNtDatabasePaths(databaseFilePath, NtProfileInfoDbFileName);
    }

    public void UsePickedPCQQMessageDbPath(string databaseFilePath)
    {
        PCQQMessageDbPath = databaseFilePath;
        TryCompletePCQQDatabasePaths(databaseFilePath, PCQQMessageDbFileName);
    }

    public void UsePickedPCQQInfoDbPath(string databaseFilePath)
    {
        PCQQInfoDbPath = databaseFilePath;
        TryCompletePCQQDatabasePaths(databaseFilePath, PCQQInfoDbFileName);
    }

    public void UsePickedAndroidQQNtMobileQQPath(string mobileQQPath)
    {
        MobileQQPath = mobileQQPath;
        TryCompleteAndroidQQNtChatPicPath(mobileQQPath);
    }

    public void UsePickedAndroidQQNtChatPicPath(string chatPicPath)
    {
        ChatPicPath = chatPicPath;
    }

    public void UsePickedAndroidMobileQQMediaPath(string mobileQQPath)
    {
        AndroidMobileQQMediaPath = mobileQQPath;
        TryCompleteAndroidMobileQQChatPicPath(mobileQQPath);
    }

    public void UsePickedAndroidMobileQQChatPicPath(string chatPicPath)
    {
        AndroidMobileQQChatPicPath = chatPicPath;
    }

    // 同一组 QQNT 数据库共用一个密钥: 填了其中一个 (手填, 登录 QQ 获取, Android 推导) 就同步到其它
    // 还没填, 或者还是旧值 (之前同步过来) 的密钥框, 单独改过的不覆盖
    partial void OnKeyChanged(string oldValue, string newValue) => SyncNtKeys(oldValue, newValue);

    partial void OnGroupInfoDbKeyChanged(string oldValue, string newValue) => SyncNtKeys(oldValue, newValue);

    partial void OnGroupMessageFtsDbKeyChanged(string oldValue, string newValue) => SyncNtKeys(oldValue, newValue);

    partial void OnBuddyMessageFtsDbKeyChanged(string oldValue, string newValue) => SyncNtKeys(oldValue, newValue);

    partial void OnProfileInfoDbKeyChanged(string oldValue, string newValue) => SyncNtKeys(oldValue, newValue);

    private void SyncNtKeys(string oldValue, string newValue)
    {
        if (_syncingNtKeys || !IsNtPlatform || string.IsNullOrWhiteSpace(newValue))
            return;

        _syncingNtKeys = true;
        try
        {
            bool ShouldFill(string current) => string.IsNullOrWhiteSpace(current) || current == oldValue;
            if (ShouldFill(Key))
                Key = newValue;
            if (ShouldFill(GroupInfoDbKey))
                GroupInfoDbKey = newValue;
            if (ShouldFill(GroupMessageFtsDbKey))
                GroupMessageFtsDbKey = newValue;
            if (ShouldFill(BuddyMessageFtsDbKey))
                BuddyMessageFtsDbKey = newValue;
            if (ShouldFill(ProfileInfoDbKey))
                ProfileInfoDbKey = newValue;
        }
        finally
        {
            _syncingNtKeys = false;
        }
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
            }
            else if (IsIcalingua)
            {
                if (!await ValidateRequiredFileAsync(IcalinguaDatabasePath, "请输入 Icalingua 数据库路径", "Icalingua 数据库文件不存在"))
                    return;

                ResultConfig = CreateIcalinguaConfig();
            }
            else if (IsAndroidMobileQQ)
            {
                if (!await ValidateRequiredDirectoryAsync(AndroidMobileQQRootPath, "请输入 Android QQ 数据目录", "Android QQ 数据目录不存在"))
                    return;

                if (string.IsNullOrWhiteSpace(AndroidMobileQQSelfUin))
                {
                    await _dialogService.ShowMessageBox("请输入账号 QQ", "错误", ViewModelToken);
                    return;
                }

                var messageDbPath = Path.Combine(
                    ResolveAndroidMobileQQChildDirectory(AndroidMobileQQRootPath, "databases", "db"),
                    AndroidMobileQQSelfUin + ".db");
                var keyPath = Path.Combine(
                    ResolveAndroidMobileQQChildDirectory(AndroidMobileQQRootPath, "files", "f"),
                    "kc");
                if (!await ValidateOptionalFileAsync(messageDbPath, $"{AndroidMobileQQSelfUin}.db 文件不存在"))
                    return;
                if (!await ValidateOptionalFileAsync(keyPath, "files/kc 文件不存在"))
                    return;

                ResultConfig = CreateAndroidMobileQQConfig();
            }
            else
            {
                if (IsAndroidQQNT)
                    EnsureAndroidQQNTKey();

                if (!await ValidateNtDatabasesAsync())
                    return;

                ResultConfig = CreateQQNTConfig();
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

        if (!IsQQNT)
            return;

        // 没获取到 (直接关了窗口) 时保留原来填的密钥; 获取到的会同步到其它数据库的密钥框
        var key = await _dialogService.ShowQQDebuggerWindow(ViewModelToken);
        if (!string.IsNullOrWhiteSpace(key))
            Key = key;
    }

    private async Task<bool> ValidateNtDatabasesAsync()
    {
        if (string.IsNullOrWhiteSpace(NtMessageDbPath) &&
            string.IsNullOrWhiteSpace(NtGroupInfoDbPath) &&
            string.IsNullOrWhiteSpace(NtGroupMessageFtsDbPath) &&
            string.IsNullOrWhiteSpace(NtBuddyMessageFtsDbPath) &&
            string.IsNullOrWhiteSpace(NtProfileInfoDbPath))
        {
            await _dialogService.ShowMessageBox("请至少填写一个 QQNT 数据库路径", "错误", ViewModelToken);
            return false;
        }

        return await ValidateOptionalFileAsync(NtGroupInfoDbPath, "group_info.db 文件不存在") &&
               await ValidateOptionalFileAsync(NtProfileInfoDbPath, "profile_info.db 文件不存在") &&
               await ValidateOptionalFileAsync(NtMessageDbPath, "nt_msg.db 文件不存在") &&
               await ValidateOptionalFileAsync(NtGroupMessageFtsDbPath, "group_msg_fts.db 文件不存在") &&
               await ValidateOptionalFileAsync(NtBuddyMessageFtsDbPath, "buddy_msg_fts.db 文件不存在");
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

    private async Task<bool> ValidateRequiredDirectoryAsync(string path, string emptyMessage, string missingMessage)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            await _dialogService.ShowMessageBox(emptyMessage, "错误", ViewModelToken);
            return false;
        }

        if (Directory.Exists(path))
            return true;

        await _dialogService.ShowMessageBox(missingMessage, "错误", ViewModelToken);
        return false;
    }

    private async Task<bool> ValidateOptionalFileAsync(string path, string missingMessage)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;

        // 也可以是 tar / zip 归档里的文件, 例如 data.tar\databases\nt_db\nt_qq_xxx\nt_msg.db
        if (ArchiveFile.Exists(path))
            return true;

        await _dialogService.ShowMessageBox(missingMessage, "错误", ViewModelToken);
        return false;
    }

    private void EnsureAndroidQQNTKey()
    {
        if (IsAndroidQQNT && !string.IsNullOrWhiteSpace(NtUid) && !string.IsNullOrWhiteSpace(Rand))
            Key = RawDatabase.GetQQKey(NtUid, Rand);
    }

    private void TryCompleteNtDatabasePaths(string databaseFilePath, string expectedFileName)
    {
        if (!IsPickedStandardFile(databaseFilePath, expectedFileName))
            return;

        if (string.Equals(expectedFileName, NtMessageDbFileName, StringComparison.OrdinalIgnoreCase))
        {
            EnsureRand(databaseFilePath);
        }

        var directory = Path.GetDirectoryName(databaseFilePath);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        if (TryGetExistingSiblingFile(NtMessageDbPath, directory, NtMessageDbFileName) is { } messageDbPath)
            NtMessageDbPath = messageDbPath;

        if (TryGetExistingSiblingFile(NtGroupInfoDbPath, directory, NtGroupInfoDbFileName) is { } groupInfoDbPath)
            NtGroupInfoDbPath = groupInfoDbPath;

        if (TryGetExistingSiblingFile(NtGroupMessageFtsDbPath, directory, NtGroupMessageFtsDbFileName) is { } groupMessageFtsDbPath)
            NtGroupMessageFtsDbPath = groupMessageFtsDbPath;

        if (TryGetExistingSiblingFile(NtBuddyMessageFtsDbPath, directory, NtBuddyMessageFtsDbFileName) is { } buddyMessageFtsDbPath)
            NtBuddyMessageFtsDbPath = buddyMessageFtsDbPath;

        if (TryGetExistingSiblingFile(NtProfileInfoDbPath, directory, NtProfileInfoDbFileName) is { } profileInfoDbPath)
            NtProfileInfoDbPath = profileInfoDbPath;

        EnsureNtDataPath(databaseFilePath);
    }

    private void TryCompletePCQQDatabasePaths(string databaseFilePath, string expectedFileName)
    {
        if (!IsPickedStandardFile(databaseFilePath, expectedFileName))
            return;

        var directory = Path.GetDirectoryName(databaseFilePath);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        if (TryGetExistingSiblingFile(PCQQMessageDbPath, directory, PCQQMessageDbFileName) is { } messageDbPath)
            PCQQMessageDbPath = messageDbPath;

        if (TryGetExistingSiblingFile(PCQQInfoDbPath, directory, PCQQInfoDbFileName) is { } infoDbPath)
            PCQQInfoDbPath = infoDbPath;

        EnsurePCQQDataPath(databaseFilePath);
    }

    private static bool IsPickedStandardFile(string databaseFilePath, string expectedFileName)
    {
        return ArchiveFile.Exists(databaseFilePath) &&
               string.Equals(Path.GetFileName(databaseFilePath), expectedFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveAndroidMobileQQChildDirectory(string rootPath, string primaryName, string fallbackName)
    {
        var primaryPath = Path.Combine(rootPath, primaryName);
        if (Directory.Exists(primaryPath))
            return primaryPath;

        var fallbackPath = Path.Combine(rootPath, fallbackName);
        return Directory.Exists(fallbackPath) ? fallbackPath : primaryPath;
    }

    private static string? TryGetExistingSiblingFile(string currentPath, string directory, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(currentPath))
            return null;

        var filePath = Path.Combine(directory, fileName);
        return ArchiveFile.Exists(filePath) ? filePath : null;
    }

    private void EnsureRand(string databaseFilePath)
    {
        if (ArchiveFile.Exists(databaseFilePath) && RawDatabase.GetRand(databaseFilePath) is string rand)
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

    private void TryCompleteAndroidQQNtChatPicPath(string mobileQQPath)
    {
        if (!string.IsNullOrWhiteSpace(ChatPicPath))
            return;

        if (TryGetMobileQQChatPicPath(mobileQQPath) is { } chatPicPath)
            ChatPicPath = chatPicPath;
    }

    private void TryCompleteAndroidMobileQQChatPicPath(string mobileQQPath)
    {
        if (!string.IsNullOrWhiteSpace(AndroidMobileQQChatPicPath))
            return;

        if (TryGetMobileQQChatPicPath(mobileQQPath) is { } chatPicPath)
            AndroidMobileQQChatPicPath = chatPicPath;
    }

    private static string? TryGetMobileQQChatPicPath(string mobileQQPath)
    {
        if (string.IsNullOrWhiteSpace(mobileQQPath))
            return null;

        var chatPicPath = Path.Combine(mobileQQPath, "chatpic");
        return Directory.Exists(chatPicPath) ? chatPicPath : null;
    }

    private static string? EmptyToNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private void SetQQNTFields(QQNTDatabaseConfig config)
    {
        NtMessageDbPath = config.MessageDbPath ?? string.Empty;
        NtGroupInfoDbPath = config.GroupInfoDbPath ?? string.Empty;
        NtGroupMessageFtsDbPath = config.GroupMessageFtsDbPath ?? string.Empty;
        NtBuddyMessageFtsDbPath = config.BuddyMessageFtsDbPath ?? string.Empty;
        NtProfileInfoDbPath = config.ProfileInfoDbPath ?? string.Empty;

        // 配置里的密钥原样恢复, 不互相同步
        _syncingNtKeys = true;
        try
        {
            Key = config.MessageDbPassword ?? string.Empty;
            GroupInfoDbKey = config.GroupInfoDbPassword ?? string.Empty;
            GroupMessageFtsDbKey = config.GroupMessageFtsDbPassword ?? string.Empty;
            BuddyMessageFtsDbKey = config.BuddyMessageFtsDbPassword ?? string.Empty;
            ProfileInfoDbKey = config.ProfileInfoDbPassword ?? string.Empty;
        }
        finally
        {
            _syncingNtKeys = false;
        }

        NtDataPath = config.NtDataPath ?? string.Empty;
        MobileQQPath = config is AndroidQQNTDatabaseConfig android
            ? android.MobileQQPath ?? string.Empty
            : string.Empty;
        ChatPicPath = config is AndroidQQNTDatabaseConfig androidConfig
            ? androidConfig.ChatPicPath ?? string.Empty
            : string.Empty;
        // 设置路径时已经自动检测过, 这里以保存的配置为准; 旧配置没有这一项时按 HMAC_SHA1
        CipherHmacAlgorithm = config.CipherHmacAlgorithm ?? QQNtHmacAlgorithm.HmacSha1;
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

    private DatabaseConfig CreateAndroidMobileQQConfig()
    {
        return new DatabaseConfig
        {
            Type = DatabasePlatformType.AndroidMobileQQ,
            AndroidMobileQQ = new AndroidMobileQQDatabaseConfig
            {
                RootPath = EmptyToNull(AndroidMobileQQRootPath),
                SelfUin = EmptyToNull(AndroidMobileQQSelfUin),
                MobileQQPath = EmptyToNull(AndroidMobileQQMediaPath),
                ChatPicPath = EmptyToNull(AndroidMobileQQChatPicPath),
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
            BuddyMessageFtsDbPath = EmptyToNull(NtBuddyMessageFtsDbPath),
            BuddyMessageFtsDbPassword = EmptyToNull(BuddyMessageFtsDbKey),
            ProfileInfoDbPath = EmptyToNull(NtProfileInfoDbPath),
            ProfileInfoDbPassword = EmptyToNull(ProfileInfoDbKey),
            CipherHmacAlgorithm = CipherHmacAlgorithm,
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
                ChatPicPath = EmptyToNull(ChatPicPath),
                MessageDbPath = config.MessageDbPath,
                MessageDbPassword = config.MessageDbPassword,
                GroupInfoDbPath = config.GroupInfoDbPath,
                GroupInfoDbPassword = config.GroupInfoDbPassword,
                GroupMessageFtsDbPath = config.GroupMessageFtsDbPath,
                GroupMessageFtsDbPassword = config.GroupMessageFtsDbPassword,
                BuddyMessageFtsDbPath = config.BuddyMessageFtsDbPath,
                BuddyMessageFtsDbPassword = config.BuddyMessageFtsDbPassword,
                ProfileInfoDbPath = config.ProfileInfoDbPath,
                ProfileInfoDbPassword = config.ProfileInfoDbPassword,
                CipherHmacAlgorithm = config.CipherHmacAlgorithm,
                NtUid = EmptyToNull(NtUid),
                Rand = EmptyToNull(Rand),
            },
        };
    }

    private DatabaseConfig CreateIcalinguaConfig()
    {
        return new DatabaseConfig
        {
            Type = DatabasePlatformType.Icalingua,
            Icalingua = new IcalinguaDatabaseConfig
            {
                DatabasePath = EmptyToNull(IcalinguaDatabasePath),
                DataPath = EmptyToNull(IcalinguaDataPath),
            },
        };
    }
}
