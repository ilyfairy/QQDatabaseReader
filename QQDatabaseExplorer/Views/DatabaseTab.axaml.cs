using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using QQDatabaseExplorer.Services;
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
        OpenContextMenu(owner, contextMenu);
        e.Handled = true;
    }

    private void DatabaseItem_ContextRequested(object? sender, Avalonia.Input.ContextRequestedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not LoadedDatabaseItem item ||
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
                    Header = "导出成无加密数据库(同时可以修复损坏的数据)",
                    Command = _viewModel.ExportDatabaseCommand,
                    CommandParameter = item,
                    IsEnabled = item.Database is not null,
                },
                new MenuItem
                {
                    Header = "移除",
                    Command = _viewModel.RemoveDatabaseCommand,
                    CommandParameter = item,
                    IsEnabled = item.Database is not null,
                },
            },
        };
        OpenContextMenu(owner, contextMenu);
        e.Handled = true;
    }

    private static void OpenContextMenu(Control owner, ContextMenu contextMenu)
    {
        var previousContextMenu = owner.ContextMenu;
        contextMenu.Closed += (_, _) =>
        {
            if (ReferenceEquals(owner.ContextMenu, contextMenu))
                owner.ContextMenu = previousContextMenu;
        };

        owner.ContextMenu = contextMenu;
        contextMenu.Open(owner);
    }
}
