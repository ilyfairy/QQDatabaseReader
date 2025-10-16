using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using QQDatabaseExplorer.Models.Messenger;
using QQDatabaseExplorer.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using Ursa.Controls;
using QQDatabaseExplorer.Services;

namespace QQDatabaseExplorer;

public partial class OpenDatabaseDialog : Window, IRecipient<CloseDatabaseDialogMessage>
{
    public OpenDatabaseDialogViewModel ViewModel { get; }

    public OpenDatabaseDialog(IMessenger messenger, OpenDatabaseDialogViewModel viewModel, ViewModelTokenService viewModelTokenService)
    {
        viewModelTokenService.AutoRegister(viewModel.ViewModelToken, this);

        ViewModel = viewModel;
        DataContext = viewModel;
        messenger.Register<CloseDatabaseDialogMessage>(this);
        InitializeComponent();
    }

    public void Receive(CloseDatabaseDialogMessage message)
    {
        Close();
    }

}