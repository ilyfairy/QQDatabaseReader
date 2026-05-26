using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.Services;

public class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public AppSettings Settings { get; private set; } = new();

    public static string ConfigFilePath => Path.Combine(AppContext.BaseDirectory, "appconfig.json");

    public event EventHandler? SettingsChanged;

    public void Load()
    {
        if (!File.Exists(ConfigFilePath))
            return;

        try
        {
            var json = File.ReadAllText(ConfigFilePath);
            Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            Settings = new AppSettings();
        }
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(ConfigFilePath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(Settings, JsonOptions);
        File.WriteAllText(ConfigFilePath, json);
    }

    public bool AlwaysShowMessageTime
    {
        get => Settings.AlwaysShowMessageTime;
        set
        {
            if (Settings.AlwaysShowMessageTime == value)
                return;

            Settings.AlwaysShowMessageTime = value;
            Save();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool HighlightMentions
    {
        get => Settings.HighlightMentions;
        set
        {
            if (Settings.HighlightMentions == value)
                return;

            Settings.HighlightMentions = value;
            Save();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
