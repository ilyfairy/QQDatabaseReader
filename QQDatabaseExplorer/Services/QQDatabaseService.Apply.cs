using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;

namespace QQDatabaseExplorer.Services;

public partial class QQDatabaseService
{
    internal async Task ApplyPreparedDatabaseConfigsAsync(IReadOnlyList<PreparedDatabaseConfig> preparedDatabases)
    {
        var icalinguaDatabases = preparedDatabases
            .Where(static prepared => prepared.PlatformType == DatabasePlatformType.Icalingua)
            .ToArray();

        foreach (var prepared in preparedDatabases.Where(static prepared => prepared.PlatformType != DatabasePlatformType.Icalingua))
        {
            await ApplyPreparedDatabaseConfigAsync(prepared);
        }

        if (icalinguaDatabases.Length > 0)
            await ApplyPreparedIcalinguaDatabasesAsync(icalinguaDatabases);
    }

    internal async Task ApplyPreparedDatabaseConfigAsync(PreparedDatabaseConfig prepared)
    {
        switch (prepared.PlatformType)
        {
            case DatabasePlatformType.PCQQ:
                await ApplyPreparedPCQQDatabaseAsync(prepared);
                break;
            case DatabasePlatformType.AndroidMobileQQ:
                await ApplyPreparedAndroidMobileQQDatabaseAsync(prepared);
                break;
            case DatabasePlatformType.Icalingua:
                await ApplyPreparedIcalinguaDatabaseAsync(prepared);
                break;
            case DatabasePlatformType.QQNT:
            case DatabasePlatformType.AndroidQQNT:
                await ApplyPreparedQQNTDatabasesAsync(prepared);
                break;
        }

        prepared.Detach();
    }

    private async Task ApplyPreparedQQNTDatabasesAsync(PreparedDatabaseConfig prepared)
    {
        var ntDatabases = prepared.NtDatabaseGroup;
        _currentQQNTConfig = prepared.Config;
        _ntPlatformType = prepared.PlatformType;
        RemoveQQNTDatabases(clearConfig: false);

        _qqNtDatabases = QQNtDatabaseRuntimeGroup.FromPrepared(ntDatabases);

        if (ntDatabases is not null)
        {
            foreach (var database in ntDatabases.Databases)
            {
                await NotifyDatabaseAddedAsync(database);
            }
        }

        RebuildDatabaseGroups();
    }

    private async Task ApplyPreparedPCQQDatabaseAsync(PreparedDatabaseConfig prepared)
    {
        _currentPCQQConfig = prepared.Config;
        RemovePCQQDatabase(clearConfig: false);

        _pcqqDatabase = prepared.PCQQMessageDatabase is null
            ? PCQQDatabaseRuntimeGroup.Empty
            : PCQQDatabaseRuntimeGroup.Create(prepared.PCQQMessageDatabase, prepared.PCQQDataPath);
        if (PCQQMessageDatabase is not null)
            await NotifyDatabaseAddedAsync(PCQQMessageDatabase);

        RebuildDatabaseGroups();
    }

    private async Task ApplyPreparedAndroidMobileQQDatabaseAsync(PreparedDatabaseConfig prepared)
    {
        _currentAndroidMobileQQConfig = prepared.Config;
        RemoveAndroidMobileQQDatabase(clearConfig: false);

        _androidMobileQQDatabase = prepared.AndroidMobileQQMessageDatabase is null
            ? AndroidMobileQQDatabaseRuntimeGroup.Empty
            : AndroidMobileQQDatabaseRuntimeGroup.Create(
                prepared.AndroidMobileQQMessageDatabase,
                prepared.AndroidMobileQQMediaPath,
                prepared.AndroidMobileQQChatPicPath);
        if (AndroidMobileQQMessageDatabase is not null)
            await NotifyDatabaseAddedAsync(AndroidMobileQQMessageDatabase);

        RebuildDatabaseGroups();
    }

    private async Task ApplyPreparedIcalinguaDatabaseAsync(PreparedDatabaseConfig prepared)
    {
        if (prepared.IcalinguaMessageDatabase is not null)
        {
            AddIcalinguaDatabase(
                prepared.IcalinguaMessageDatabase,
                prepared.Config,
                prepared.IcalinguaDataPath);
            await NotifyDatabaseAddedAsync(prepared.IcalinguaMessageDatabase);
        }

        RebuildDatabaseGroups();
    }

    private async Task ApplyPreparedIcalinguaDatabasesAsync(IEnumerable<PreparedDatabaseConfig> preparedDatabases)
    {
        IcalinguaMessageReader? notificationDatabase = null;

        foreach (var prepared in preparedDatabases)
        {
            if (prepared.IcalinguaMessageDatabase is null)
            {
                prepared.Detach();
                continue;
            }

            AddIcalinguaDatabase(
                prepared.IcalinguaMessageDatabase,
                prepared.Config,
                prepared.IcalinguaDataPath);
            notificationDatabase ??= prepared.IcalinguaMessageDatabase;
            prepared.Detach();
        }

        if (notificationDatabase is not null)
            await NotifyDatabaseAddedAsync(notificationDatabase);

        RebuildDatabaseGroups();
    }

    private void AddIcalinguaDatabase(
        QQDatabaseReader.IcalinguaMessageReader messageDatabase,
        DatabaseConfig? config,
        string? dataPath)
    {
        _icalinguaDatabases = _icalinguaDatabases.Add(messageDatabase, config, dataPath);
    }
}
