using Avalonia.Controls;
using QQDatabaseExplorer.ViewModels;

namespace QQDatabaseExplorer.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel mainViewModel, MainView mainView)
    {
        DataContext = mainViewModel;
        Content = mainView;
        InitializeComponent();
    }
}
