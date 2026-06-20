using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.Services;

public partial class QQDatabaseService
{
    public void RemoveDatabaseGroup(LoadedDatabaseGroup group)
    {
        switch (group.PlatformType)
        {
            case DatabasePlatformType.PCQQ:
                RemovePCQQDatabase();
                break;
            case DatabasePlatformType.Icalingua:
                RemoveIcalinguaDatabase();
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
        databases.AddRange(_icalinguaDatabases.DatabasesForRemoval);
        databases.AddRange(_qqNtDatabases.DatabasesForRemoval);

        _qqNtDatabases = QQNtDatabaseRuntimeGroup.Empty;
        _pcqqDatabase = PCQQDatabaseRuntimeGroup.Empty;
        _icalinguaDatabases = IcalinguaDatabaseRuntimeGroup.Empty;
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
        if (!TryRemoveDatabase(qqDatabase, out var removedDatabase) ||
            removedDatabase is null)
        {
            return;
        }

        NotifyDatabaseRemoved(removedDatabase);
        RebuildDatabaseGroups();
    }

    private bool TryRemoveDatabase(IQQDatabase qqDatabase, out IQQDatabase? removedDatabase)
    {
        return TryRemoveQQNtDatabase(qqDatabase, out removedDatabase) ||
            TryRemovePCQQDatabase(qqDatabase, out removedDatabase) ||
            TryRemoveIcalinguaDatabase(qqDatabase, out removedDatabase);
    }

    private bool TryRemoveQQNtDatabase(IQQDatabase qqDatabase, out IQQDatabase? removedDatabase)
    {
        removedDatabase = qqDatabase;

        if (qqDatabase.Equals(GroupInfoDatabase))
        {
            _qqNtDatabases = _qqNtDatabases.WithoutGroupInfoDatabase();
            ClearQQNTConfigItem(LoadedDatabaseItemKind.GroupInfoDb);
            return true;
        }

        if (qqDatabase.Equals(MessageDatabase))
        {
            _qqNtDatabases = _qqNtDatabases.WithoutMessageDatabase();
            ClearQQNTConfigItem(LoadedDatabaseItemKind.NtMessageDb);
            return true;
        }

        if (qqDatabase.Equals(AndroidMessageDatabase))
        {
            _qqNtDatabases = _qqNtDatabases.WithoutAndroidMessageDatabase();
            ClearQQNTConfigItem(LoadedDatabaseItemKind.NtMessageDb);
            return true;
        }

        if (qqDatabase.Equals(GroupMessageFtsDatabase))
        {
            _qqNtDatabases = _qqNtDatabases.WithoutGroupMessageFtsDatabase();
            ClearQQNTConfigItem(LoadedDatabaseItemKind.GroupMessageFtsDb);
            return true;
        }

        if (qqDatabase.Equals(ProfileInfoDatabase))
        {
            _qqNtDatabases = _qqNtDatabases.WithoutProfileInfoDatabase();
            ClearQQNTConfigItem(LoadedDatabaseItemKind.ProfileInfoDb);
            return true;
        }

        removedDatabase = null;
        return false;
    }

    private bool TryRemovePCQQDatabase(IQQDatabase qqDatabase, out IQQDatabase? removedDatabase)
    {
        removedDatabase = qqDatabase;

        if (qqDatabase.Equals(PCQQMessageDatabase))
        {
            _pcqqDatabase = PCQQDatabaseRuntimeGroup.Empty;
            ClearPCQQConfigItem(LoadedDatabaseItemKind.PCQQMessageDb);
            return true;
        }

        removedDatabase = null;
        return false;
    }

    private bool TryRemoveIcalinguaDatabase(IQQDatabase qqDatabase, out IQQDatabase? removedDatabase)
    {
        removedDatabase = qqDatabase;

        if (_icalinguaDatabases.FindEntry(qqDatabase) is { } icalinguaEntry)
        {
            RemoveIcalinguaDatabaseEntry(icalinguaEntry.Reader, notify: false, clearConfig: true);
            return true;
        }

        removedDatabase = null;
        return false;
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
            case LoadedDatabaseItemKind.IcalinguaDataPath:
                ClearIcalinguaConfigItem(item.Kind, item.FilePath);
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

    private void RemoveQQNTDatabases(bool clearConfig = true)
    {
        var databases = _qqNtDatabases.DatabasesForRemoval.ToArray();

        _qqNtDatabases = QQNtDatabaseRuntimeGroup.Empty;
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
        _pcqqDatabase = PCQQDatabaseRuntimeGroup.Empty;
        NotifyDatabaseRemoved(database);
        RebuildDatabaseGroups();
    }

    private void RemoveIcalinguaDatabase(bool clearConfig = true)
    {
        _icalinguaDatabases = _icalinguaDatabases.RemoveAll(clearConfig, out var databases);
        foreach (var database in databases)
        {
            NotifyDatabaseRemoved(database);
        }

        RebuildDatabaseGroups();
    }

    private void RemoveIcalinguaDatabaseEntry(
        IcalinguaMessageReader reader,
        bool notify,
        bool clearConfig)
    {
        _icalinguaDatabases = _icalinguaDatabases.RemoveEntry(reader, clearConfig);

        if (notify)
            NotifyDatabaseRemoved(reader);
    }
}
