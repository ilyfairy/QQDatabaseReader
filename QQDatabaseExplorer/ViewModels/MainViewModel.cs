using CommunityToolkit.Mvvm.ComponentModel;

namespace QQDatabaseExplorer.ViewModels;

public partial class MainViewModel : ViewModelBase
{

    [ObservableProperty]
    public partial int TabIndex { get; set; }

}
