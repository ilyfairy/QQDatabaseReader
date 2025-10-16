using CommunityToolkit.Mvvm.ComponentModel;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public ViewModelToken ViewModelToken { get; } = new();

    [ObservableProperty]
    public partial int TabIndex { get; set; }

}
