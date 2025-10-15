using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.Messaging;
using QQDatabaseExplorer.Models.Messenger;
using QQDatabaseExplorer.ViewModels;
using QQDatabaseExplorer.Views;

namespace QQDatabaseExplorer;

public partial class ExportDatabaseDialog : Window, IRecipient<CloseExportDatabaseDialogMessage>
{
    private readonly MainWindow _mainWindow;

    public ExportDatabaseDialog(ExportDatabaseDialogViewModel viewModel, IMessenger messenger, MainWindow mainWindow)
    {
        DataContext = viewModel;
        messenger.Register(this);
        InitializeComponent();
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