using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;

namespace QQDatabaseExplorer.Services;

public class ConfigService
{
    private readonly QQDatabaseService _qqDatabaseService;
    private readonly IDialogService _dialogService;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            new JsonStringEnumConverter(),
        },
    };

    /// <summary>
    /// 默认配置文件路径：%AppData%/QQDatabaseExplorer/config.json
    /// </summary>
    public static string DefaultConfigFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QQDatabaseExplorer",
            "config.json");

    public ConfigService(QQDatabaseService qqDatabaseService, IDialogService dialogService)
    {
        _qqDatabaseService = qqDatabaseService;
        _dialogService = dialogService;
    }

    /// <summary>
    /// 从当前已打开的数据库状态生成配置对象
    /// </summary>
    public AppConfig CreateConfigFromCurrentState()
    {
        return _qqDatabaseService.CreateCurrentConfig();
    }

    /// <summary>
    /// 保存当前配置到指定路径
    /// </summary>
    public async Task SaveToFileAsync(string filePath)
    {
        var config = CreateConfigFromCurrentState();

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(config, JsonOptions);
        await File.WriteAllTextAsync(filePath, json);
    }

    /// <summary>
    /// 保存当前配置到默认路径
    /// </summary>
    public async Task SaveToDefaultAsync()
    {
        await SaveToFileAsync(DefaultConfigFilePath);
    }

    /// <summary>
    /// 从指定路径加载配置并打开数据库
    /// </summary>
    public async Task LoadFromFileAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return;

        string json;
        try
        {
            json = await File.ReadAllTextAsync(filePath);
        }
        catch
        {
            return;
        }

        AppConfig config;
        try
        {
            config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
                ?? new AppConfig();
        }
        catch
        {
            return;
        }

        await ApplyConfigAsync(config);
    }

    /// <summary>
    /// 从默认路径加载配置并打开数据库
    /// </summary>
    public async Task<bool> LoadFromDefaultAsync()
    {
        if (!File.Exists(DefaultConfigFilePath))
            return false;

        await LoadFromFileAsync(DefaultConfigFilePath);
        return true;
    }

    /// <summary>
    /// 将配置应用到 QQDatabaseService，重新打开数据库
    /// </summary>
    private async Task ApplyConfigAsync(AppConfig config)
    {
        _qqDatabaseService.ClearDatabases();

        foreach (var databaseConfig in config.Databases)
        {
            EnsureAndroidQQNTPassword(databaseConfig);
            await ApplyDatabaseConfigAsync(databaseConfig);
        }
    }

    private async Task ApplyDatabaseConfigAsync(DatabaseConfig config)
    {
        try
        {
            _qqDatabaseService.LoadDatabaseConfig(config);
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageBox(
                $"打开 {config.Type} 数据库配置失败:\n{ex.Message}",
                "错误");
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
