using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.Services;

internal sealed class IcalinguaDatabaseRuntimeGroup
{
    private readonly IReadOnlyList<DatabaseConfig> _currentConfigs;

    private IcalinguaDatabaseRuntimeGroup(
        IcalinguaMessageDatabaseSet? databases,
        IReadOnlyList<DatabaseConfig> currentConfigs)
    {
        Databases = databases;
        _currentConfigs = currentConfigs;
    }

    public static IcalinguaDatabaseRuntimeGroup Empty { get; } = new(null, Array.Empty<DatabaseConfig>());

    public IcalinguaMessageDatabaseSet? Databases { get; }

    public IReadOnlyList<DatabaseConfig> CurrentConfigs => _currentConfigs;

    public IcalinguaMessageReader? PrimaryReader => Databases?.PrimaryReader;

    public string? PrimaryDataPath => Databases?.PrimaryDataPath;

    public IReadOnlyList<IQQDatabase> DatabasesForRemoval =>
        Databases?.Databases.Select(static entry => (IQQDatabase)entry.Reader).ToArray() ??
        Array.Empty<IQQDatabase>();

    public IReadOnlyList<DatabaseConfig> CurrentOrLoadedConfigs =>
        _currentConfigs.Count > 0 ? _currentConfigs : CreateLoadedConfigs();

    public DatabaseConfig? LoadedPrimaryConfig => CreateLoadedConfigs().FirstOrDefault();

    public IcalinguaMessageDatabaseEntry? FindEntry(IQQDatabase database)
    {
        return Databases?.Databases.FirstOrDefault(entry => entry.Reader.Equals(database));
    }

    public IcalinguaDatabaseRuntimeGroup Add(
        IcalinguaMessageReader messageDatabase,
        DatabaseConfig? config,
        string? dataPath)
    {
        var entries = Databases?.Databases
            .Select((entry, index) => entry with { Index = index })
            .ToList() ?? [];
        var entryConfig = config ?? PreparedDatabaseConfigLoader.CreateIcalinguaDatabaseConfig(new IcalinguaDatabaseConfig
        {
            DatabasePath = messageDatabase.RawDatabase.DatabaseFilePath,
            DataPath = dataPath,
        });

        entries.Add(new IcalinguaMessageDatabaseEntry(
            entries.Count,
            messageDatabase,
            entryConfig,
            NormalizeOptionalPath(dataPath)));

        var configs = _currentConfigs.ToList();
        if (config is not null)
            configs.Add(config);

        return new IcalinguaDatabaseRuntimeGroup(new IcalinguaMessageDatabaseSet(entries), configs);
    }

    public IcalinguaDatabaseRuntimeGroup ClearCurrentConfigs()
    {
        return new IcalinguaDatabaseRuntimeGroup(Databases, Array.Empty<DatabaseConfig>());
    }

    public IcalinguaDatabaseRuntimeGroup RemoveAll(
        bool clearConfig,
        out IReadOnlyList<IQQDatabase> removedDatabases)
    {
        removedDatabases = DatabasesForRemoval;
        return new IcalinguaDatabaseRuntimeGroup(
            null,
            clearConfig ? Array.Empty<DatabaseConfig>() : _currentConfigs);
    }

    public IcalinguaDatabaseRuntimeGroup RemoveEntry(
        IcalinguaMessageReader reader,
        bool clearConfig)
    {
        if (Databases is null)
            return this;

        var remainingEntries = Databases.Databases
            .Where(entry => !entry.Reader.Equals(reader))
            .Select((entry, index) => entry with { Index = index })
            .ToArray();
        var remainingDatabases = remainingEntries.Length == 0
            ? null
            : new IcalinguaMessageDatabaseSet(remainingEntries);
        var configs = clearConfig
            ? remainingEntries.Select(static entry => entry.Config).ToArray()
            : _currentConfigs;

        return new IcalinguaDatabaseRuntimeGroup(remainingDatabases, configs);
    }

    public void ClearConfigItem(LoadedDatabaseItemKind kind, string? filePath = null)
    {
        foreach (var config in _currentConfigs)
        {
            var icalingua = config.Icalingua;
            if (icalingua is null)
                continue;
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var currentPath = kind switch
                {
                    LoadedDatabaseItemKind.IcalinguaMessageDb => icalingua.DatabasePath,
                    LoadedDatabaseItemKind.IcalinguaDataPath => icalingua.DataPath,
                    _ => null,
                };
                if (!PathsEqual(currentPath, filePath))
                    continue;
            }

            switch (kind)
            {
                case LoadedDatabaseItemKind.IcalinguaMessageDb:
                    icalingua.DatabasePath = null;
                    break;
                case LoadedDatabaseItemKind.IcalinguaDataPath:
                    icalingua.DataPath = null;
                    break;
            }
        }
    }

    public List<DatabaseConfig> CreateLoadedConfigs()
    {
        if (Databases is null)
            return [];

        return Databases.Databases
            .Select(static entry => PreparedDatabaseConfigLoader.CreateIcalinguaDatabaseConfig(new IcalinguaDatabaseConfig
            {
                DatabasePath = entry.Reader.RawDatabase.DatabaseFilePath,
                DataPath = entry.DataPath,
            }))
            .ToList();
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeOptionalPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : path;
    }
}
