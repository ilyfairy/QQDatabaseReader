using System.Threading.Tasks;
using QQDatabaseExplorer.ViewModels;

namespace QQDatabaseExplorer.Models.Messenger;

public record ShowOpenDatabaseDialogMessage(string DatabaseFilePath, ViewModelToken? Token, TaskCompletionSource<OpenDatabaseDialogViewModel> Completion);
