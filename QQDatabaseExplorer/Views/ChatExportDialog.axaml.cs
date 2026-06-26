using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.Utilities;
using QQDatabaseExplorer.ViewModels;

namespace QQDatabaseExplorer.Views;

public partial class ChatExportDialog : Window
{
    public ChatExportDialogViewModel ViewModel { get; }

    public ChatExportDialog(ChatExportDialogViewModel viewModel, ViewModelTokenService viewModelTokenService)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        viewModelTokenService.AutoRegister(viewModel.ViewModelToken, this);
        InitializeComponent();
    }

    private async void ChooseOutputDirectory_Click(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanPickFolder)
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择聊天记录导出目录",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { Length: > 0 } path)
        {
            ViewModel.OutputDirectory = path;
        }
    }

    private void OpenResult_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.Result is { PrimaryOutputPath: { Length: > 0 } primaryOutputPath } &&
            ShellFileLocator.ShowFileInFolder(primaryOutputPath))
        {
            return;
        }

        if (ViewModel.Result is { ExportDirectory: { Length: > 0 } exportDirectory })
            ShellFileLocator.ShowDirectory(exportDirectory);
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
