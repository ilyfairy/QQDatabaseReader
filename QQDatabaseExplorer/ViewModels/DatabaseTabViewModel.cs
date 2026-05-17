using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels;

public partial class DatabaseTabViewModel : ViewModelBase
{
    private readonly QQDatabaseService _qqDatabaseService;
    private readonly IDialogService _dialogService;

    public ViewModelToken ViewModelToken { get; } = new();

    public ObservableCollection<LoadedDatabaseGroup> DatabaseGroups => _qqDatabaseService.DatabaseGroups;

    public DatabaseTabViewModel(QQDatabaseService qqDatabaseService, IDialogService dialogService)
    {
        _qqDatabaseService = qqDatabaseService;
        _dialogService = dialogService;
    }

    [RelayCommand]
    public async Task EditDatabaseGroup(LoadedDatabaseGroup group)
    {
        var config = _qqDatabaseService.CreateConfigForGroup(group);
        if (config is null)
            return;

        var updatedConfig = await _dialogService.ShowEditDatabaseDialog(config, ViewModelToken);
        if (updatedConfig is null)
            return;

        _qqDatabaseService.ReplaceDatabaseGroup(group, updatedConfig);
    }

    [RelayCommand]
    public void RemoveDatabaseGroup(LoadedDatabaseGroup group)
    {
        _qqDatabaseService.RemoveDatabaseGroup(group);
    }

    [RelayCommand]
    public void RemoveDatabase(LoadedDatabaseItem item)
    {
        _qqDatabaseService.RemoveDatabaseItem(item);
    }

    [RelayCommand]
    public async Task ExportDatabase(LoadedDatabaseItem item)
    {
        if (item.Database is { } database)
            await _dialogService.ShowExportDatabaseDialog(database, ViewModelToken);
    }
}
