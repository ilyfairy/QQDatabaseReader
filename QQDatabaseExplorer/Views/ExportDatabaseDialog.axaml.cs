using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.Messaging;
using QQDatabaseExplorer.Models.Messenger;
using QQDatabaseExplorer.ViewModels;

namespace QQDatabaseExplorer;

public partial class ExportDatabaseDialog : Window, IRecipient<CloseExportDatabaseDialogMessage>
{
    public ExportDatabaseDialog(ExportDatabaseDialogViewModel viewModel, IMessenger messenger)
    {
        DataContext = viewModel;
        messenger.Register(this);
        InitializeComponent();
    }

    public void Receive(CloseExportDatabaseDialogMessage message)
    {
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
    }
}