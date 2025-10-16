using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using QQDatabaseExplorer.Models.Messenger;
using QQDatabaseExplorer.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using Ursa.Controls;

namespace QQDatabaseExplorer;

public partial class OpenDatabaseDialog : Window, IRecipient<CloseDatabaseDialogMessage>, IRecipient<ShowMessageBoxMessage>
{
    public OpenDatabaseDialogViewModel ViewModel { get; }

    public OpenDatabaseDialog(IMessenger messenger, OpenDatabaseDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        messenger.Register<CloseDatabaseDialogMessage>(this);
        messenger.Register<ShowMessageBoxMessage>(this);
    }

    public void Receive(CloseDatabaseDialogMessage message)
    {
        Close();
    }

    public async void Receive(ShowMessageBoxMessage message)
    {
        if (message.Token == ViewModel.MessageBoxToken)
        {
            await MessageBox.ShowAsync(this, message.Message, message.Title ?? string.Empty);
            message.TaskCompletionSource.SetResult();
        }
    }
}