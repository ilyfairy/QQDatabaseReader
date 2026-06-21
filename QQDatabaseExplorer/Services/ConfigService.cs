using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.Services;

public class ConfigService
{
    private readonly QQDatabaseService _qqDatabaseService;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            new JsonStringEnumConverter(),
        },
    };

    /// <summary>
    /// 默认配置文件路径：%AppData%/QQDatabaseExplorer/config.json
    /// </summary>
    public static string DefaultConfigFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QQDatabaseExplorer",
            "config.json");

    public ConfigService(QQDatabaseService qqDatabaseService)
    {
        _qqDatabaseService = qqDatabaseService;
    }

    /// <summary>
    /// 从当前已打开的数据库状态生成配置对象
    /// </summary>
    public AppConfig CreateConfigFromCurrentState()
    {
        return _qqDatabaseService.CreateCurrentConfig();
    }

    /// <summary>
    /// 保存当前配置到指定路径
    /// </summary>
    public async Task SaveToFileAsync(string filePath)
    {
        var config = CreateConfigForSave(CreateConfigFromCurrentState(), filePath);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(config, JsonOptions);
        await File.WriteAllTextAsync(filePath, json);
    }

    /// <summary>
    /// 保存当前配置到默认路径
    /// </summary>
    public async Task SaveToDefaultAsync()
    {
        await SaveToFileAsync(DefaultConfigFilePath);
    }

    /// <summary>
    /// 从指定路径读取配置。数据库应用由 DatabaseConfigApplicationService 负责。
    /// </summary>
    public async Task<AppConfig?> LoadFromFileAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        string json;
        try
        {
            json = await File.ReadAllTextAsync(filePath);
        }
        catch
        {
            return null;
        }

        AppConfig config;
        try
        {
            config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
                ?? new AppConfig();
        }
        catch
        {
            return null;
        }

        ResolveConfigPathsForLoad(config, filePath);
        return config;
    }

    /// <summary>
    /// 从默认路径读取配置。
    /// </summary>
    public async Task<AppConfig?> LoadFromDefaultAsync()
    {
        if (!File.Exists(DefaultConfigFilePath))
            return null;

        return await LoadFromFileAsync(DefaultConfigFilePath);
    }

    private static AppConfig CreateConfigForSave(AppConfig config, string filePath)
    {
        var configDirectory = GetConfigDirectory(filePath);
        var result = new AppConfig();
        foreach (var databaseConfig in config.Databases)
        {
            result.Databases.Add(CreateDatabaseConfigForSave(databaseConfig, configDirectory));
        }

        return result;
    }

    private static DatabaseConfig CreateDatabaseConfigForSave(DatabaseConfig config, string configDirectory)
    {
        return config.Type switch
        {
            DatabasePlatformType.PCQQ => new DatabaseConfig
            {
                Type = DatabasePlatformType.PCQQ,
                PCQQ = config.PCQQ is null
                    ? null
                    : new PCQQDatabaseConfig
                    {
                        MessageDbPath = CreatePortablePath(config.PCQQ.MessageDbPath, configDirectory),
                        MessageDbKey = config.PCQQ.MessageDbKey,
                        InfoDbPath = CreatePortablePath(config.PCQQ.InfoDbPath, configDirectory),
                        InfoDbKey = config.PCQQ.InfoDbKey,
                        DataPath = CreatePortablePath(config.PCQQ.DataPath, configDirectory),
                    },
            },
            DatabasePlatformType.AndroidQQNT => new DatabaseConfig
            {
                Type = DatabasePlatformType.AndroidQQNT,
                AndroidQQNT = config.AndroidQQNT is null
                    ? null
                    : new AndroidQQNTDatabaseConfig
                    {
                        MobileQQPath = CreatePortablePath(config.AndroidQQNT.MobileQQPath, configDirectory),
                        NtUid = config.AndroidQQNT.NtUid,
                        Rand = config.AndroidQQNT.Rand,
                        NtDataPath = CreatePortablePath(config.AndroidQQNT.NtDataPath, configDirectory),
                        MessageDbPath = CreatePortablePath(config.AndroidQQNT.MessageDbPath, configDirectory),
                        MessageDbPassword = config.AndroidQQNT.MessageDbPassword,
                        GroupInfoDbPath = CreatePortablePath(config.AndroidQQNT.GroupInfoDbPath, configDirectory),
                        GroupInfoDbPassword = config.AndroidQQNT.GroupInfoDbPassword,
                        GroupMessageFtsDbPath = CreatePortablePath(config.AndroidQQNT.GroupMessageFtsDbPath, configDirectory),
                        GroupMessageFtsDbPassword = config.AndroidQQNT.GroupMessageFtsDbPassword,
                        ProfileInfoDbPath = CreatePortablePath(config.AndroidQQNT.ProfileInfoDbPath, configDirectory),
                        ProfileInfoDbPassword = config.AndroidQQNT.ProfileInfoDbPassword,
                    },
            },
            DatabasePlatformType.AndroidMobileQQ => new DatabaseConfig
            {
                Type = DatabasePlatformType.AndroidMobileQQ,
                AndroidMobileQQ = config.AndroidMobileQQ is null
                    ? null
                    : new AndroidMobileQQDatabaseConfig
                    {
                        RootPath = CreatePortablePath(config.AndroidMobileQQ.RootPath, configDirectory),
                        SelfUin = config.AndroidMobileQQ.SelfUin,
                        MobileQQPath = CreatePortablePath(config.AndroidMobileQQ.MobileQQPath, configDirectory),
                    },
            },
            DatabasePlatformType.Icalingua => new DatabaseConfig
            {
                Type = DatabasePlatformType.Icalingua,
                Icalingua = config.Icalingua is null
                    ? null
                    : new IcalinguaDatabaseConfig
                    {
                        DatabasePath = CreatePortablePath(config.Icalingua.DatabasePath, configDirectory),
                        DataPath = CreatePortablePath(config.Icalingua.DataPath, configDirectory),
                    },
            },
            _ => new DatabaseConfig
            {
                Type = DatabasePlatformType.QQNT,
                QQNT = config.QQNT is null
                    ? null
                    : new QQNTDatabaseConfig
                    {
                        NtDataPath = CreatePortablePath(config.QQNT.NtDataPath, configDirectory),
                        MessageDbPath = CreatePortablePath(config.QQNT.MessageDbPath, configDirectory),
                        MessageDbPassword = config.QQNT.MessageDbPassword,
                        GroupInfoDbPath = CreatePortablePath(config.QQNT.GroupInfoDbPath, configDirectory),
                        GroupInfoDbPassword = config.QQNT.GroupInfoDbPassword,
                        GroupMessageFtsDbPath = CreatePortablePath(config.QQNT.GroupMessageFtsDbPath, configDirectory),
                        GroupMessageFtsDbPassword = config.QQNT.GroupMessageFtsDbPassword,
                        ProfileInfoDbPath = CreatePortablePath(config.QQNT.ProfileInfoDbPath, configDirectory),
                        ProfileInfoDbPassword = config.QQNT.ProfileInfoDbPassword,
                    },
            },
        };
    }

    private static void ResolveConfigPathsForLoad(AppConfig config, string filePath)
    {
        var configDirectory = GetConfigDirectory(filePath);
        foreach (var databaseConfig in config.Databases)
        {
            ResolveDatabaseConfigPathsForLoad(databaseConfig, configDirectory);
        }
    }

    private static void ResolveDatabaseConfigPathsForLoad(DatabaseConfig config, string configDirectory)
    {
        if (config.QQNT is { } qqnt)
        {
            ResolveQQNTPathsForLoad(qqnt, configDirectory);
        }

        if (config.AndroidQQNT is { } android)
        {
            ResolveQQNTPathsForLoad(android, configDirectory);
            android.MobileQQPath = ResolveConfigPath(android.MobileQQPath, configDirectory);
        }

        if (config.PCQQ is { } pcqq)
        {
            pcqq.MessageDbPath = ResolveConfigPath(pcqq.MessageDbPath, configDirectory);
            pcqq.InfoDbPath = ResolveConfigPath(pcqq.InfoDbPath, configDirectory);
            pcqq.DataPath = ResolveConfigPath(pcqq.DataPath, configDirectory);
        }

        if (config.AndroidMobileQQ is { } androidMobileQQ)
        {
            androidMobileQQ.RootPath = ResolveConfigPath(androidMobileQQ.RootPath, configDirectory);
            androidMobileQQ.MobileQQPath = ResolveConfigPath(androidMobileQQ.MobileQQPath, configDirectory);
        }

        if (config.Icalingua is { } icalingua)
        {
            icalingua.DatabasePath = ResolveConfigPath(icalingua.DatabasePath, configDirectory);
            icalingua.DataPath = ResolveConfigPath(icalingua.DataPath, configDirectory);
        }
    }

    private static void ResolveQQNTPathsForLoad(QQNTDatabaseConfig config, string configDirectory)
    {
        config.NtDataPath = ResolveConfigPath(config.NtDataPath, configDirectory);
        config.MessageDbPath = ResolveConfigPath(config.MessageDbPath, configDirectory);
        config.GroupInfoDbPath = ResolveConfigPath(config.GroupInfoDbPath, configDirectory);
        config.GroupMessageFtsDbPath = ResolveConfigPath(config.GroupMessageFtsDbPath, configDirectory);
        config.ProfileInfoDbPath = ResolveConfigPath(config.ProfileInfoDbPath, configDirectory);
    }

    private static string? CreatePortablePath(string? path, string configDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return path;

        var fullPath = Path.GetFullPath(path);
        return IsSameOrChildPath(fullPath, configDirectory)
            ? Path.GetRelativePath(configDirectory, fullPath)
            : path;
    }

    private static string? ResolveConfigPath(string? path, string configDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path))
            return path;

        return Path.GetFullPath(Path.Combine(configDirectory, path));
    }

    private static string GetConfigDirectory(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        return Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
    }

    private static bool IsSameOrChildPath(string path, string directory)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (string.Equals(normalizedPath, normalizedDirectory, comparison))
            return true;

        var directoryWithSeparator = normalizedDirectory + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(directoryWithSeparator, comparison);
    }
}
