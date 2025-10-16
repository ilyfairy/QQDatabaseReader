using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.ViewModels;

namespace QQDatabaseExplorer.Views;

public partial class DatabaseTab : UserControl
{
    public DatabaseTab(DatabaseTabViewModel viewModel, ViewModelTokenService viewModelTokenService)
    {
        viewModelTokenService.AutoRegister(viewModel.ViewModelToken, this);

        DataContext = viewModel;
        InitializeComponent();
    }

}