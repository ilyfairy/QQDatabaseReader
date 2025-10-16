using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using QQDatabaseExplorer.Models.Messenger;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.ViewModels;
using QQDatabaseExplorer.Views;
using Ursa.Controls;

namespace QQDatabaseExplorer;

public partial class ExportDatabaseDialog : Window, IRecipient<CloseExportDatabaseDialogMessage>
{
    private readonly MainWindow _mainWindow;

    public ExportDatabaseDialogViewModel ViewModel { get; }

    public ExportDatabaseDialog(ExportDatabaseDialogViewModel viewModel, IMessenger messenger, MainWindow mainWindow, ViewModelTokenService viewModelTokenService)
    {
        viewModelTokenService.AutoRegister(viewModel.ViewModelToken, this);

        DataContext = viewModel;
        messenger.Register<CloseExportDatabaseDialogMessage>(this);
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


}