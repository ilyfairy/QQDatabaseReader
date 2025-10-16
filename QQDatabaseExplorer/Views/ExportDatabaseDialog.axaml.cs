using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using QQDatabaseExplorer.Models.Messenger;
using QQDatabaseExplorer.ViewModels;
using QQDatabaseExplorer.Views;
using Ursa.Controls;

namespace QQDatabaseExplorer;

public partial class ExportDatabaseDialog : Window, IRecipient<CloseExportDatabaseDialogMessage>, IRecipient<ShowMessageBoxMessage>
{
    private readonly MainWindow _mainWindow;

    public ExportDatabaseDialogViewModel ViewModel { get; }

    public ExportDatabaseDialog(ExportDatabaseDialogViewModel viewModel, IMessenger messenger, MainWindow mainWindow)
    {
        DataContext = viewModel;
        messenger.Register<CloseExportDatabaseDialogMessage>(this);
        messenger.Register<ShowMessageBoxMessage>(this);
        InitializeComponent();
        ViewModel = viewModel;
        _mainWindow = mainWindow;
    }

    public void Receive(CloseExportDatabaseDialogMessage message)
    {
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
    }

    public Task ShowDialog() => ShowDialog(_mainWindow);

    public async void Receive(ShowMessageBoxMessage message)
    {
        if (message.Token == ViewModel.MessageBoxToken)
        {
            await MessageBox.ShowAsync(this, message.Message, message.Title ?? string.Empty);
            message.TaskCompletionSource.SetResult();
        } 
    }
}