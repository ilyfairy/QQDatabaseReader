using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using QQDatabaseExplorer.ViewModels;

namespace QQDatabaseExplorer.Views;

public partial class DatabaseTab : UserControl
{
    public DatabaseTab(DatabaseTabViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

}