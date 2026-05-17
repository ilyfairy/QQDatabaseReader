using Avalonia.Controls;
using QQDatabaseExplorer.ViewModels;

namespace QQDatabaseExplorer.Views;

public partial class SettingsTab : UserControl
{
    public SettingsTab(SettingsTabViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    public SettingsTab()
    {
        InitializeComponent();
    }
}
