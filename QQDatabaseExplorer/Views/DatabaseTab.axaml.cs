using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using System.Collections.Generic;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.Utilities;
using QQDatabaseExplorer.ViewModels;

namespace QQDatabaseExplorer.Views;

public partial class DatabaseTab : UserControl
{
    private readonly DatabaseTabViewModel _viewModel;

    public DatabaseTab(DatabaseTabViewModel viewModel, ViewModelTokenService viewModelTokenService)
    {
        viewModelTokenService.AutoRegister(viewModel.ViewModelToken, this);

        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void DatabaseGroup_ContextRequested(object? sender, Avalonia.Input.ContextRequestedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not LoadedDatabaseGroup group ||
            sender is not Control owner)
        {
            return;
        }

        var contextMenu = new ContextMenu
        {
            ItemsSource = new Control[]
            {
                new MenuItem
                {
                    Header = "编辑",
                    Command = _viewModel.EditDatabaseGroupCommand,
                    CommandParameter = group,
                },
                new MenuItem
                {
                    Header = "移除",
                    Command = _viewModel.RemoveDatabaseGroupCommand,
                    CommandParameter = group,
                },
            },
        };
        ContextMenuHelper.Open(owner, contextMenu);
        e.Handled = true;
    }

    private void DatabaseItem_ContextRequested(object? sender, Avalonia.Input.ContextRequestedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not LoadedDatabaseItem item ||
            sender is not Control owner)
        {
            return;
        }

        var menuItems = new List<Control>();
        if (CanExportDatabase(item))
        {
            menuItems.Add(new MenuItem
            {
                Header = "导出成无加密数据库(同时可以修复损坏的数据)",
                Command = _viewModel.ExportDatabaseCommand,
                CommandParameter = item,
            });
        }

        menuItems.Add(new MenuItem
        {
            Header = "移除",
            Command = _viewModel.RemoveDatabaseCommand,
            CommandParameter = item,
            IsEnabled = item.Database is not null,
        });

        var contextMenu = new ContextMenu { ItemsSource = menuItems };
        ContextMenuHelper.Open(owner, contextMenu);
        e.Handled = true;
    }

    private static bool CanExportDatabase(LoadedDatabaseItem item)
    {
        return item.CanExport && item.Kind != LoadedDatabaseItemKind.IcalinguaMessageDb;
    }
}
