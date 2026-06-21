using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace QQDatabaseExplorer.Models;

/// <summary>
/// 应用程序配置，用于保存和恢复已打开的数据库状态。
/// 只保存用户在界面上输入的内容。
/// </summary>
public class AppConfig
{
    /// <summary>
    /// 已打开的数据库配置列表。当前界面一次只打开一个配置项，后续复合查询可扩展为多个同类配置。
    /// </summary>
    [JsonPropertyName("database")]
    public List<DatabaseConfig> Databases { get; set; } = [];
}

public class DatabaseConfig
{
    public DatabasePlatformType Type { get; set; } = DatabasePlatformType.QQNT;

    public QQNTDatabaseConfig? QQNT { get; set; }

    public PCQQDatabaseConfig? PCQQ { get; set; }

    public AndroidQQNTDatabaseConfig? AndroidQQNT { get; set; }

    public AndroidMobileQQDatabaseConfig? AndroidMobileQQ { get; set; }

    public IcalinguaDatabaseConfig? Icalingua { get; set; }
}

public enum DatabasePlatformType
{
    PCQQ,
    QQNT,
    AndroidMobileQQ,
    AndroidQQNT,
    Icalingua,
}

public class QQNTDatabaseConfig
{
    public string? NtDataPath { get; set; }

    public string? MessageDbPath { get; set; }

    public string? MessageDbPassword { get; set; }

    public string? GroupInfoDbPath { get; set; }

    public string? GroupInfoDbPassword { get; set; }

    public string? GroupMessageFtsDbPath { get; set; }

    public string? GroupMessageFtsDbPassword { get; set; }

    public string? ProfileInfoDbPath { get; set; }

    public string? ProfileInfoDbPassword { get; set; }
}

public class AndroidQQNTDatabaseConfig : QQNTDatabaseConfig
{
    public string? MobileQQPath { get; set; }

    public string? ChatPicPath { get; set; }

    public string? NtUid { get; set; }

    public string? Rand { get; set; }
}

public class PCQQDatabaseConfig
{
    public string? MessageDbPath { get; set; }

    public string? MessageDbKey { get; set; }

    public string? InfoDbPath { get; set; }

    public string? InfoDbKey { get; set; }

    public string? DataPath { get; set; }
}

public class AndroidMobileQQDatabaseConfig
{
    public string? RootPath { get; set; }

    public string? SelfUin { get; set; }

    public string? MobileQQPath { get; set; }

    public string? ChatPicPath { get; set; }
}

public class IcalinguaDatabaseConfig
{
    public string? DatabasePath { get; set; }

    public string? DataPath { get; set; }
}
