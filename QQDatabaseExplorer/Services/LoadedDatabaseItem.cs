using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using QQDatabaseReader;
using QQDatabaseReader.Database;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.Services;

public enum LoadedDatabaseItemKind
{
    Other,
    NtMessageDb,
    GroupInfoDb,
    ProfileInfoDb,
    GroupMessageFtsDb,
    BuddyMessageFtsDb,
    NtDataPath,
    MobileQQPath,
    AndroidQQNtChatPicPath,
    PCQQMessageDb,
    PCQQInfoDb,
    PCQQDataPath,
    AndroidMobileQQMessageDb,
    AndroidMobileQQSlowTableDb,
    AndroidMobileQQRootPath,
    AndroidMobileQQMobileQQPath,
    AndroidMobileQQChatPicPath,
    IcalinguaMessageDb,
    IcalinguaDataPath,
}

public sealed class LoadedDatabaseGroup
{
    public LoadedDatabaseGroup(DatabasePlatformType platformType, IEnumerable<LoadedDatabaseItem> items, DatabaseConfig? config = null)
    {
        PlatformType = platformType;
        Name = GetPlatformDisplayName(platformType);
        Items = new ObservableCollection<LoadedDatabaseItem>(items);
        Config = config;
    }

    public DatabasePlatformType PlatformType { get; }

    public string Name { get; }

    public ObservableCollection<LoadedDatabaseItem> Items { get; }

    public DatabaseConfig? Config { get; }

    private static string GetPlatformDisplayName(DatabasePlatformType platformType)
    {
        return platformType switch
        {
            DatabasePlatformType.AndroidMobileQQ => "AndroidQQ",
            _ => platformType.ToString(),
        };
    }
}

public sealed class LoadedDatabaseItem
{
    public LoadedDatabaseItem(IQQDatabase database)
    {
        Database = database;
        FilePath = database.DatabaseFilePath;
        Name = GetDatabaseDisplayName(database.DatabaseType, FilePath);
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

    public bool CanExport => Database is not null &&
                             Database.DatabaseType != QQDatabaseType.IcalinguaMessage &&
                             Kind != LoadedDatabaseItemKind.IcalinguaMessageDb;

    private static string GetDatabaseDisplayName(QQDatabaseType databaseType, string filePath)
    {
        return databaseType switch
        {
            QQDatabaseType.GroupInfo => "group_info.db",
            QQDatabaseType.GroupMessageFts => "group_msg_fts.db",
            QQDatabaseType.ProfileInfo => "profile_info.db",
            QQDatabaseType.PCQQMessage => "Msg3.0.db",
            QQDatabaseType.AndroidMobileQQMessage => Path.GetFileName(filePath),
            QQDatabaseType.IcalinguaMessage => GetIcalinguaDatabaseName(filePath),
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
            QQDatabaseType.AndroidMobileQQMessage => LoadedDatabaseItemKind.AndroidMobileQQMessageDb,
            QQDatabaseType.IcalinguaMessage => LoadedDatabaseItemKind.IcalinguaMessageDb,
            QQDatabaseType.AndroidMessage => LoadedDatabaseItemKind.NtMessageDb,
            QQDatabaseType.Message => LoadedDatabaseItemKind.NtMessageDb,
            _ => LoadedDatabaseItemKind.Other,
        };
    }

    public static string GetIcalinguaDatabaseName(string? filePath)
    {
        var fileName = string.IsNullOrWhiteSpace(filePath)
            ? null
            : Path.GetFileName(filePath);
        return string.IsNullOrWhiteSpace(fileName) ? "eqq*.db" : fileName;
    }
}
