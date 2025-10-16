
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace QQDatabaseExplorer.Models.Messenger;

public record ShowMessageBoxMessage(string Message, string? Title, ViewModelToken Token, TaskCompletionSource Completion);
