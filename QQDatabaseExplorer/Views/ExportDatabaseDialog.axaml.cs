using Avalonia.Controls;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.ViewModels;

namespace QQDatabaseExplorer;

public partial class ExportDatabaseDialog : Window
{
    public ExportDatabaseDialogViewModel ViewModel { get; }

    public ExportDatabaseDialog(ExportDatabaseDialogViewModel viewModel, ViewModelTokenService viewModelTokenService)
    {
        viewModelTokenService.AutoRegister(viewModel.ViewModelToken, this);

        DataContext = viewModel;
        InitializeComponent();
        ViewModel = viewModel;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
    }
}
