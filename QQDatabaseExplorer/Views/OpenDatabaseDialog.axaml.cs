using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using QQDatabaseExplorer.Models.Messenger;
using QQDatabaseExplorer.ViewModels;
using CommunityToolkit.Mvvm.Messaging;

namespace QQDatabaseExplorer;

public partial class OpenDatabaseDialog : Window, IRecipient<CloseDatabaseDialogMessage>
{
    public OpenDatabaseDialogViewModel ViewModel { get; }

    public OpenDatabaseDialog(IMessenger messenger, OpenDatabaseDialogViewModel viewModel)
    {
        ViewModel = new(messenger);
        DataContext = viewModel;
        InitializeComponent();

        messenger.Register(this);
    }

    public void Receive(CloseDatabaseDialogMessage message)
    {
        Close();
    }
}