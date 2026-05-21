using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using QQDatabaseReader;
using QQDatabaseReader.Database;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.Services;

public class QQDatabaseService
{
    public event Action<IQQDatabase>? DatabaseAdded;
    public event Func<IQQDatabase, Task>? DatabaseAddedAsync;
    public event Action<IQQDatabase>? DatabaseRemoved;
    public event Action? DatabaseGroupsChanged;

    public QQGroupInfoReader? GroupInfoDatabase { get; private set; }

    public QQMessageReader? MessageDatabase { get; private set; }

    public QQAndroidMessageReader? AndroidMessageDatabase { get; private set; }

    public PCQQMessageReader? PCQQMessageDatabase { get; private set; }

    public QQGroupMessageFtsReader? GroupMessageFtsDatabase { get; private set; }

    public QQProfileInfoReader? ProfileInfoDatabase { get; private set; }

    public string? NtDataPath { get; private set; }

    public string? AndroidMobileQQPath { get; private set; }

    public string? PCQQDataPath { get; private set; }

    public ObservableCollection<LoadedDatabaseGroup> DatabaseGroups { get; } = new();

    private DatabasePlatformType _ntPlatformType = DatabasePlatformType.QQNT;
    private DatabaseConfig? _currentQQNTConfig;
    private DatabaseConfig? _currentPCQQConfig;
    
    public QQGroupInfoReader LoadGroupInfoDatabase(string grouInfoDb, string? password = null)
    {
        QQGroupInfoReader groupInfoDatabase;
        if (password is null)
        {
            groupInfoDatabase = new(grouInfoDb);
        }
        else
        {
            groupInfoDatabase = new(grouInfoDb, password);
        }
        groupInfoDatabase.Initialize();
        var previousDatabase = GroupInfoDatabase;
        GroupInfoDatabase = groupInfoDatabase;
        if (previousDatabase is not null)
            NotifyDatabaseRemoved(previousDatabase);

        NotifyDatabaseAdded(groupInfoDatabase, runAsyncHandlers: true);
        RebuildDatabaseGroups();

        return groupInfoDatabase;
    }

    public QQMessageReader LoadMessageDatabase(string messageDb, string? password = null, string? ntDataPath = null)
    {
        NtDataPath = string.IsNullOrWhiteSpace(ntDataPath)
            ? null
            : ntDataPath;

        QQMessageReader messageDatabase;
        if (password is null)
        {
            messageDatabase = new(messageDb);
        }
        else
        {
            messageDatabase = new(messageDb, password);
        }
        messageDatabase.Initialize();
        var previousDatabase = MessageDatabase;
        MessageDatabase = messageDatabase;
        if (previousDatabase is not null)
            NotifyDatabaseRemoved(previousDatabase);

        NotifyDatabaseAdded(messageDatabase, runAsyncHandlers: true);
        RebuildDatabaseGroups();

        return messageDatabase;
    }

    public QQAndroidMessageReader LoadAndroidMessageDatabase(
        string messageDb,
        string? password = null,
        string? mobileQQPath = null)
    {
        AndroidMobileQQPath = string.IsNullOrWhiteSpace(mobileQQPath)
            ? null
            : mobileQQPath;

        QQAndroidMessageReader messageDatabase;
        if (password is null)
        {
            messageDatabase = new(messageDb);
        }
        else
        {
            messageDatabase = new(messageDb, password);
        }
        messageDatabase.Initialize();
        var previousDatabase = AndroidMessageDatabase;
        AndroidMessageDatabase = messageDatabase;
        if (previousDatabase is not null)
            NotifyDatabaseRemoved(previousDatabase);

        NotifyDatabaseAdded(messageDatabase, runAsyncHandlers: true);
        RebuildDatabaseGroups();

        return messageDatabase;
    }

    public QQGroupMessageFtsReader LoadGroupMessageFtsDatabase(string groupMessageFtsDb, string? password = null)
    {
        QQGroupMessageFtsReader groupMessageFtsDatabase;
        if (password is null)
        {
            groupMessageFtsDatabase = new(groupMessageFtsDb);
        }
        else
        {
            groupMessageFtsDatabase = new(groupMessageFtsDb, password);
        }
        groupMessageFtsDatabase.Initialize();
        var previousDatabase = GroupMessageFtsDatabase;
        GroupMessageFtsDatabase = groupMessageFtsDatabase;
        if (previousDatabase is not null)
            NotifyDatabaseRemoved(previousDatabase);

        NotifyDatabaseAdded(groupMessageFtsDatabase, runAsyncHandlers: true);
        RebuildDatabaseGroups();

        return groupMessageFtsDatabase;
    }

    public QQProfileInfoReader LoadProfileInfoDatabase(string profileInfoDb, string? password = null)
    {
        QQProfileInfoReader profileInfoDatabase;
        if (password is null)
        {
            profileInfoDatabase = new(profileInfoDb);
        }
        else
        {
            profileInfoDatabase = new(profileInfoDb, password);
        }

        profileInfoDatabase.Initialize();
        var previousDatabase = ProfileInfoDatabase;
        ProfileInfoDatabase = profileInfoDatabase;
        if (previousDatabase is not null)
            NotifyDatabaseRemoved(previousDatabase);

        NotifyDatabaseAdded(profileInfoDatabase, runAsyncHandlers: true);
        RebuildDatabaseGroups();

        return profileInfoDatabase;
    }

    public PCQQMessageReader LoadPCQQMessageDatabase(
        string messageDb,
        string key,
        string? infoDbPath = null,
        string? infoDbKey = null,
        string? dataPath = null)
    {
        PCQQDataPath = string.IsNullOrWhiteSpace(dataPath)
            ? null
            : dataPath;

        var messageDatabase = new PCQQMessageReader(messageDb, key, infoDbPath, infoDbKey);
        messageDatabase.Initialize();
        var previousDatabase = PCQQMessageDatabase;
        PCQQMessageDatabase = messageDatabase;
        if (previousDatabase is not null)
            NotifyDatabaseRemoved(previousDatabase);

        NotifyDatabaseAdded(messageDatabase, runAsyncHandlers: true);
        RebuildDatabaseGroups();

        return messageDatabase;
    }

    public void LoadDatabaseConfig(DatabaseConfig config)
    {
        switch (config.Type)
        {
            case DatabasePlatformType.PCQQ when config.PCQQ is { } pcqq:
                ReplacePCQQDatabase(pcqq);
                break;
            case DatabasePlatformType.AndroidQQNT when config.AndroidQQNT is { } android:
                ReplaceQQNTDatabases(android, DatabasePlatformType.AndroidQQNT);
                break;
            case DatabasePlatformType.QQNT when config.QQNT is { } qqnt:
                ReplaceQQNTDatabases(qqnt, DatabasePlatformType.QQNT);
                break;
        }
    }

    internal Task<PreparedDatabaseConfig> PrepareDatabaseConfigAsync(DatabaseConfig config)
    {
        return Task.Run(() => PrepareDatabaseConfig(config));
    }

    internal async Task ApplyPreparedDatabaseConfigAsync(PreparedDatabaseConfig prepared)
    {
        switch (prepared.PlatformType)
        {
            case DatabasePlatformType.PCQQ:
                await ApplyPreparedPCQQDatabaseAsync(prepared);
                break;
            case DatabasePlatformType.QQNT:
            case DatabasePlatformType.AndroidQQNT:
                await ApplyPreparedQQNTDatabasesAsync(prepared);
                break;
        }

        prepared.Detach();
    }

    public DatabaseConfig? CreateConfigForGroup(LoadedDatabaseGroup group)
    {
        return group.Config ?? group.PlatformType switch
        {
            DatabasePlatformType.PCQQ => CreateCurrentPCQQConfig(),
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

        return config;
    }

    public void ReplaceDatabaseGroup(LoadedDatabaseGroup group, DatabaseConfig config)
    {
        RemoveDatabaseGroup(group);
        LoadDatabaseConfig(config);
    }

    public void RemoveDatabaseGroup(LoadedDatabaseGroup group)
    {
        switch (group.PlatformType)
        {
            case DatabasePlatformType.PCQQ:
                RemovePCQQDatabase();
                break;
            case DatabasePlatformType.QQNT:
            case DatabasePlatformType.AndroidQQNT:
                RemoveQQNTDatabases();
                break;
        }
    }

    public void ClearDatabases()
    {
        var databases = new List<IQQDatabase>();
        if (PCQQMessageDatabase is not null)
            databases.Add(PCQQMessageDatabase);
        if (GroupMessageFtsDatabase is not null)
            databases.Add(GroupMessageFtsDatabase);
        if (ProfileInfoDatabase is not null)
            databases.Add(ProfileInfoDatabase);
        if (MessageDatabase is not null)
            databases.Add(MessageDatabase);
        if (AndroidMessageDatabase is not null)
            databases.Add(AndroidMessageDatabase);
        if (GroupInfoDatabase is not null)
            databases.Add(GroupInfoDatabase);

        GroupMessageFtsDatabase = null;
        ProfileInfoDatabase = null;
        PCQQMessageDatabase = null;
        MessageDatabase = null;
        AndroidMessageDatabase = null;
        GroupInfoDatabase = null;
        NtDataPath = null;
        AndroidMobileQQPath = null;
        PCQQDataPath = null;
        _ntPlatformType = DatabasePlatformType.QQNT;
        _currentQQNTConfig = null;
        _currentPCQQConfig = null;

        foreach (var database in databases)
        {
            NotifyDatabaseRemoved(database);
        }

        RebuildDatabaseGroups();
    }

    public void RemoveDatabase(IQQDatabase qqDatabase)
    {
        if (qqDatabase.Equals(GroupInfoDatabase))
        {
            GroupInfoDatabase = null;
            ClearQQNTConfigItem(LoadedDatabaseItemKind.GroupInfoDb);
            NotifyDatabaseRemoved(qqDatabase);
            RebuildDatabaseGroups();
        }
        else if (qqDatabase.Equals(MessageDatabase))
        {
            MessageDatabase = null;
            NtDataPath = null;
            ClearQQNTConfigItem(LoadedDatabaseItemKind.NtMessageDb);
            NotifyDatabaseRemoved(qqDatabase);
            RebuildDatabaseGroups();
        }
        else if (qqDatabase.Equals(AndroidMessageDatabase))
        {
            AndroidMessageDatabase = null;
            AndroidMobileQQPath = null;
            ClearQQNTConfigItem(LoadedDatabaseItemKind.NtMessageDb);
            NotifyDatabaseRemoved(qqDatabase);
            RebuildDatabaseGroups();
        }
        else if (qqDatabase.Equals(GroupMessageFtsDatabase))
        {
            GroupMessageFtsDatabase = null;
            ClearQQNTConfigItem(LoadedDatabaseItemKind.GroupMessageFtsDb);
            NotifyDatabaseRemoved(qqDatabase);
            RebuildDatabaseGroups();
        }
        else if (qqDatabase.Equals(ProfileInfoDatabase))
        {
            ProfileInfoDatabase = null;
            ClearQQNTConfigItem(LoadedDatabaseItemKind.ProfileInfoDb);
            NotifyDatabaseRemoved(qqDatabase);
            RebuildDatabaseGroups();
        }
        else if (qqDatabase.Equals(PCQQMessageDatabase))
        {
            PCQQMessageDatabase = null;
            PCQQDataPath = null;
            ClearPCQQConfigItem(LoadedDatabaseItemKind.PCQQMessageDb);
            NotifyDatabaseRemoved(qqDatabase);
            RebuildDatabaseGroups();
        }
    }

    public void RemoveDatabaseItem(LoadedDatabaseItem item)
    {
        if (item.Database is { } database)
        {
            RemoveDatabase(database);
            return;
        }

        switch (item.Kind)
        {
            case LoadedDatabaseItemKind.PCQQInfoDb:
            case LoadedDatabaseItemKind.PCQQDataPath:
                ClearPCQQConfigItem(item.Kind);
                break;
            case LoadedDatabaseItemKind.NtDataPath:
            case LoadedDatabaseItemKind.MobileQQPath:
            case LoadedDatabaseItemKind.NtMessageDb:
            case LoadedDatabaseItemKind.GroupInfoDb:
            case LoadedDatabaseItemKind.ProfileInfoDb:
            case LoadedDatabaseItemKind.GroupMessageFtsDb:
                ClearQQNTConfigItem(item.Kind);
                break;
        }

        RebuildDatabaseGroups();
    }

    private void ReplaceQQNTDatabases(QQNTDatabaseConfig config, DatabasePlatformType platformType)
    {
        _currentQQNTConfig = CreateQQNTDatabaseConfig(config, platformType);
        _ntPlatformType = platformType;
        RemoveQQNTDatabases(clearConfig: false);

        if (!string.IsNullOrWhiteSpace(config.GroupInfoDbPath))
            LoadGroupInfoDatabase(config.GroupInfoDbPath, config.GroupInfoDbPassword);

        if (!string.IsNullOrWhiteSpace(config.ProfileInfoDbPath))
            LoadProfileInfoDatabase(config.ProfileInfoDbPath, config.ProfileInfoDbPassword);

        if (!string.IsNullOrWhiteSpace(config.MessageDbPath) &&
            platformType is DatabasePlatformType.AndroidQQNT)
        {
            var mobileQQPath = config is AndroidQQNTDatabaseConfig android
                ? android.MobileQQPath
                : null;
            LoadAndroidMessageDatabase(config.MessageDbPath, config.MessageDbPassword, mobileQQPath);
        }
        else if (!string.IsNullOrWhiteSpace(config.MessageDbPath))
        {
            LoadMessageDatabase(config.MessageDbPath, config.MessageDbPassword, config.NtDataPath);
        }

        if (!string.IsNullOrWhiteSpace(config.GroupMessageFtsDbPath))
            LoadGroupMessageFtsDatabase(config.GroupMessageFtsDbPath, config.GroupMessageFtsDbPassword);

        RebuildDatabaseGroups();
    }

    private async Task ApplyPreparedQQNTDatabasesAsync(PreparedDatabaseConfig prepared)
    {
        _currentQQNTConfig = prepared.Config;
        _ntPlatformType = prepared.PlatformType;
        RemoveQQNTDatabases(clearConfig: false);

        NtDataPath = prepared.NtDataPath;
        AndroidMobileQQPath = prepared.AndroidMobileQQPath;
        GroupInfoDatabase = prepared.GroupInfoDatabase;
        ProfileInfoDatabase = prepared.ProfileInfoDatabase;
        MessageDatabase = prepared.MessageDatabase;
        AndroidMessageDatabase = prepared.AndroidMessageDatabase;
        GroupMessageFtsDatabase = prepared.GroupMessageFtsDatabase;

        if (GroupInfoDatabase is not null)
            await NotifyDatabaseAddedAsync(GroupInfoDatabase);
        if (ProfileInfoDatabase is not null)
            await NotifyDatabaseAddedAsync(ProfileInfoDatabase);
        if (MessageDatabase is not null)
            await NotifyDatabaseAddedAsync(MessageDatabase);
        if (AndroidMessageDatabase is not null)
            await NotifyDatabaseAddedAsync(AndroidMessageDatabase);
        if (GroupMessageFtsDatabase is not null)
            await NotifyDatabaseAddedAsync(GroupMessageFtsDatabase);

        RebuildDatabaseGroups();
    }

    private void ReplacePCQQDatabase(PCQQDatabaseConfig config)
    {
        _currentPCQQConfig = CreatePCQQDatabaseConfig(config);
        RemovePCQQDatabase(clearConfig: false);
        if (!string.IsNullOrWhiteSpace(config.MessageDbPath) &&
            !string.IsNullOrWhiteSpace(config.MessageDbKey))
        {
            LoadPCQQMessageDatabase(
                config.MessageDbPath,
                config.MessageDbKey,
                config.InfoDbPath,
                config.InfoDbKey,
                config.DataPath);
        }
    }

    private async Task ApplyPreparedPCQQDatabaseAsync(PreparedDatabaseConfig prepared)
    {
        _currentPCQQConfig = prepared.Config;
        RemovePCQQDatabase(clearConfig: false);

        PCQQDataPath = prepared.PCQQDataPath;
        PCQQMessageDatabase = prepared.PCQQMessageDatabase;
        if (PCQQMessageDatabase is not null)
            await NotifyDatabaseAddedAsync(PCQQMessageDatabase);

        RebuildDatabaseGroups();
    }

    private static PreparedDatabaseConfig PrepareDatabaseConfig(DatabaseConfig config)
    {
        return config.Type switch
        {
            DatabasePlatformType.PCQQ when config.PCQQ is { } pcqq => PreparePCQQDatabase(pcqq),
            DatabasePlatformType.AndroidQQNT when config.AndroidQQNT is { } android => PrepareQQNTDatabases(android, DatabasePlatformType.AndroidQQNT),
            DatabasePlatformType.QQNT when config.QQNT is { } qqnt => PrepareQQNTDatabases(qqnt, DatabasePlatformType.QQNT),
            _ => PreparedDatabaseConfig.Empty(config.Type),
        };
    }

    private static PreparedDatabaseConfig PrepareQQNTDatabases(QQNTDatabaseConfig config, DatabasePlatformType platformType)
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

            return new PreparedDatabaseConfig(
                platformType,
                CreateQQNTDatabaseConfig(config, platformType),
                groupInfoDatabase,
                profileInfoDatabase,
                messageDatabase,
                androidMessageDatabase,
                groupMessageFtsDatabase,
                null,
                config.NtDataPath,
                config is AndroidQQNTDatabaseConfig android ? android.MobileQQPath : null,
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

    private static PreparedDatabaseConfig PreparePCQQDatabase(PCQQDatabaseConfig config)
    {
        PCQQMessageReader? messageDatabase = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(config.MessageDbPath) &&
                !string.IsNullOrWhiteSpace(config.MessageDbKey))
            {
                messageDatabase = new PCQQMessageReader(
                    config.MessageDbPath,
                    config.MessageDbKey,
                    config.InfoDbPath,
                    config.InfoDbKey);
                messageDatabase.Initialize();
            }

            return new PreparedDatabaseConfig(
                DatabasePlatformType.PCQQ,
                CreatePCQQDatabaseConfig(config),
                null,
                null,
                null,
                null,
                null,
                messageDatabase,
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

    private void RemoveQQNTDatabases(bool clearConfig = true)
    {
        var databases = new List<IQQDatabase>();
        if (GroupMessageFtsDatabase is not null)
            databases.Add(GroupMessageFtsDatabase);
        if (ProfileInfoDatabase is not null)
            databases.Add(ProfileInfoDatabase);
        if (MessageDatabase is not null)
            databases.Add(MessageDatabase);
        if (AndroidMessageDatabase is not null)
            databases.Add(AndroidMessageDatabase);
        if (GroupInfoDatabase is not null)
            databases.Add(GroupInfoDatabase);

        GroupMessageFtsDatabase = null;
        ProfileInfoDatabase = null;
        MessageDatabase = null;
        AndroidMessageDatabase = null;
        GroupInfoDatabase = null;
        NtDataPath = null;
        AndroidMobileQQPath = null;
        if (clearConfig)
        {
            _ntPlatformType = DatabasePlatformType.QQNT;
            _currentQQNTConfig = null;
        }

        foreach (var database in databases)
        {
            NotifyDatabaseRemoved(database);
        }

        RebuildDatabaseGroups();
    }

    private void RemovePCQQDatabase(bool clearConfig = true)
    {
        if (clearConfig)
            _currentPCQQConfig = null;

        if (PCQQMessageDatabase is null)
        {
            RebuildDatabaseGroups();
            return;
        }

        var database = PCQQMessageDatabase;
        PCQQMessageDatabase = null;
        PCQQDataPath = null;
        NotifyDatabaseRemoved(database);
        RebuildDatabaseGroups();
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

    private static DatabaseConfig CreatePCQQDatabaseConfig(PCQQDatabaseConfig config)
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

    private DatabaseConfig? CreateCurrentQQNTConfig(DatabasePlatformType platformType)
    {
        if (MessageDatabase is null &&
            AndroidMessageDatabase is null &&
            GroupInfoDatabase is null &&
            GroupMessageFtsDatabase is null &&
            ProfileInfoDatabase is null)
        {
            return null;
        }

        var qqnt = new QQNTDatabaseConfig
        {
            NtDataPath = NtDataPath,
            MessageDbPath = MessageDatabase?.RawDatabase.DatabaseFilePath ?? AndroidMessageDatabase?.RawDatabase.DatabaseFilePath,
            MessageDbPassword = MessageDatabase?.RawDatabase.CipherPassword ?? AndroidMessageDatabase?.RawDatabase.CipherPassword,
            GroupInfoDbPath = GroupInfoDatabase?.RawDatabase.DatabaseFilePath,
            GroupInfoDbPassword = GroupInfoDatabase?.RawDatabase.CipherPassword,
            GroupMessageFtsDbPath = GroupMessageFtsDatabase?.RawDatabase.DatabaseFilePath,
            GroupMessageFtsDbPassword = GroupMessageFtsDatabase?.RawDatabase.CipherPassword,
            ProfileInfoDbPath = ProfileInfoDatabase?.RawDatabase.DatabaseFilePath,
            ProfileInfoDbPassword = ProfileInfoDatabase?.RawDatabase.CipherPassword,
        };

        if (platformType is DatabasePlatformType.AndroidQQNT)
        {
            return new DatabaseConfig
            {
                Type = DatabasePlatformType.AndroidQQNT,
                AndroidQQNT = new AndroidQQNTDatabaseConfig
                {
                    MobileQQPath = AndroidMobileQQPath,
                    MessageDbPath = qqnt.MessageDbPath,
                    MessageDbPassword = qqnt.MessageDbPassword,
                    GroupInfoDbPath = qqnt.GroupInfoDbPath,
                    GroupInfoDbPassword = qqnt.GroupInfoDbPassword,
                    GroupMessageFtsDbPath = qqnt.GroupMessageFtsDbPath,
                    GroupMessageFtsDbPassword = qqnt.GroupMessageFtsDbPassword,
                    ProfileInfoDbPath = qqnt.ProfileInfoDbPath,
                    ProfileInfoDbPassword = qqnt.ProfileInfoDbPassword,
                },
            };
        }

        return new DatabaseConfig
        {
            Type = DatabasePlatformType.QQNT,
            QQNT = qqnt,
        };
    }

    private DatabaseConfig? CreateCurrentPCQQConfig()
    {
        if (PCQQMessageDatabase is not { } pcqqMessageDb)
            return null;

        return new DatabaseConfig
        {
            Type = DatabasePlatformType.PCQQ,
            PCQQ = new PCQQDatabaseConfig
            {
                MessageDbPath = pcqqMessageDb.RawDatabase.DatabaseFilePath,
                MessageDbKey = pcqqMessageDb.RawDatabase.PCQQKey is { } key
                    ? "hex:" + Convert.ToHexString(key)
                    : null,
                InfoDbPath = pcqqMessageDb.InfoDbPath,
                InfoDbKey = pcqqMessageDb.InfoDatabase?.Key is { } infoKey
                    ? "hex:" + Convert.ToHexString(infoKey)
                    : pcqqMessageDb.InfoDbKey,
                DataPath = PCQQDataPath,
            },
        };
    }

    private void RebuildDatabaseGroups()
    {
        DatabaseGroups.Clear();

        var pcqqItems = _currentPCQQConfig?.PCQQ is { } pcqqConfig
            ? CreatePCQQDisplayItems(pcqqConfig)
            : CreateLoadedPCQQDisplayItems();
        if (pcqqItems.Count > 0)
            DatabaseGroups.Add(new LoadedDatabaseGroup(DatabasePlatformType.PCQQ, pcqqItems, _currentPCQQConfig ?? CreateCurrentPCQQConfig()));

        var qqntItems = _currentQQNTConfig is { } qqntConfig
            ? CreateQQNTDisplayItems(qqntConfig)
            : CreateLoadedQQNTDisplayItems();
        if (qqntItems.Count > 0)
            DatabaseGroups.Add(new LoadedDatabaseGroup(_ntPlatformType, qqntItems, _currentQQNTConfig ?? CreateCurrentQQNTConfig(_ntPlatformType)));

        DatabaseGroupsChanged?.Invoke();
    }

    private List<LoadedDatabaseItem> CreatePCQQDisplayItems(PCQQDatabaseConfig config)
    {
        var items = new List<LoadedDatabaseItem>();
        AddDisplayItem(items, "Msg3.0.db", config.MessageDbPath, PCQQMessageDatabase, LoadedDatabaseItemKind.PCQQMessageDb);
        AddDisplayItem(items, "Info.db", config.InfoDbPath, null, LoadedDatabaseItemKind.PCQQInfoDb);
        AddDisplayItem(items, "数据目录", config.DataPath, null, LoadedDatabaseItemKind.PCQQDataPath);
        return items;
    }

    private List<LoadedDatabaseItem> CreateLoadedPCQQDisplayItems()
    {
        var items = new List<LoadedDatabaseItem>();
        if (PCQQMessageDatabase is not null)
            items.Add(new LoadedDatabaseItem(PCQQMessageDatabase));
        if (PCQQMessageDatabase?.InfoDatabase is { } infoDb)
            items.Add(new LoadedDatabaseItem("Info.db", infoDb.InfoDbPath, null, LoadedDatabaseItemKind.PCQQInfoDb));
        if (!string.IsNullOrWhiteSpace(PCQQDataPath))
            items.Add(new LoadedDatabaseItem("数据目录", PCQQDataPath, null, LoadedDatabaseItemKind.PCQQDataPath));
        return items;
    }

    private List<LoadedDatabaseItem> CreateQQNTDisplayItems(DatabaseConfig config)
    {
        var items = new List<LoadedDatabaseItem>();
        var qqnt = config.Type is DatabasePlatformType.AndroidQQNT
            ? config.AndroidQQNT
            : config.QQNT;
        if (qqnt is null)
            return items;

        AddDisplayItem(items, "nt_msg.db", qqnt.MessageDbPath, config.Type is DatabasePlatformType.AndroidQQNT ? AndroidMessageDatabase : MessageDatabase, LoadedDatabaseItemKind.NtMessageDb);
        AddDisplayItem(items, "group_info.db", qqnt.GroupInfoDbPath, GroupInfoDatabase, LoadedDatabaseItemKind.GroupInfoDb);
        AddDisplayItem(items, "profile_info.db", qqnt.ProfileInfoDbPath, ProfileInfoDatabase, LoadedDatabaseItemKind.ProfileInfoDb);
        AddDisplayItem(items, "group_msg_fts.db", qqnt.GroupMessageFtsDbPath, GroupMessageFtsDatabase, LoadedDatabaseItemKind.GroupMessageFtsDb);

        if (config.Type is DatabasePlatformType.AndroidQQNT && config.AndroidQQNT is { } android)
            AddDisplayItem(items, "MobileQQ", android.MobileQQPath, null, LoadedDatabaseItemKind.MobileQQPath);
        else
            AddDisplayItem(items, "nt_data", qqnt.NtDataPath, null, LoadedDatabaseItemKind.NtDataPath);

        return items;
    }

    private List<LoadedDatabaseItem> CreateLoadedQQNTDisplayItems()
    {
        var items = new List<LoadedDatabaseItem>();
        if (MessageDatabase is not null)
            items.Add(new LoadedDatabaseItem(MessageDatabase));
        if (AndroidMessageDatabase is not null)
            items.Add(new LoadedDatabaseItem(AndroidMessageDatabase));
        if (GroupInfoDatabase is not null)
            items.Add(new LoadedDatabaseItem(GroupInfoDatabase));
        if (ProfileInfoDatabase is not null)
            items.Add(new LoadedDatabaseItem(ProfileInfoDatabase));
        if (GroupMessageFtsDatabase is not null)
            items.Add(new LoadedDatabaseItem(GroupMessageFtsDatabase));
        if (!string.IsNullOrWhiteSpace(AndroidMobileQQPath))
            items.Add(new LoadedDatabaseItem("MobileQQ", AndroidMobileQQPath, null, LoadedDatabaseItemKind.MobileQQPath));
        else if (!string.IsNullOrWhiteSpace(NtDataPath))
            items.Add(new LoadedDatabaseItem("nt_data", NtDataPath, null, LoadedDatabaseItemKind.NtDataPath));
        return items;
    }

    private static void AddDisplayItem(List<LoadedDatabaseItem> items, string name, string? path, IQQDatabase? database, LoadedDatabaseItemKind kind)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        items.Add(new LoadedDatabaseItem(name, path, database, kind));
    }

    private void NotifyDatabaseRemoved(IQQDatabase database)
    {
        DatabaseRemoved?.Invoke(database);
        database.Dispose();
    }

    private void NotifyDatabaseAdded(IQQDatabase database, bool runAsyncHandlers = false)
    {
        DatabaseAdded?.Invoke(database);
        if (runAsyncHandlers)
            _ = RunDatabaseAddedAsyncHandlersAsync(database);
    }

    private async Task NotifyDatabaseAddedAsync(IQQDatabase database)
    {
        NotifyDatabaseAdded(database);
        await RunDatabaseAddedAsyncHandlersAsync(database);
    }

    private async Task RunDatabaseAddedAsyncHandlersAsync(IQQDatabase database)
    {
        if (DatabaseAddedAsync is null)
            return;

        foreach (Func<IQQDatabase, Task> handler in DatabaseAddedAsync.GetInvocationList())
        {
            await handler(database);
        }
    }
}

public sealed class LoadedDatabaseGroup
{
    public LoadedDatabaseGroup(DatabasePlatformType platformType, IEnumerable<LoadedDatabaseItem> items, DatabaseConfig? config = null)
    {
        PlatformType = platformType;
        Name = platformType.ToString();
        Items = new ObservableCollection<LoadedDatabaseItem>(items);
        Config = config;
    }

    public DatabasePlatformType PlatformType { get; }

    public string Name { get; }

    public ObservableCollection<LoadedDatabaseItem> Items { get; }

    public DatabaseConfig? Config { get; }
}

public sealed class LoadedDatabaseItem
{
    public LoadedDatabaseItem(IQQDatabase database)
    {
        Database = database;
        Name = GetDatabaseDisplayName(database.DatabaseType);
        FilePath = database.DatabaseFilePath;
        Kind = GetDatabaseItemKind(database.DatabaseType);
    }

    public LoadedDatabaseItem(string name, string filePath, IQQDatabase? database, LoadedDatabaseItemKind kind = LoadedDatabaseItemKind.Other)
    {
        Name = name;
        FilePath = filePath;
        Database = database;
        Kind = kind;
    }

    public string Name { get; }

    public string FilePath { get; }

    public IQQDatabase? Database { get; }

    public LoadedDatabaseItemKind Kind { get; }

    private static string GetDatabaseDisplayName(QQDatabaseType databaseType)
    {
        return databaseType switch
        {
            QQDatabaseType.GroupInfo => "group_info.db",
            QQDatabaseType.GroupMessageFts => "group_msg_fts.db",
            QQDatabaseType.ProfileInfo => "profile_info.db",
            QQDatabaseType.PCQQMessage => "Msg3.0.db",
            QQDatabaseType.AndroidMessage => "nt_msg.db",
            QQDatabaseType.Message => "nt_msg.db",
            _ => databaseType.ToString(),
        };
    }

    private static LoadedDatabaseItemKind GetDatabaseItemKind(QQDatabaseType databaseType)
    {
        return databaseType switch
        {
            QQDatabaseType.GroupInfo => LoadedDatabaseItemKind.GroupInfoDb,
            QQDatabaseType.GroupMessageFts => LoadedDatabaseItemKind.GroupMessageFtsDb,
            QQDatabaseType.ProfileInfo => LoadedDatabaseItemKind.ProfileInfoDb,
            QQDatabaseType.PCQQMessage => LoadedDatabaseItemKind.PCQQMessageDb,
            QQDatabaseType.AndroidMessage => LoadedDatabaseItemKind.NtMessageDb,
            QQDatabaseType.Message => LoadedDatabaseItemKind.NtMessageDb,
            _ => LoadedDatabaseItemKind.Other,
        };
    }
}

public enum LoadedDatabaseItemKind
{
    Other,
    NtMessageDb,
    GroupInfoDb,
    ProfileInfoDb,
    GroupMessageFtsDb,
    NtDataPath,
    MobileQQPath,
    PCQQMessageDb,
    PCQQInfoDb,
    PCQQDataPath,
}

internal sealed class PreparedDatabaseConfig : IDisposable
{
    private bool _isDetached;

    public PreparedDatabaseConfig(
        DatabasePlatformType platformType,
        DatabaseConfig? config,
        QQGroupInfoReader? groupInfoDatabase,
        QQProfileInfoReader? profileInfoDatabase,
        QQMessageReader? messageDatabase,
        QQAndroidMessageReader? androidMessageDatabase,
        QQGroupMessageFtsReader? groupMessageFtsDatabase,
        PCQQMessageReader? pcqqMessageDatabase,
        string? ntDataPath,
        string? androidMobileQQPath,
        string? pcqqDataPath)
    {
        PlatformType = platformType;
        Config = config;
        GroupInfoDatabase = groupInfoDatabase;
        ProfileInfoDatabase = profileInfoDatabase;
        MessageDatabase = messageDatabase;
        AndroidMessageDatabase = androidMessageDatabase;
        GroupMessageFtsDatabase = groupMessageFtsDatabase;
        PCQQMessageDatabase = pcqqMessageDatabase;
        NtDataPath = ntDataPath;
        AndroidMobileQQPath = androidMobileQQPath;
        PCQQDataPath = pcqqDataPath;
    }

    public DatabasePlatformType PlatformType { get; }

    public DatabaseConfig? Config { get; }

    public QQGroupInfoReader? GroupInfoDatabase { get; }

    public QQProfileInfoReader? ProfileInfoDatabase { get; }

    public QQMessageReader? MessageDatabase { get; }

    public QQAndroidMessageReader? AndroidMessageDatabase { get; }

    public QQGroupMessageFtsReader? GroupMessageFtsDatabase { get; }

    public PCQQMessageReader? PCQQMessageDatabase { get; }

    public string? NtDataPath { get; }

    public string? AndroidMobileQQPath { get; }

    public string? PCQQDataPath { get; }

    public static PreparedDatabaseConfig Empty(DatabasePlatformType platformType) =>
        new(platformType, null, null, null, null, null, null, null, null, null, null);

    public void Detach()
    {
        _isDetached = true;
    }

    public void Dispose()
    {
        if (_isDetached)
            return;

        GroupMessageFtsDatabase?.Dispose();
        ProfileInfoDatabase?.Dispose();
        MessageDatabase?.Dispose();
        AndroidMessageDatabase?.Dispose();
        GroupInfoDatabase?.Dispose();
        PCQQMessageDatabase?.Dispose();
    }
}
