using System.Threading.Tasks;
using QQDatabaseExplorer.Models;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.Services;

public interface IDialogService
{
    Task ShowMessageBox(string message, string? title = null, ViewModelToken? ownerToken = null);

    Task ShowOpenDatabaseDialog(
        string? databaseFilePath = null,
        DatabasePlatformType? platformType = null,
        ViewModelToken? ownerToken = null);

    Task<DatabaseConfig?> ShowEditDatabaseDialog(
        DatabaseConfig initialConfig,
        ViewModelToken? ownerToken = null);

    Task ShowExportDatabaseDialog(IQQDatabase database, ViewModelToken? ownerToken = null);

    Task<MessageFilterCriteria?> ShowMessageFilterDialog(
        MessageFilterDialogRequest request,
        ViewModelToken? ownerToken = null);

    Task ShowChatExportDialog(AvaQQGroup conversation, ViewModelToken? ownerToken = null);

    Task<string> ShowQQDebuggerWindow(ViewModelToken? ownerToken = null);

    void ShowPCQQKeyDumpWindow(ViewModelToken? ownerToken = null);

    void Close(ViewModelToken ownerToken);
}
