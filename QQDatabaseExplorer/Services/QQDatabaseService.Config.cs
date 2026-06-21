using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.Services;

public partial class QQDatabaseService
{
    public DatabaseConfig? CreateConfigForGroup(LoadedDatabaseGroup group)
    {
        return group.Config ?? group.PlatformType switch
        {
            DatabasePlatformType.PCQQ => CreateCurrentPCQQConfig(),
            DatabasePlatformType.AndroidMobileQQ => CreateCurrentAndroidMobileQQConfig(),
            DatabasePlatformType.Icalingua => CreateCurrentIcalinguaConfigs().FirstOrDefault(),
            DatabasePlatformType.AndroidQQNT => CreateCurrentQQNTConfig(DatabasePlatformType.AndroidQQNT),
            DatabasePlatformType.QQNT => CreateCurrentQQNTConfig(DatabasePlatformType.QQNT),
            _ => null,
        };
    }

    public AppConfig CreateCurrentConfig()
    {
        var config = new AppConfig();

        if ((_currentQQNTConfig ?? CreateCurrentQQNTConfig(_ntPlatformType)) is { } qqntConfig)
            config.Databases.Add(qqntConfig);

        if ((_currentPCQQConfig ?? CreateCurrentPCQQConfig()) is { } pcqqConfig)
            config.Databases.Add(pcqqConfig);

        if ((_currentAndroidMobileQQConfig ?? CreateCurrentAndroidMobileQQConfig()) is { } androidMobileQQConfig)
            config.Databases.Add(androidMobileQQConfig);

        config.Databases.AddRange(_icalinguaDatabases.CurrentOrLoadedConfigs);

        return config;
    }

    private void ClearQQNTConfigItem(LoadedDatabaseItemKind kind)
    {
        var qqnt = _currentQQNTConfig?.Type is DatabasePlatformType.AndroidQQNT
            ? _currentQQNTConfig.AndroidQQNT
            : _currentQQNTConfig?.QQNT;
        if (qqnt is null)
            return;

        switch (kind)
        {
            case LoadedDatabaseItemKind.NtMessageDb:
                qqnt.MessageDbPath = null;
                qqnt.MessageDbPassword = null;
                break;
            case LoadedDatabaseItemKind.GroupInfoDb:
                qqnt.GroupInfoDbPath = null;
                qqnt.GroupInfoDbPassword = null;
                break;
            case LoadedDatabaseItemKind.ProfileInfoDb:
                qqnt.ProfileInfoDbPath = null;
                qqnt.ProfileInfoDbPassword = null;
                break;
            case LoadedDatabaseItemKind.GroupMessageFtsDb:
                qqnt.GroupMessageFtsDbPath = null;
                qqnt.GroupMessageFtsDbPassword = null;
                break;
            case LoadedDatabaseItemKind.NtDataPath:
                qqnt.NtDataPath = null;
                break;
            case LoadedDatabaseItemKind.MobileQQPath:
                if (_currentQQNTConfig?.AndroidQQNT is { } android)
                    android.MobileQQPath = null;
                break;
            case LoadedDatabaseItemKind.AndroidQQNtChatPicPath:
                if (_currentQQNTConfig?.AndroidQQNT is { } androidConfig)
                    androidConfig.ChatPicPath = null;
                break;
        }
    }

    private void ClearPCQQConfigItem(LoadedDatabaseItemKind kind)
    {
        var pcqq = _currentPCQQConfig?.PCQQ;
        if (pcqq is null)
            return;

        switch (kind)
        {
            case LoadedDatabaseItemKind.PCQQMessageDb:
                pcqq.MessageDbPath = null;
                pcqq.MessageDbKey = null;
                break;
            case LoadedDatabaseItemKind.PCQQInfoDb:
                pcqq.InfoDbPath = null;
                pcqq.InfoDbKey = null;
                break;
            case LoadedDatabaseItemKind.PCQQDataPath:
                pcqq.DataPath = null;
                break;
        }
    }

    private void ClearIcalinguaConfigItem(LoadedDatabaseItemKind kind, string? filePath = null)
    {
        _icalinguaDatabases.ClearConfigItem(kind, filePath);
    }

    private void ClearAndroidMobileQQConfigItem(LoadedDatabaseItemKind kind)
    {
        var androidMobileQQ = _currentAndroidMobileQQConfig?.AndroidMobileQQ;
        if (androidMobileQQ is null)
            return;

        switch (kind)
        {
            case LoadedDatabaseItemKind.AndroidMobileQQMessageDb:
            case LoadedDatabaseItemKind.AndroidMobileQQRootPath:
            case LoadedDatabaseItemKind.AndroidMobileQQSlowTableDb:
                androidMobileQQ.RootPath = null;
                androidMobileQQ.SelfUin = null;
                break;
            case LoadedDatabaseItemKind.AndroidMobileQQMobileQQPath:
                androidMobileQQ.MobileQQPath = null;
                break;
            case LoadedDatabaseItemKind.AndroidMobileQQChatPicPath:
                androidMobileQQ.ChatPicPath = null;
                break;
        }
    }

    private DatabaseConfig? CreateCurrentQQNTConfig(DatabasePlatformType platformType)
    {
        return _qqNtDatabases.CreateConfig(platformType);
    }

    private DatabaseConfig? CreateCurrentPCQQConfig()
    {
        return _pcqqDatabase.CreateConfig();
    }

    private DatabaseConfig? CreateCurrentAndroidMobileQQConfig()
    {
        return _androidMobileQQDatabase.CreateConfig();
    }

    private List<DatabaseConfig> CreateCurrentIcalinguaConfigs()
    {
        return _icalinguaDatabases.CreateLoadedConfigs();
    }
}
