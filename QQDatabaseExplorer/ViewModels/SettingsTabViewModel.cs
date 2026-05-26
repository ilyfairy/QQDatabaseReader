using CommunityToolkit.Mvvm.ComponentModel;
using QQDatabaseExplorer.Services;

namespace QQDatabaseExplorer.ViewModels;

public partial class SettingsTabViewModel : ViewModelBase
{
    private readonly AppSettingsService _appSettingsService;

    [ObservableProperty]
    public partial bool AlwaysShowMessageTime { get; set; }

    [ObservableProperty]
    public partial bool HighlightMentions { get; set; }

    public SettingsTabViewModel(AppSettingsService appSettingsService)
    {
        _appSettingsService = appSettingsService;
        AlwaysShowMessageTime = _appSettingsService.AlwaysShowMessageTime;
        HighlightMentions = _appSettingsService.HighlightMentions;
        _appSettingsService.SettingsChanged += OnSettingsChanged;
    }

    partial void OnAlwaysShowMessageTimeChanged(bool value)
    {
        _appSettingsService.AlwaysShowMessageTime = value;
    }

    partial void OnHighlightMentionsChanged(bool value)
    {
        _appSettingsService.HighlightMentions = value;
    }

    private void OnSettingsChanged(object? sender, System.EventArgs e)
    {
        if (AlwaysShowMessageTime != _appSettingsService.AlwaysShowMessageTime)
        {
            AlwaysShowMessageTime = _appSettingsService.AlwaysShowMessageTime;
        }

        if (HighlightMentions != _appSettingsService.HighlightMentions)
        {
            HighlightMentions = _appSettingsService.HighlightMentions;
        }
    }
}
