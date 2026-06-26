using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.ViewModels;
using QQDatabaseExplorer.Views;
using QQDatabaseReader;
using QQDatabaseReader.Database;
using Ursa.Controls;

namespace QQDatabaseExplorer.Services;

public class DialogService : IDialogService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly DatabaseConfigApplicationService _databaseConfigApplicationService;
    private readonly ViewModelTokenService _viewModelTokenService;

    public DialogService(
        IServiceProvider serviceProvider,
        DatabaseConfigApplicationService databaseConfigApplicationService,
        ViewModelTokenService viewModelTokenService)
    {
        _serviceProvider = serviceProvider;
        _databaseConfigApplicationService = databaseConfigApplicationService;
        _viewModelTokenService = viewModelTokenService;
    }

    public async Task ShowMessageBox(string message, string? title = null, ViewModelToken? ownerToken = null)
    {
        var owner = ResolveOwner(ownerToken);
        if (owner is null)
        {
            await MessageBox.ShowAsync(message, title ?? string.Empty);
            return;
        }

        await MessageBox.ShowAsync(owner, message, title ?? string.Empty);
    }

    public async Task ShowOpenDatabaseDialog(
        string? databaseFilePath = null,
        DatabasePlatformType? platformType = null,
        ViewModelToken? ownerToken = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var viewModel = scope.ServiceProvider.GetRequiredService<OpenDatabaseDialogViewModel>();
        if (platformType is { } type)
        {
            viewModel.SetInitialPlatform(type);
        }

        if (!string.IsNullOrWhiteSpace(databaseFilePath))
        {
            viewModel.SetInitialDatabaseFile(databaseFilePath);
        }

        var dialog = scope.ServiceProvider.GetRequiredService<OpenDatabaseDialog>();
        await ShowDialog(dialog, ownerToken);
        if (viewModel.ResultConfig is { } config)
            await ApplyDatabaseConfigAsync(config, ownerToken);
    }

    public async Task<DatabaseConfig?> ShowEditDatabaseDialog(
        DatabaseConfig initialConfig,
        ViewModelToken? ownerToken = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var viewModel = scope.ServiceProvider.GetRequiredService<OpenDatabaseDialogViewModel>();
        viewModel.SetInitialConfig(initialConfig);

        var dialog = scope.ServiceProvider.GetRequiredService<OpenDatabaseDialog>();
        await ShowDialog(dialog, ownerToken);
        return viewModel.ResultConfig;
    }

    private async Task ApplyDatabaseConfigAsync(DatabaseConfig config, ViewModelToken? ownerToken)
    {
        try
        {
            await _databaseConfigApplicationService.ApplyAsync(config);
        }
        catch (Exception ex)
        {
            await ShowMessageBox($"打开 {config.Type} 数据库失败:\n{ex.Message}", "错误", ownerToken);
        }
    }

    public async Task ShowExportDatabaseDialog(IQQDatabase database, ViewModelToken? ownerToken = null)
    {
        if (database.DatabaseType == QQDatabaseType.IcalinguaMessage)
            return;

        using var scope = _serviceProvider.CreateScope();
        var viewModel = scope.ServiceProvider.GetRequiredService<ExportDatabaseDialogViewModel>();
        viewModel.Database = database;

        var dialog = scope.ServiceProvider.GetRequiredService<ExportDatabaseDialog>();
        await ShowDialog(dialog, ownerToken);
    }

    public async Task<MessageFilterCriteria?> ShowMessageFilterDialog(
        MessageFilterDialogRequest request,
        ViewModelToken? ownerToken = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var viewModel = scope.ServiceProvider.GetRequiredService<MessageFilterDialogViewModel>();
        viewModel.Initialize(request);

        var dialog = scope.ServiceProvider.GetRequiredService<MessageFilterDialog>();
        await ShowDialog(dialog, ownerToken);
        return viewModel.ResultFilter;
    }

    public async Task ShowChatExportDialog(AvaQQGroup conversation, ViewModelToken? ownerToken = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var viewModel = scope.ServiceProvider.GetRequiredService<ChatExportDialogViewModel>();
        viewModel.Initialize(conversation);

        var dialog = scope.ServiceProvider.GetRequiredService<ChatExportDialog>();
        await ShowDialog(dialog, ownerToken);
    }

    public async Task<string> ShowQQDebuggerWindow(ViewModelToken? ownerToken = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var viewModel = scope.ServiceProvider.GetRequiredService<QQDebuggerWindowViewModel>();
        var dialog = scope.ServiceProvider.GetRequiredService<QQDebuggerWindow>();

        await ShowDialog(dialog, ownerToken);
        return viewModel.Key;
    }

    public void ShowPCQQKeyDumpWindow(ViewModelToken? ownerToken = null)
    {
        var scope = _serviceProvider.CreateScope();
        var dialog = scope.ServiceProvider.GetRequiredService<PCQQKeyDumpWindow>();
        dialog.Closed += (_, _) => scope.Dispose();
        ShowWindow(dialog, ownerToken);
    }

    public void Close(ViewModelToken ownerToken)
    {
        if (!_viewModelTokenService.Tokens.TryGetValue(ownerToken, out var owner))
            return;

        if (TopLevel.GetTopLevel(owner) is Window window)
        {
            window.Close();
        }
    }

    private async Task ShowDialog(Window dialog, ViewModelToken? ownerToken)
    {
        var owner = ResolveOwner(ownerToken) ?? ResolveMainWindow();
        if (owner is null)
            throw new InvalidOperationException("A dialog owner window could not be resolved.");

        await dialog.ShowDialog(owner);
    }

    private void ShowWindow(Window window, ViewModelToken? ownerToken)
    {
        var owner = ResolveOwner(ownerToken) ?? ResolveMainWindow();
        if (owner is null)
            window.Show();
        else
            window.Show(owner);
    }

    private Window? ResolveOwner(ViewModelToken? ownerToken)
    {
        if (ownerToken is not null &&
            _viewModelTokenService.Tokens.TryGetValue(ownerToken, out var owner) &&
            (owner as Window ?? TopLevel.GetTopLevel(owner) as Window) is { } ownerWindow)
        {
            return ownerWindow;
        }

        return ResolveMainWindow();
    }

    private static Window? ResolveMainWindow()
    {
        return Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
    }
}
