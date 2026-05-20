using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.Threading.Tasks;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.ViewModels;

namespace QQDatabaseExplorer;

public partial class OpenDatabaseDialog : Window
{
    public OpenDatabaseDialogViewModel ViewModel { get; }

    public OpenDatabaseDialog(OpenDatabaseDialogViewModel viewModel, ViewModelTokenService viewModelTokenService)
    {
        viewModelTokenService.AutoRegister(viewModel.ViewModelToken, this);

        ViewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();
    }

    private async void PickNtDataPathButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!StorageProvider.CanPickFolder)
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = ViewModel.IsAndroidQQNT ? "选择 MobileQQ 目录" : "选择 nt_data 目录",
            AllowMultiple = false,
        });

        var folderPath = folders.Count > 0
            ? folders[0].TryGetLocalPath()
            : null;
        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            if (ViewModel.IsAndroidQQNT)
                ViewModel.MobileQQPath = folderPath;
            else
                ViewModel.NtDataPath = folderPath;
        }
    }

    private async void PickPCQQDataPathButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!StorageProvider.CanPickFolder)
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 PCQQ 数据目录",
            AllowMultiple = false,
        });

        var folderPath = folders.Count > 0
            ? folders[0].TryGetLocalPath()
            : null;
        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            ViewModel.PCQQDataPath = folderPath;
        }
    }

    private async void PickNtMessageDbButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (await PickDatabaseFileAsync("选择 nt_msg.db") is { } filePath)
            ViewModel.UsePickedNtMessageDbPath(filePath);
    }

    private async void PickNtGroupInfoDbButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (await PickDatabaseFileAsync("选择 group_info.db") is { } filePath)
            ViewModel.UsePickedNtGroupInfoDbPath(filePath);
    }

    private async void PickNtGroupMessageFtsDbButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (await PickDatabaseFileAsync("选择 group_msg_fts.db") is { } filePath)
            ViewModel.UsePickedNtGroupMessageFtsDbPath(filePath);
    }

    private async void PickNtProfileInfoDbButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (await PickDatabaseFileAsync("选择 profile_info.db") is { } filePath)
            ViewModel.UsePickedNtProfileInfoDbPath(filePath);
    }

    private async void PickPCQQMessageDbButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (await PickDatabaseFileAsync("选择 PCQQ Msg3.0.db") is { } filePath)
            ViewModel.UsePickedPCQQMessageDbPath(filePath);
    }

    private async void PickPCQQInfoDbButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!StorageProvider.CanOpen)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 PCQQ Info.db",
            AllowMultiple = false,
        });

        var filePath = files.Count > 0
            ? files[0].TryGetLocalPath()
            : null;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            ViewModel.UsePickedPCQQInfoDbPath(filePath);
        }
    }

    private async Task<string?> PickDatabaseFileAsync(string title)
    {
        if (!StorageProvider.CanOpen)
            return null;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("数据库文件")
                {
                    Patterns = ["*.db", "*.sqlite", "*.sqlite3"],
                },
                FilePickerFileTypes.All,
            ],
        });

        return files.Count > 0
            ? files[0].TryGetLocalPath()
            : null;
    }
}
