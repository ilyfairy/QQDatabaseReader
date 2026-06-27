using System;
using System.Collections.Generic;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.Services;

internal sealed record QQNtDatabaseRuntimeGroup(
    QQGroupInfoReader? GroupInfoDatabase,
    QQProfileInfoReader? ProfileInfoDatabase,
    QQMessageReader? MessageDatabase,
    QQAndroidMessageReader? AndroidMessageDatabase,
    QQGroupMessageFtsReader? GroupMessageFtsDatabase,
    QQGroupMessageFtsReader? BuddyMessageFtsDatabase,
    string? NtDataPath,
    string? AndroidMobileQQPath,
    string? AndroidChatPicPath,
    string? AndroidNtUid,
    string? AndroidRand)
{
    public static QQNtDatabaseRuntimeGroup Empty { get; } = new(null, null, null, null, null, null, null, null, null, null, null);

    public bool HasDatabases =>
        MessageDatabase is not null ||
        AndroidMessageDatabase is not null ||
        GroupInfoDatabase is not null ||
        GroupMessageFtsDatabase is not null ||
        BuddyMessageFtsDatabase is not null ||
        ProfileInfoDatabase is not null;

    public IEnumerable<IQQDatabase> DatabasesForRemoval
    {
        get
        {
            if (GroupMessageFtsDatabase is not null)
                yield return GroupMessageFtsDatabase;
            if (BuddyMessageFtsDatabase is not null)
                yield return BuddyMessageFtsDatabase;
            if (ProfileInfoDatabase is not null)
                yield return ProfileInfoDatabase;
            if (MessageDatabase is not null)
                yield return MessageDatabase;
            if (AndroidMessageDatabase is not null)
                yield return AndroidMessageDatabase;
            if (GroupInfoDatabase is not null)
                yield return GroupInfoDatabase;
        }
    }

    public static QQNtDatabaseRuntimeGroup FromPrepared(PreparedNtDatabaseGroup? prepared)
    {
        return prepared is null
            ? Empty
            : new QQNtDatabaseRuntimeGroup(
                prepared.GroupInfoDatabase,
                prepared.ProfileInfoDatabase,
                prepared.MessageDatabase,
                prepared.AndroidMessageDatabase,
                prepared.GroupMessageFtsDatabase,
                prepared.BuddyMessageFtsDatabase,
                prepared.NtDataPath,
                prepared.AndroidMobileQQPath,
                prepared.AndroidChatPicPath,
                prepared.AndroidNtUid,
                prepared.AndroidRand);
    }

    public QQNtDatabaseRuntimeGroup WithGroupInfoDatabase(QQGroupInfoReader database)
    {
        return this with { GroupInfoDatabase = database };
    }

    public QQNtDatabaseRuntimeGroup WithProfileInfoDatabase(QQProfileInfoReader database)
    {
        return this with { ProfileInfoDatabase = database };
    }

    public QQNtDatabaseRuntimeGroup WithGroupMessageFtsDatabase(QQGroupMessageFtsReader database)
    {
        return this with { GroupMessageFtsDatabase = database };
    }

    public QQNtDatabaseRuntimeGroup WithBuddyMessageFtsDatabase(QQGroupMessageFtsReader database)
    {
        return this with { BuddyMessageFtsDatabase = database };
    }

    public QQNtDatabaseRuntimeGroup WithMessageDatabase(QQMessageReader database, string? ntDataPath)
    {
        return this with
        {
            MessageDatabase = database,
            NtDataPath = NormalizeOptionalPath(ntDataPath),
        };
    }

    public QQNtDatabaseRuntimeGroup WithAndroidMessageDatabase(
        QQAndroidMessageReader database,
        string? mobileQQPath,
        string? chatPicPath)
    {
        return this with
        {
            AndroidMessageDatabase = database,
            AndroidMobileQQPath = NormalizeOptionalPath(mobileQQPath),
            AndroidChatPicPath = NormalizeOptionalPath(chatPicPath),
        };
    }

    public QQNtDatabaseRuntimeGroup WithoutGroupInfoDatabase()
    {
        return this with { GroupInfoDatabase = null };
    }

    public QQNtDatabaseRuntimeGroup WithoutProfileInfoDatabase()
    {
        return this with { ProfileInfoDatabase = null };
    }

    public QQNtDatabaseRuntimeGroup WithoutGroupMessageFtsDatabase()
    {
        return this with { GroupMessageFtsDatabase = null };
    }

    public QQNtDatabaseRuntimeGroup WithoutBuddyMessageFtsDatabase()
    {
        return this with { BuddyMessageFtsDatabase = null };
    }

    public QQNtDatabaseRuntimeGroup WithoutMessageDatabase()
    {
        return this with
        {
            MessageDatabase = null,
            NtDataPath = null,
        };
    }

    public QQNtDatabaseRuntimeGroup WithoutAndroidMessageDatabase()
    {
        return this with
        {
            AndroidMessageDatabase = null,
            AndroidMobileQQPath = null,
            AndroidChatPicPath = null,
            AndroidNtUid = null,
            AndroidRand = null,
        };
    }

    public DatabaseConfig? CreateConfig(DatabasePlatformType platformType)
    {
        if (!HasDatabases)
            return null;

        var qqnt = new QQNTDatabaseConfig
        {
            NtDataPath = NtDataPath,
            MessageDbPath = MessageDatabase?.RawDatabase.DatabaseFilePath ?? AndroidMessageDatabase?.RawDatabase.DatabaseFilePath,
            MessageDbPassword = MessageDatabase?.RawDatabase.CipherPassword ?? AndroidMessageDatabase?.RawDatabase.CipherPassword,
            GroupInfoDbPath = GroupInfoDatabase?.RawDatabase.DatabaseFilePath,
            GroupInfoDbPassword = GroupInfoDatabase?.RawDatabase.CipherPassword,
            GroupMessageFtsDbPath = GroupMessageFtsDatabase?.RawDatabase.DatabaseFilePath,
            GroupMessageFtsDbPassword = GroupMessageFtsDatabase?.RawDatabase.CipherPassword,
            BuddyMessageFtsDbPath = BuddyMessageFtsDatabase?.RawDatabase.DatabaseFilePath,
            BuddyMessageFtsDbPassword = BuddyMessageFtsDatabase?.RawDatabase.CipherPassword,
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
                    ChatPicPath = AndroidChatPicPath,
                    NtUid = AndroidNtUid,
                    Rand = AndroidRand,
                    MessageDbPath = qqnt.MessageDbPath,
                    MessageDbPassword = qqnt.MessageDbPassword,
                    GroupInfoDbPath = qqnt.GroupInfoDbPath,
                    GroupInfoDbPassword = qqnt.GroupInfoDbPassword,
                    GroupMessageFtsDbPath = qqnt.GroupMessageFtsDbPath,
                    GroupMessageFtsDbPassword = qqnt.GroupMessageFtsDbPassword,
                    BuddyMessageFtsDbPath = qqnt.BuddyMessageFtsDbPath,
                    BuddyMessageFtsDbPassword = qqnt.BuddyMessageFtsDbPassword,
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

    private static string? NormalizeOptionalPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : path;
    }
}
