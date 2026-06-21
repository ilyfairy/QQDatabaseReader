using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using QQDatabaseReader;
using QQDatabaseReader.Database;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.Services;

public partial class QQDatabaseService
{
    public event Action<IQQDatabase>? DatabaseAdded;
    public event Func<IQQDatabase, Task>? DatabaseAddedAsync;
    public event Action<IQQDatabase>? DatabaseRemoved;
    public event Action? DatabaseGroupsChanged;

    private QQNtDatabaseRuntimeGroup _qqNtDatabases = QQNtDatabaseRuntimeGroup.Empty;

    public QQGroupInfoReader? GroupInfoDatabase => _qqNtDatabases.GroupInfoDatabase;

    public QQMessageReader? MessageDatabase => _qqNtDatabases.MessageDatabase;

    public QQAndroidMessageReader? AndroidMessageDatabase => _qqNtDatabases.AndroidMessageDatabase;

    private PCQQDatabaseRuntimeGroup _pcqqDatabase = PCQQDatabaseRuntimeGroup.Empty;

    public PCQQMessageReader? PCQQMessageDatabase => _pcqqDatabase.MessageDatabase;

    private AndroidMobileQQDatabaseRuntimeGroup _androidMobileQQDatabase = AndroidMobileQQDatabaseRuntimeGroup.Empty;

    public AndroidMobileQQMessageReader? AndroidMobileQQMessageDatabase => _androidMobileQQDatabase.MessageDatabase;

    private IcalinguaDatabaseRuntimeGroup _icalinguaDatabases = IcalinguaDatabaseRuntimeGroup.Empty;

    public IcalinguaMessageReader? IcalinguaMessageDatabase => _icalinguaDatabases.PrimaryReader;

    public IcalinguaMessageDatabaseSet? IcalinguaMessageDatabases => _icalinguaDatabases.Databases;

    public QQGroupMessageFtsReader? GroupMessageFtsDatabase => _qqNtDatabases.GroupMessageFtsDatabase;

    public QQProfileInfoReader? ProfileInfoDatabase => _qqNtDatabases.ProfileInfoDatabase;

    public string? NtDataPath => _qqNtDatabases.NtDataPath;

    public string? AndroidMobileQQPath => _qqNtDatabases.AndroidMobileQQPath;

    public string? PCQQDataPath => _pcqqDatabase.DataPath;

    public string? AndroidMobileQQMediaPath => _androidMobileQQDatabase.MobileQQPath;

    public string? IcalinguaDataPath => _icalinguaDatabases.PrimaryDataPath;

    public ObservableCollection<LoadedDatabaseGroup> DatabaseGroups { get; } = new();

    private DatabasePlatformType _ntPlatformType = DatabasePlatformType.QQNT;
    private DatabaseConfig? _currentQQNTConfig;
    private DatabaseConfig? _currentPCQQConfig;
    private DatabaseConfig? _currentAndroidMobileQQConfig;

    private void RebuildDatabaseGroups()
    {
        DatabaseGroups.Clear();
        foreach (var group in LoadedDatabaseGroupBuilder.Build(CreateLoadedDatabaseGroupBuildContext()))
        {
            DatabaseGroups.Add(group);
        }

        DatabaseGroupsChanged?.Invoke();
    }

    private LoadedDatabaseGroupBuildContext CreateLoadedDatabaseGroupBuildContext()
    {
        return LoadedDatabaseGroupBuildContextFactory.Create(
            _ntPlatformType,
            _currentQQNTConfig,
            _currentPCQQConfig,
            _currentAndroidMobileQQConfig,
            _icalinguaDatabases,
            _qqNtDatabases,
            _pcqqDatabase,
            _androidMobileQQDatabase);
    }

    private void NotifyDatabaseRemoved(IQQDatabase database)
    {
        DatabaseRemoved?.Invoke(database);
        database.Dispose();
    }

    private void ReplaceRuntimeDatabase(IQQDatabase? previousDatabase, IQQDatabase newDatabase)
    {
        if (previousDatabase is not null)
            NotifyDatabaseRemoved(previousDatabase);

        NotifyDatabaseAdded(newDatabase, runAsyncHandlers: true);
        RebuildDatabaseGroups();
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
