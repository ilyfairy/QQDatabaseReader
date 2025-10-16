
using System.Threading.Tasks;

namespace QQDatabaseExplorer.Models.Messenger;

public record ShowMessageBoxMessage(string Message, string? Title, MessageBoxToken Token, TaskCompletionSource TaskCompletionSource);
