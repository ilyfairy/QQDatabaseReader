using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.ViewModels;

namespace QQDatabaseExplorer.Views;

public partial class MainView : UserControl
{
    private readonly IDialogService _dialogService;
    private readonly MainViewModel _viewModel;

    public MainView(
        MainViewModel viewModel,
        MessageTab messageTab,
        DatabaseTab databaseTab,
        GroupMessageSearchView groupMessageSearchView,
        SettingsTab settingsTab,
        ViewModelTokenService viewModelTokenService,
        IDialogService dialogService)
    {
        viewModelTokenService.AutoRegister(viewModel.ViewModelToken, this);
        DataContext = viewModel;
        _dialogService = dialogService;
        _viewModel = viewModel;
        InitializeComponent();

        this.messageTab.Content = messageTab;
        this.databaseTab.Content = databaseTab;
        this.groupMessageSearchTab.Content = groupMessageSearchView;
        this.settingsTab.Content = settingsTab;

        _viewModel.TabNavigationRequested += OnTabNavigationRequested;
    }

    private void OnTabNavigationRequested(object? sender, int tabIndex)
    {
        MainTabControl.SelectedIndex = tabIndex;
    }

    private async void UserControl_Drop(object? sender, DragEventArgs e)
    {
        var file = e.DataTransfer.TryGetFiles()?.FirstOrDefault();
        if (file is { })
        {
            var filePath = file.Path.LocalPath;
            if (!File.Exists(filePath))
                return;

            await _dialogService.ShowOpenDatabaseDialog(filePath, ownerToken: _viewModel.ViewModelToken);
        }
    }
}
