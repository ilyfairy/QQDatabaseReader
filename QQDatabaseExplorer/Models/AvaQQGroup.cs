using CommunityToolkit.Mvvm.ComponentModel;

namespace QQDatabaseExplorer.Models;

public partial class AvaQQGroup : ObservableObject
{
    public int GroupId { get; set; }

    [ObservableProperty]
    public partial string? GroupName { get; set; }
}
