using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using QQDatabaseExplorer.Models.Messenger;
using QQDatabaseExplorer.Services;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels;

public partial class DatabaseTabViewModel : ViewModelBase, IRecipient<AddDatabaseMessage>, IRecipient<RemoveDatabaseMessage>
{
    private readonly IMessenger _messenger;
    private readonly QQDatabaseService _qqDatabaseService;

    public ObservableCollection<IQQDatabase> DatabaseList { get; } = new();

    public DatabaseTabViewModel(IMessenger messenger, QQDatabaseService qqDatabaseService)
    {
        _messenger = messenger;
        _qqDatabaseService = qqDatabaseService;
        _messenger.Register<AddDatabaseMessage>(this);
        _messenger.Register<RemoveDatabaseMessage>(this);
    }

    public void Receive(AddDatabaseMessage message)
    {
        DatabaseList.Add(message.Database);
    }

    public void Receive(RemoveDatabaseMessage message)
    {
        DatabaseList.Remove(message.Database);
    }

    [RelayCommand]
    public void RemoveDatabase(IQQDatabase qqDatabase)
    {
        _qqDatabaseService.RemoveDatabase(qqDatabase);
    }

    [RelayCommand]
    public async Task ExportDatabase(IQQDatabase qqDatabase)
    {
        await _qqDatabaseService.ExportDatabase(qqDatabase);
    }
}