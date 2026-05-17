using Avalonia.Controls;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.ViewModels;

namespace QQDatabaseExplorer;

public partial class QQDebuggerWindow : Window
{
    public QQDebuggerWindowViewModel ViewModel { get; }

    public QQDebuggerWindow(QQDebuggerWindowViewModel viewModel, ViewModelTokenService viewModelTokenService)
    {
        viewModelTokenService.AutoRegister(viewModel.ViewModelToken, this);

        DataContext = viewModel;
        ViewModel = viewModel;

        InitializeComponent();
    }
}
