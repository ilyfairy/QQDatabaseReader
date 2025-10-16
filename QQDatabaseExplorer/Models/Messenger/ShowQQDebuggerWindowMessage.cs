
using System.Threading.Tasks;
using QQDatabaseExplorer.ViewModels;

namespace QQDatabaseExplorer.Models.Messenger;

public record ShowQQDebuggerWindowMessage(ViewModelToken? Token, TaskCompletionSource<QQDebuggerWindowViewModel> Completion);
