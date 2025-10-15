using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using QQDatabaseExplorer.Models.Messenger;
using QQDatabaseExplorer.ViewModels;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.Services;

public class QQDatabaseService
{
    private readonly IMessenger _messenger;
    private readonly IServiceProvider _serviceProvider;

    public QQGroupInfoReader? GroupInfoDatabase { get; private set; }

    public QQMessageReader? MessageDatabase { get; private set; }

    //public ObservableCollection<IQQDatabase> DatabaseList { get; } = new();

    public QQDatabaseService(IMessenger messenger, IServiceProvider serviceProvider)
    {
        _messenger = messenger;
        _serviceProvider = serviceProvider;
    }

    public QQGroupInfoReader LoadGroupInfoDatabase(string grouInfoDb, string? password = null)
    {
        QQGroupInfoReader groupInfoDatabase;
        if (password is null)
        {
            groupInfoDatabase = new(grouInfoDb);
        }
        else
        {
            groupInfoDatabase = new(grouInfoDb, password);
        }
        groupInfoDatabase.Initialize();
        GroupInfoDatabase = groupInfoDatabase;
        //DatabaseList.Add(groupInfoDatabase);
        _messenger.Send(new AddDatabaseMessage(groupInfoDatabase));

        return groupInfoDatabase;
    }

    public QQMessageReader LoadMessageDatabase(string messageDb, string? password = null)
    {
        QQMessageReader messageDatabase;
        if (password is null)
        {
            messageDatabase = new(messageDb);
        }
        else
        {
            messageDatabase = new(messageDb, password);
        }
        messageDatabase.Initialize();
        MessageDatabase = messageDatabase;
        //DatabaseList.Add(messageDatabase);
        _messenger.Send(new AddDatabaseMessage(messageDatabase));

        return messageDatabase;
    }

    public void RemoveDatabase(IQQDatabase qqDatabase)
    {
        if (qqDatabase.Equals(GroupInfoDatabase))
        {
            GroupInfoDatabase = null;
            _messenger.Send(new RemoveDatabaseMessage(qqDatabase));
        }
        else if (qqDatabase.Equals(MessageDatabase))
        {
            MessageDatabase = null;
            _messenger.Send(new RemoveDatabaseMessage(qqDatabase));
        }
    }

    public async Task ExportDatabase(IQQDatabase qqDatabase)
    {
        using var scope = _serviceProvider.CreateScope();
        var vm = scope.ServiceProvider.GetRequiredService<ExportDatabaseDialogViewModel>();
        vm.Database = qqDatabase;
        var dialog = scope.ServiceProvider.GetRequiredService<ExportDatabaseDialog>();
        dialog.Show();
    }
}
