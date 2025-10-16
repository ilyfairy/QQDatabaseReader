
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Models.Messenger;

namespace QQDatabaseExplorer.Services;

public class MessageBoxService(IMessenger messenger)
{
    public async Task ShowAsync(string text, string? title, ViewModelToken messageBoxToken)
    {
        var msg = new ShowMessageBoxMessage(text, title, messageBoxToken, new TaskCompletionSource());
        messenger.Send(msg);
        await msg.Completion.Task;
    }
}
