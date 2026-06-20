using System;
using System.Collections.Generic;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.Services;

internal sealed class PreparedNtDatabaseGroup : IDisposable
{
    public PreparedNtDatabaseGroup(
        QQGroupInfoReader? groupInfoDatabase,
        QQProfileInfoReader? profileInfoDatabase,
        QQMessageReader? messageDatabase,
        QQAndroidMessageReader? androidMessageDatabase,
        QQGroupMessageFtsReader? groupMessageFtsDatabase,
        string? ntDataPath,
        string? androidMobileQQPath)
    {
        GroupInfoDatabase = groupInfoDatabase;
        ProfileInfoDatabase = profileInfoDatabase;
        MessageDatabase = messageDatabase;
        AndroidMessageDatabase = androidMessageDatabase;
        GroupMessageFtsDatabase = groupMessageFtsDatabase;
        NtDataPath = ntDataPath;
        AndroidMobileQQPath = androidMobileQQPath;
    }

    public QQGroupInfoReader? GroupInfoDatabase { get; }

    public QQProfileInfoReader? ProfileInfoDatabase { get; }

    public QQMessageReader? MessageDatabase { get; }

    public QQAndroidMessageReader? AndroidMessageDatabase { get; }

    public QQGroupMessageFtsReader? GroupMessageFtsDatabase { get; }

    public string? NtDataPath { get; }

    public string? AndroidMobileQQPath { get; }

    public IEnumerable<IQQDatabase> Databases
    {
        get
        {
            if (GroupInfoDatabase is not null)
                yield return GroupInfoDatabase;
            if (ProfileInfoDatabase is not null)
                yield return ProfileInfoDatabase;
            if (MessageDatabase is not null)
                yield return MessageDatabase;
            if (AndroidMessageDatabase is not null)
                yield return AndroidMessageDatabase;
            if (GroupMessageFtsDatabase is not null)
                yield return GroupMessageFtsDatabase;
        }
    }

    public void Dispose()
    {
        GroupMessageFtsDatabase?.Dispose();
        ProfileInfoDatabase?.Dispose();
        MessageDatabase?.Dispose();
        AndroidMessageDatabase?.Dispose();
        GroupInfoDatabase?.Dispose();
    }
}

internal sealed class PreparedDatabaseConfig : IDisposable
{
    private bool _isDetached;

    public PreparedDatabaseConfig(
        DatabasePlatformType platformType,
        DatabaseConfig? config,
        PreparedNtDatabaseGroup? ntDatabaseGroup,
        PCQQMessageReader? pcqqMessageDatabase,
        IcalinguaMessageReader? icalinguaMessageDatabase,
        string? pcqqDataPath,
        string? icalinguaDataPath)
    {
        PlatformType = platformType;
        Config = config;
        NtDatabaseGroup = ntDatabaseGroup;
        PCQQMessageDatabase = pcqqMessageDatabase;
        IcalinguaMessageDatabase = icalinguaMessageDatabase;
        PCQQDataPath = pcqqDataPath;
        IcalinguaDataPath = icalinguaDataPath;
    }

    public DatabasePlatformType PlatformType { get; }

    public DatabaseConfig? Config { get; }

    public PreparedNtDatabaseGroup? NtDatabaseGroup { get; }

    public QQGroupInfoReader? GroupInfoDatabase => NtDatabaseGroup?.GroupInfoDatabase;

    public QQProfileInfoReader? ProfileInfoDatabase => NtDatabaseGroup?.ProfileInfoDatabase;

    public QQMessageReader? MessageDatabase => NtDatabaseGroup?.MessageDatabase;

    public QQAndroidMessageReader? AndroidMessageDatabase => NtDatabaseGroup?.AndroidMessageDatabase;

    public QQGroupMessageFtsReader? GroupMessageFtsDatabase => NtDatabaseGroup?.GroupMessageFtsDatabase;

    public PCQQMessageReader? PCQQMessageDatabase { get; }

    public IcalinguaMessageReader? IcalinguaMessageDatabase { get; }

    public string? NtDataPath => NtDatabaseGroup?.NtDataPath;

    public string? AndroidMobileQQPath => NtDatabaseGroup?.AndroidMobileQQPath;

    public string? PCQQDataPath { get; }

    public string? IcalinguaDataPath { get; }

    public static PreparedDatabaseConfig Empty(DatabasePlatformType platformType) =>
        new(platformType, null, null, null, null, null, null);

    public void Detach()
    {
        _isDetached = true;
    }

    public void Dispose()
    {
        if (_isDetached)
            return;

        NtDatabaseGroup?.Dispose();
        PCQQMessageDatabase?.Dispose();
        IcalinguaMessageDatabase?.Dispose();
    }
}
