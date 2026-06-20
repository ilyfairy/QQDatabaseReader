using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.ViewModels;
using QQDatabaseReader;

namespace QQDatabaseExplorer.Services;

public sealed record DatabaseConfigApplyError(DatabasePlatformType Type, string Message);

public sealed class DatabaseConfigApplicationService
{
    private readonly QQDatabaseService _databaseService;
    private readonly MainViewModel _mainViewModel;

    public DatabaseConfigApplicationService(
        QQDatabaseService databaseService,
        MainViewModel mainViewModel)
    {
        _databaseService = databaseService;
        _mainViewModel = mainViewModel;
    }

    public async Task ApplyAsync(
        DatabaseConfig config,
        string loadingText = "正在打开数据库...")
    {
        PreparedDatabaseConfig? prepared = null;
        try
        {
            _mainViewModel.LoadingText = loadingText;
            _mainViewModel.IsLoadingConfig = true;
            EnsureAndroidQQNTPassword(config);
            prepared = await PreparedDatabaseConfigLoader.PrepareAsync(config);
            await _databaseService.ApplyPreparedDatabaseConfigAsync(prepared);
            prepared = null;
        }
        finally
        {
            prepared?.Dispose();
            _mainViewModel.IsLoadingConfig = false;
            _mainViewModel.LoadingText = string.Empty;
        }
    }

    public async Task<IReadOnlyList<DatabaseConfigApplyError>> ApplyAsync(
        AppConfig config,
        string loadingText = "正在打开数据库配置...")
    {
        var preparedDatabases = new List<PreparedDatabaseConfig>();
        var errors = new List<DatabaseConfigApplyError>();
        try
        {
            _mainViewModel.LoadingText = loadingText;
            _mainViewModel.IsLoadingConfig = true;

            foreach (var databaseConfig in config.Databases)
            {
                await PrepareDatabaseConfigAsync(databaseConfig, preparedDatabases, errors);
            }

            _databaseService.ClearDatabases();

            await _databaseService.ApplyPreparedDatabaseConfigsAsync(preparedDatabases);

            return errors;
        }
        finally
        {
            foreach (var preparedDatabase in preparedDatabases)
            {
                preparedDatabase.Dispose();
            }

            _mainViewModel.IsLoadingConfig = false;
            _mainViewModel.LoadingText = string.Empty;
        }
    }

    private async Task PrepareDatabaseConfigAsync(
        DatabaseConfig config,
        ICollection<PreparedDatabaseConfig> preparedDatabases,
        ICollection<DatabaseConfigApplyError> errors)
    {
        try
        {
            EnsureAndroidQQNTPassword(config);
            preparedDatabases.Add(await PreparedDatabaseConfigLoader.PrepareAsync(config));
        }
        catch (Exception ex)
        {
            errors.Add(new DatabaseConfigApplyError(config.Type, ex.Message));
        }
    }

    private static void EnsureAndroidQQNTPassword(DatabaseConfig config)
    {
        if (config.Type is not DatabasePlatformType.AndroidQQNT ||
            config.AndroidQQNT is not { } android ||
            !string.IsNullOrWhiteSpace(android.MessageDbPassword) ||
            string.IsNullOrWhiteSpace(android.NtUid) ||
            string.IsNullOrWhiteSpace(android.Rand))
        {
            return;
        }

        android.MessageDbPassword = RawDatabase.GetQQKey(android.NtUid, android.Rand);
    }
}
