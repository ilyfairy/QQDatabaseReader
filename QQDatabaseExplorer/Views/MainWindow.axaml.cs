using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Platform.Storage;
using Avalonia.Controls;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.ViewModels;
using Ursa.Controls;

namespace QQDatabaseExplorer.Views;

public partial class MainWindow : UrsaWindow
{
    private readonly IDialogService _dialogService;
    private readonly ConfigService _configService;
    private readonly DatabaseConfigApplicationService _databaseConfigApplicationService;
    private readonly QQDatabaseService _qqDatabaseService;
    private readonly MainViewModel _mainViewModel;

    public MainWindow(
        MainViewModel mainViewModel,
        MainView mainView,
        IDialogService dialogService,
        ConfigService configService,
        DatabaseConfigApplicationService databaseConfigApplicationService,
        QQDatabaseService qqDatabaseService)
    {
        DataContext = mainViewModel;
        Content = mainView;
        _dialogService = dialogService;
        _configService = configService;
        _databaseConfigApplicationService = databaseConfigApplicationService;
        _qqDatabaseService = qqDatabaseService;
        _mainViewModel = mainViewModel;

        InitializeComponent();
    }

    private async void OpenMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await _dialogService.ShowOpenDatabaseDialog(platformType: DatabasePlatformType.QQNT);
    }

    private async void OpenPCQQMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await _dialogService.ShowOpenDatabaseDialog(platformType: DatabasePlatformType.PCQQ);
    }

    private async void OpenAndroidQQNTMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await _dialogService.ShowOpenDatabaseDialog(platformType: DatabasePlatformType.AndroidQQNT);
    }

    private async void OpenAndroidMobileQQMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await _dialogService.ShowOpenDatabaseDialog(platformType: DatabasePlatformType.AndroidMobileQQ);
    }

    private async void OpenIcalinguaMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await _dialogService.ShowOpenDatabaseDialog(platformType: DatabasePlatformType.Icalingua);
    }

    private async void SaveConfigMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!StorageProvider.CanSave)
            return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存数据库配置",
            DefaultExtension = "json",
            FileTypeChoices =
            [
                new FilePickerFileType("JSON 配置文件")
                {
                    Patterns = ["*.json"],
                },
            ],
        });

        var filePath = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        try
        {
            await _configService.SaveToFileAsync(filePath);
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageBox($"保存数据库配置失败:\n{ex.Message}", "错误");
        }
    }

    private async void LoadConfigMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_mainViewModel.IsLoadingConfig)
            return;

        if (!StorageProvider.CanOpen)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开数据库配置",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON 配置文件")
                {
                    Patterns = ["*.json"],
                },
            ],
        });

        var filePath = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        try
        {
            var config = await _configService.LoadFromFileAsync(filePath);
            if (config is null)
                return;

            var errors = await _databaseConfigApplicationService.ApplyAsync(config);
            if (errors.Count > 0)
                await _dialogService.ShowMessageBox(CreateConfigApplyErrorMessage(errors), "错误");
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageBox($"打开数据库配置失败:\n{ex.Message}", "错误");
        }
    }

    private static string CreateConfigApplyErrorMessage(IReadOnlyList<DatabaseConfigApplyError> errors)
    {
        return "部分数据库配置打开失败:\n" +
               string.Join("\n", errors.Select(static error => $"{error.Type}: {error.Message}"));
    }

    private void CloseConfigMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _qqDatabaseService.ClearDatabases();
    }

    private void ExitMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
}
