using System.Threading.Tasks;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;

namespace QQDatabaseExplorer.Services;

internal static class PreparedDatabaseConfigLoader
{
    public static Task<PreparedDatabaseConfig> PrepareAsync(DatabaseConfig config)
    {
        return Task.Run(() => Prepare(config));
    }

    private static PreparedDatabaseConfig Prepare(DatabaseConfig config)
    {
        return config.Type switch
        {
            DatabasePlatformType.PCQQ when config.PCQQ is { } pcqq =>
                PreparePCQQ(pcqq),
            DatabasePlatformType.AndroidMobileQQ when config.AndroidMobileQQ is { } androidMobile =>
                PrepareAndroidMobileQQ(androidMobile),
            DatabasePlatformType.Icalingua when config.Icalingua is { } icalingua =>
                PrepareIcalingua(icalingua),
            DatabasePlatformType.AndroidQQNT when config.AndroidQQNT is { } android =>
                PrepareQQNt(android, DatabasePlatformType.AndroidQQNT),
            DatabasePlatformType.QQNT when config.QQNT is { } qqnt =>
                PrepareQQNt(qqnt, DatabasePlatformType.QQNT),
            _ => PreparedDatabaseConfig.Empty(config.Type),
        };
    }

    private static PreparedDatabaseConfig PrepareQQNt(QQNTDatabaseConfig config, DatabasePlatformType platformType)
    {
        QQGroupInfoReader? groupInfoDatabase = null;
        QQProfileInfoReader? profileInfoDatabase = null;
        QQMessageReader? messageDatabase = null;
        QQAndroidMessageReader? androidMessageDatabase = null;
        QQGroupMessageFtsReader? groupMessageFtsDatabase = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(config.GroupInfoDbPath))
                groupInfoDatabase = CreateGroupInfoDatabase(config.GroupInfoDbPath, config.GroupInfoDbPassword);

            if (!string.IsNullOrWhiteSpace(config.ProfileInfoDbPath))
                profileInfoDatabase = CreateProfileInfoDatabase(config.ProfileInfoDbPath, config.ProfileInfoDbPassword);

            if (!string.IsNullOrWhiteSpace(config.MessageDbPath) &&
                platformType is DatabasePlatformType.AndroidQQNT)
            {
                androidMessageDatabase = CreateAndroidMessageDatabase(config.MessageDbPath, config.MessageDbPassword);
            }
            else if (!string.IsNullOrWhiteSpace(config.MessageDbPath))
            {
                messageDatabase = CreateMessageDatabase(config.MessageDbPath, config.MessageDbPassword);
            }

            if (!string.IsNullOrWhiteSpace(config.GroupMessageFtsDbPath))
                groupMessageFtsDatabase = CreateGroupMessageFtsDatabase(config.GroupMessageFtsDbPath, config.GroupMessageFtsDbPassword);

            var ntDatabases = new PreparedNtDatabaseGroup(
                groupInfoDatabase,
                profileInfoDatabase,
                messageDatabase,
                androidMessageDatabase,
                groupMessageFtsDatabase,
                config.NtDataPath,
                config is AndroidQQNTDatabaseConfig android ? android.MobileQQPath : null,
                config is AndroidQQNTDatabaseConfig androidConfig ? androidConfig.ChatPicPath : null,
                config is AndroidQQNTDatabaseConfig androidNtConfig ? androidNtConfig.NtUid : null,
                config is AndroidQQNTDatabaseConfig androidRandConfig ? androidRandConfig.Rand : null);
            groupInfoDatabase = null;
            profileInfoDatabase = null;
            messageDatabase = null;
            androidMessageDatabase = null;
            groupMessageFtsDatabase = null;

            return new PreparedDatabaseConfig(
                platformType,
                CreateQQNTDatabaseConfig(config, platformType),
                ntDatabases,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
        }
        catch
        {
            groupInfoDatabase?.Dispose();
            profileInfoDatabase?.Dispose();
            messageDatabase?.Dispose();
            androidMessageDatabase?.Dispose();
            groupMessageFtsDatabase?.Dispose();
            throw;
        }
    }

    private static PreparedDatabaseConfig PreparePCQQ(PCQQDatabaseConfig config)
    {
        PCQQMessageReader? messageDatabase = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(config.MessageDbPath) &&
                !string.IsNullOrWhiteSpace(config.MessageDbKey))
            {
                messageDatabase = CreatePCQQMessageDatabase(
                    config.MessageDbPath,
                    config.MessageDbKey,
                    config.InfoDbPath,
                    config.InfoDbKey);
            }

            return new PreparedDatabaseConfig(
                DatabasePlatformType.PCQQ,
                CreatePCQQDatabaseConfig(config),
                null,
                messageDatabase,
                null,
                null,
                config.DataPath,
                null,
                null,
                null);
        }
        catch
        {
            messageDatabase?.Dispose();
            throw;
        }
    }

    private static PreparedDatabaseConfig PrepareAndroidMobileQQ(AndroidMobileQQDatabaseConfig config)
    {
        AndroidMobileQQMessageReader? messageDatabase = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(config.RootPath) &&
                !string.IsNullOrWhiteSpace(config.SelfUin))
            {
                messageDatabase = CreateAndroidMobileQQMessageDatabase(config.RootPath, config.SelfUin);
            }

            return new PreparedDatabaseConfig(
                DatabasePlatformType.AndroidMobileQQ,
                CreateAndroidMobileQQDatabaseConfig(config),
                null,
                null,
                messageDatabase,
                null,
                null,
                config.MobileQQPath,
                config.ChatPicPath,
                null);
        }
        catch
        {
            messageDatabase?.Dispose();
            throw;
        }
    }

    private static PreparedDatabaseConfig PrepareIcalingua(IcalinguaDatabaseConfig config)
    {
        IcalinguaMessageReader? messageDatabase = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(config.DatabasePath))
            {
                messageDatabase = CreateIcalinguaMessageDatabase(config.DatabasePath, config.DataPath);
            }

            return new PreparedDatabaseConfig(
                DatabasePlatformType.Icalingua,
                CreateIcalinguaDatabaseConfig(config),
                null,
                null,
                null,
                messageDatabase,
                null,
                null,
                null,
                config.DataPath);
        }
        catch
        {
            messageDatabase?.Dispose();
            throw;
        }
    }

    private static QQGroupInfoReader CreateGroupInfoDatabase(string databasePath, string? password)
    {
        var database = password is null
            ? new QQGroupInfoReader(databasePath)
            : new QQGroupInfoReader(databasePath, password);
        database.Initialize();
        return database;
    }

    private static QQMessageReader CreateMessageDatabase(string databasePath, string? password)
    {
        var database = password is null
            ? new QQMessageReader(databasePath)
            : new QQMessageReader(databasePath, password);
        database.Initialize();
        return database;
    }

    private static QQAndroidMessageReader CreateAndroidMessageDatabase(string databasePath, string? password)
    {
        var database = password is null
            ? new QQAndroidMessageReader(databasePath)
            : new QQAndroidMessageReader(databasePath, password);
        database.Initialize();
        return database;
    }

    private static QQProfileInfoReader CreateProfileInfoDatabase(string databasePath, string? password)
    {
        var database = password is null
            ? new QQProfileInfoReader(databasePath)
            : new QQProfileInfoReader(databasePath, password);
        database.Initialize();
        return database;
    }

    private static QQGroupMessageFtsReader CreateGroupMessageFtsDatabase(string databasePath, string? password)
    {
        var database = password is null
            ? new QQGroupMessageFtsReader(databasePath)
            : new QQGroupMessageFtsReader(databasePath, password);
        database.Initialize();
        return database;
    }

    private static PCQQMessageReader CreatePCQQMessageDatabase(
        string databasePath,
        string key,
        string? infoDbPath,
        string? infoDbKey)
    {
        var database = new PCQQMessageReader(databasePath, key, infoDbPath, infoDbKey);
        database.Initialize();
        return database;
    }

    private static AndroidMobileQQMessageReader CreateAndroidMobileQQMessageDatabase(string rootPath, string selfUin)
    {
        var database = new AndroidMobileQQMessageReader(rootPath, selfUin);
        database.Initialize();
        return database;
    }

    private static IcalinguaMessageReader CreateIcalinguaMessageDatabase(string databasePath, string? dataPath)
    {
        var database = new IcalinguaMessageReader(databasePath, dataPath);
        database.Initialize();
        return database;
    }

    private static DatabaseConfig CreateQQNTDatabaseConfig(QQNTDatabaseConfig config, DatabasePlatformType platformType)
    {
        if (platformType is DatabasePlatformType.AndroidQQNT)
        {
            var android = config as AndroidQQNTDatabaseConfig;
            return new DatabaseConfig
            {
                Type = DatabasePlatformType.AndroidQQNT,
                AndroidQQNT = new AndroidQQNTDatabaseConfig
                {
                    MobileQQPath = android?.MobileQQPath,
                    ChatPicPath = android?.ChatPicPath,
                    NtUid = android?.NtUid,
                    Rand = android?.Rand,
                    NtDataPath = config.NtDataPath,
                    MessageDbPath = config.MessageDbPath,
                    MessageDbPassword = config.MessageDbPassword,
                    GroupInfoDbPath = config.GroupInfoDbPath,
                    GroupInfoDbPassword = config.GroupInfoDbPassword,
                    GroupMessageFtsDbPath = config.GroupMessageFtsDbPath,
                    GroupMessageFtsDbPassword = config.GroupMessageFtsDbPassword,
                    ProfileInfoDbPath = config.ProfileInfoDbPath,
                    ProfileInfoDbPassword = config.ProfileInfoDbPassword,
                },
            };
        }

        return new DatabaseConfig
        {
            Type = DatabasePlatformType.QQNT,
            QQNT = new QQNTDatabaseConfig
            {
                NtDataPath = config.NtDataPath,
                MessageDbPath = config.MessageDbPath,
                MessageDbPassword = config.MessageDbPassword,
                GroupInfoDbPath = config.GroupInfoDbPath,
                GroupInfoDbPassword = config.GroupInfoDbPassword,
                GroupMessageFtsDbPath = config.GroupMessageFtsDbPath,
                GroupMessageFtsDbPassword = config.GroupMessageFtsDbPassword,
                ProfileInfoDbPath = config.ProfileInfoDbPath,
                ProfileInfoDbPassword = config.ProfileInfoDbPassword,
            },
        };
    }

    internal static DatabaseConfig CreatePCQQDatabaseConfig(PCQQDatabaseConfig config)
    {
        return new DatabaseConfig
        {
            Type = DatabasePlatformType.PCQQ,
            PCQQ = new PCQQDatabaseConfig
            {
                MessageDbPath = config.MessageDbPath,
                MessageDbKey = config.MessageDbKey,
                InfoDbPath = config.InfoDbPath,
                InfoDbKey = config.InfoDbKey,
                DataPath = config.DataPath,
            },
        };
    }

    internal static DatabaseConfig CreateAndroidMobileQQDatabaseConfig(AndroidMobileQQDatabaseConfig config)
    {
        return new DatabaseConfig
        {
            Type = DatabasePlatformType.AndroidMobileQQ,
            AndroidMobileQQ = new AndroidMobileQQDatabaseConfig
            {
                RootPath = config.RootPath,
                SelfUin = config.SelfUin,
                MobileQQPath = config.MobileQQPath,
                ChatPicPath = config.ChatPicPath,
            },
        };
    }

    internal static DatabaseConfig CreateIcalinguaDatabaseConfig(IcalinguaDatabaseConfig config)
    {
        return new DatabaseConfig
        {
            Type = DatabasePlatformType.Icalingua,
            Icalingua = new IcalinguaDatabaseConfig
            {
                DatabasePath = config.DatabasePath,
                DataPath = config.DataPath,
            },
        };
    }
}
