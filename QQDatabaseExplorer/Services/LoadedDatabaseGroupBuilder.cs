using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.Services;

internal sealed record LoadedDatabaseGroupBuildContext(
    DatabasePlatformType NtPlatformType,
    DatabaseConfig? CurrentQQNTConfig,
    DatabaseConfig? CurrentPCQQConfig,
    DatabaseConfig? CurrentAndroidMobileQQConfig,
    IReadOnlyList<DatabaseConfig> CurrentIcalinguaConfigs,
    DatabaseConfig? LoadedQQNTConfig,
    DatabaseConfig? LoadedPCQQConfig,
    DatabaseConfig? LoadedAndroidMobileQQConfig,
    DatabaseConfig? LoadedIcalinguaConfig,
    QQNtDatabaseRuntimeGroup QQNtDatabases,
    PCQQDatabaseRuntimeGroup PCQQDatabase,
    AndroidMobileQQDatabaseRuntimeGroup AndroidMobileQQDatabase,
    IcalinguaDatabaseRuntimeGroup IcalinguaDatabases);

internal static class LoadedDatabaseGroupBuildContextFactory
{
    public static LoadedDatabaseGroupBuildContext Create(
        DatabasePlatformType ntPlatformType,
        DatabaseConfig? currentQQNTConfig,
        DatabaseConfig? currentPCQQConfig,
        DatabaseConfig? currentAndroidMobileQQConfig,
        IcalinguaDatabaseRuntimeGroup icalinguaDatabases,
        QQNtDatabaseRuntimeGroup qqNtDatabases,
        PCQQDatabaseRuntimeGroup pcqqDatabase,
        AndroidMobileQQDatabaseRuntimeGroup androidMobileQQDatabase)
    {
        return new LoadedDatabaseGroupBuildContext(
            ntPlatformType,
            currentQQNTConfig,
            currentPCQQConfig,
            currentAndroidMobileQQConfig,
            icalinguaDatabases.CurrentConfigs,
            qqNtDatabases.CreateConfig(ntPlatformType),
            pcqqDatabase.CreateConfig(),
            androidMobileQQDatabase.CreateConfig(),
            icalinguaDatabases.LoadedPrimaryConfig,
            qqNtDatabases,
            pcqqDatabase,
            androidMobileQQDatabase,
            icalinguaDatabases);
    }
}

internal static class LoadedDatabaseGroupBuilder
{
    public static IReadOnlyList<LoadedDatabaseGroup> Build(LoadedDatabaseGroupBuildContext context)
    {
        var groups = new List<LoadedDatabaseGroup>();

        var pcqqItems = context.CurrentPCQQConfig?.PCQQ is { } pcqqConfig
            ? CreatePCQQDisplayItems(pcqqConfig, context.PCQQDatabase.MessageDatabase)
            : CreateLoadedPCQQDisplayItems(context.PCQQDatabase);
        if (pcqqItems.Count > 0)
            groups.Add(new LoadedDatabaseGroup(DatabasePlatformType.PCQQ, pcqqItems, context.CurrentPCQQConfig ?? context.LoadedPCQQConfig));

        var androidMobileQQItems = context.CurrentAndroidMobileQQConfig?.AndroidMobileQQ is { } androidMobileQQConfig
            ? CreateAndroidMobileQQDisplayItems(androidMobileQQConfig, context.AndroidMobileQQDatabase.MessageDatabase)
            : CreateLoadedAndroidMobileQQDisplayItems(context.AndroidMobileQQDatabase);
        if (androidMobileQQItems.Count > 0)
            groups.Add(new LoadedDatabaseGroup(
                DatabasePlatformType.AndroidMobileQQ,
                androidMobileQQItems,
                context.CurrentAndroidMobileQQConfig ?? context.LoadedAndroidMobileQQConfig));

        var icalinguaItems = context.CurrentIcalinguaConfigs.Count > 0
            ? CreateIcalinguaDisplayItems(context.CurrentIcalinguaConfigs, context.IcalinguaDatabases.Databases)
            : CreateLoadedIcalinguaDisplayItems(context.IcalinguaDatabases.Databases);
        if (icalinguaItems.Count > 0)
            groups.Add(new LoadedDatabaseGroup(DatabasePlatformType.Icalingua, icalinguaItems, context.LoadedIcalinguaConfig));

        var qqntItems = context.CurrentQQNTConfig is { } qqntConfig
            ? CreateQQNTDisplayItems(qqntConfig, context)
            : CreateLoadedQQNTDisplayItems(context);
        if (qqntItems.Count > 0)
            groups.Add(new LoadedDatabaseGroup(context.NtPlatformType, qqntItems, context.CurrentQQNTConfig ?? context.LoadedQQNTConfig));

        return groups;
    }

    private static List<LoadedDatabaseItem> CreatePCQQDisplayItems(
        PCQQDatabaseConfig config,
        PCQQMessageReader? messageDatabase)
    {
        var items = new List<LoadedDatabaseItem>();
        AddDisplayItem(items, "Msg3.0.db", config.MessageDbPath, messageDatabase, LoadedDatabaseItemKind.PCQQMessageDb);
        AddDisplayItem(items, "Info.db", config.InfoDbPath, null, LoadedDatabaseItemKind.PCQQInfoDb);
        AddDisplayItem(items, "数据目录", config.DataPath, null, LoadedDatabaseItemKind.PCQQDataPath);
        return items;
    }

    private static List<LoadedDatabaseItem> CreateIcalinguaDisplayItems(
        IEnumerable<DatabaseConfig> configs,
        IcalinguaMessageDatabaseSet? databases)
    {
        var items = new List<LoadedDatabaseItem>();
        foreach (var config in configs)
        {
            if (config.Icalingua is not { } icalingua)
                continue;

            var reader = FindIcalinguaReader(databases, icalingua.DatabasePath);
            AddDisplayItem(items, LoadedDatabaseItem.GetIcalinguaDatabaseName(icalingua.DatabasePath), icalingua.DatabasePath, reader, LoadedDatabaseItemKind.IcalinguaMessageDb);
            AddDisplayItem(items, "数据目录", icalingua.DataPath, null, LoadedDatabaseItemKind.IcalinguaDataPath);
        }

        return items;
    }

    private static List<LoadedDatabaseItem> CreateAndroidMobileQQDisplayItems(
        AndroidMobileQQDatabaseConfig config,
        AndroidMobileQQMessageReader? messageDatabase)
    {
        var items = new List<LoadedDatabaseItem>();
        AddDisplayItem(items, "数据目录", config.RootPath, null, LoadedDatabaseItemKind.AndroidMobileQQRootPath);
        if (!string.IsNullOrWhiteSpace(config.RootPath) &&
            !string.IsNullOrWhiteSpace(config.SelfUin))
        {
            var messageDbPath = messageDatabase?.DatabaseFilePath ??
                Path.Combine(ResolveAndroidMobileQQChildDirectory(config.RootPath, "databases", "db"), config.SelfUin + ".db");
            AddDisplayItem(items, config.SelfUin + ".db", messageDbPath, messageDatabase, LoadedDatabaseItemKind.AndroidMobileQQMessageDb);
        }

        AddDisplayItem(items, "MobileQQ", config.MobileQQPath, null, LoadedDatabaseItemKind.AndroidMobileQQMobileQQPath);
        AddDisplayItem(items, "chatpic", config.ChatPicPath, null, LoadedDatabaseItemKind.AndroidMobileQQChatPicPath);
        return items;
    }

    private static List<LoadedDatabaseItem> CreateLoadedIcalinguaDisplayItems(IcalinguaMessageDatabaseSet? databases)
    {
        var items = new List<LoadedDatabaseItem>();
        if (databases is null)
            return items;

        foreach (var entry in databases.Databases)
        {
            items.Add(new LoadedDatabaseItem(entry.Reader));
            if (!string.IsNullOrWhiteSpace(entry.DataPath))
                items.Add(new LoadedDatabaseItem("数据目录", entry.DataPath, null, LoadedDatabaseItemKind.IcalinguaDataPath));
        }

        return items;
    }

    private static List<LoadedDatabaseItem> CreateLoadedPCQQDisplayItems(PCQQDatabaseRuntimeGroup database)
    {
        var items = new List<LoadedDatabaseItem>();
        var messageDatabase = database.MessageDatabase;
        if (messageDatabase is not null)
            items.Add(new LoadedDatabaseItem(messageDatabase));
        if (messageDatabase?.InfoDatabase is { } infoDb)
            items.Add(new LoadedDatabaseItem("Info.db", infoDb.InfoDbPath, null, LoadedDatabaseItemKind.PCQQInfoDb));
        if (!string.IsNullOrWhiteSpace(database.DataPath))
            items.Add(new LoadedDatabaseItem("数据目录", database.DataPath, null, LoadedDatabaseItemKind.PCQQDataPath));
        return items;
    }

    private static List<LoadedDatabaseItem> CreateLoadedAndroidMobileQQDisplayItems(AndroidMobileQQDatabaseRuntimeGroup database)
    {
        var items = new List<LoadedDatabaseItem>();
        var messageDatabase = database.MessageDatabase;
        if (messageDatabase is not null)
        {
            items.Add(new LoadedDatabaseItem(messageDatabase));
            items.Add(new LoadedDatabaseItem("数据目录", messageDatabase.RootPath, null, LoadedDatabaseItemKind.AndroidMobileQQRootPath));
        }

        if (!string.IsNullOrWhiteSpace(database.MobileQQPath))
            items.Add(new LoadedDatabaseItem("MobileQQ", database.MobileQQPath, null, LoadedDatabaseItemKind.AndroidMobileQQMobileQQPath));
        if (!string.IsNullOrWhiteSpace(database.ChatPicPath))
            items.Add(new LoadedDatabaseItem("chatpic", database.ChatPicPath, null, LoadedDatabaseItemKind.AndroidMobileQQChatPicPath));

        return items;
    }

    private static List<LoadedDatabaseItem> CreateQQNTDisplayItems(
        DatabaseConfig config,
        LoadedDatabaseGroupBuildContext context)
    {
        var items = new List<LoadedDatabaseItem>();
        var qqnt = config.Type is DatabasePlatformType.AndroidQQNT
            ? config.AndroidQQNT
            : config.QQNT;
        if (qqnt is null)
            return items;

        var qqNtDatabases = context.QQNtDatabases;
        AddDisplayItem(items, "nt_msg.db", qqnt.MessageDbPath, config.Type is DatabasePlatformType.AndroidQQNT ? qqNtDatabases.AndroidMessageDatabase : qqNtDatabases.MessageDatabase, LoadedDatabaseItemKind.NtMessageDb);
        AddDisplayItem(items, "group_info.db", qqnt.GroupInfoDbPath, qqNtDatabases.GroupInfoDatabase, LoadedDatabaseItemKind.GroupInfoDb);
        AddDisplayItem(items, "profile_info.db", qqnt.ProfileInfoDbPath, qqNtDatabases.ProfileInfoDatabase, LoadedDatabaseItemKind.ProfileInfoDb);
        AddDisplayItem(items, "group_msg_fts.db", qqnt.GroupMessageFtsDbPath, qqNtDatabases.GroupMessageFtsDatabase, LoadedDatabaseItemKind.GroupMessageFtsDb);

        if (config.Type is DatabasePlatformType.AndroidQQNT && config.AndroidQQNT is { } android)
        {
            AddDisplayItem(items, "MobileQQ", android.MobileQQPath, null, LoadedDatabaseItemKind.MobileQQPath);
            AddDisplayItem(items, "chatpic", android.ChatPicPath, null, LoadedDatabaseItemKind.AndroidQQNtChatPicPath);
        }
        else
        {
            AddDisplayItem(items, "nt_data", qqnt.NtDataPath, null, LoadedDatabaseItemKind.NtDataPath);
        }

        return items;
    }

    private static List<LoadedDatabaseItem> CreateLoadedQQNTDisplayItems(LoadedDatabaseGroupBuildContext context)
    {
        var items = new List<LoadedDatabaseItem>();
        var database = context.QQNtDatabases;
        if (database.MessageDatabase is not null)
            items.Add(new LoadedDatabaseItem(database.MessageDatabase));
        if (database.AndroidMessageDatabase is not null)
            items.Add(new LoadedDatabaseItem(database.AndroidMessageDatabase));
        if (database.GroupInfoDatabase is not null)
            items.Add(new LoadedDatabaseItem(database.GroupInfoDatabase));
        if (database.ProfileInfoDatabase is not null)
            items.Add(new LoadedDatabaseItem(database.ProfileInfoDatabase));
        if (database.GroupMessageFtsDatabase is not null)
            items.Add(new LoadedDatabaseItem(database.GroupMessageFtsDatabase));
        if (!string.IsNullOrWhiteSpace(database.AndroidMobileQQPath))
            items.Add(new LoadedDatabaseItem("MobileQQ", database.AndroidMobileQQPath, null, LoadedDatabaseItemKind.MobileQQPath));
        if (!string.IsNullOrWhiteSpace(database.AndroidChatPicPath))
            items.Add(new LoadedDatabaseItem("chatpic", database.AndroidChatPicPath, null, LoadedDatabaseItemKind.AndroidQQNtChatPicPath));
        else if (!string.IsNullOrWhiteSpace(database.NtDataPath))
            items.Add(new LoadedDatabaseItem("nt_data", database.NtDataPath, null, LoadedDatabaseItemKind.NtDataPath));
        return items;
    }

    private static void AddDisplayItem(
        List<LoadedDatabaseItem> items,
        string name,
        string? path,
        IQQDatabase? database,
        LoadedDatabaseItemKind kind)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        items.Add(new LoadedDatabaseItem(name, path, database, kind));
    }

    private static string ResolveAndroidMobileQQChildDirectory(string rootPath, string primaryName, string fallbackName)
    {
        var primaryPath = Path.Combine(rootPath, primaryName);
        if (Directory.Exists(primaryPath))
            return primaryPath;

        var fallbackPath = Path.Combine(rootPath, fallbackName);
        return Directory.Exists(fallbackPath) ? fallbackPath : primaryPath;
    }

    private static IcalinguaMessageReader? FindIcalinguaReader(
        IcalinguaMessageDatabaseSet? databases,
        string? databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || databases is null)
            return null;

        var fullPath = Path.GetFullPath(databasePath);
        return databases.Databases
            .FirstOrDefault(entry => string.Equals(
                Path.GetFullPath(entry.Reader.DatabaseFilePath),
                fullPath,
                StringComparison.OrdinalIgnoreCase))
            ?.Reader;
    }
}
