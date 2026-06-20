using System;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;

namespace QQDatabaseExplorer.Services;

internal sealed record PCQQDatabaseRuntimeGroup(
    PCQQMessageReader? MessageDatabase,
    string? DataPath)
{
    public static PCQQDatabaseRuntimeGroup Empty { get; } = new(null, null);

    public static PCQQDatabaseRuntimeGroup Create(PCQQMessageReader messageDatabase, string? dataPath)
    {
        return new PCQQDatabaseRuntimeGroup(
            messageDatabase,
            NormalizeOptionalPath(dataPath));
    }

    public DatabaseConfig? CreateConfig()
    {
        if (MessageDatabase is not { } pcqqMessageDb)
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
                DataPath = DataPath,
            },
        };
    }

    private static string? NormalizeOptionalPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : path;
    }
}
