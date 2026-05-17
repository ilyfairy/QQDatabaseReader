using System;
using CommunityToolkit.Mvvm.ComponentModel;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public ViewModelToken ViewModelToken { get; } = new();

    public const int MessageTabIndex = 0;

    public event EventHandler<int>? TabNavigationRequested;

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    public void ShowMessageTab()
    {
        SelectTab(MessageTabIndex);
    }

    public void SelectTab(int tabIndex)
    {
        SelectedTabIndex = tabIndex;
        TabNavigationRequested?.Invoke(this, tabIndex);
    }

}
