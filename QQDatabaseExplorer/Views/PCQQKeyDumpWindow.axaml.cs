using Avalonia.Controls;
using Avalonia.Input;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.ViewModels;
using Ursa.Controls;

namespace QQDatabaseExplorer.Views;

public partial class PCQQKeyDumpWindow : UrsaWindow
{
    public PCQQKeyDumpWindowViewModel ViewModel { get; }

    public PCQQKeyDumpWindow(
        PCQQKeyDumpWindowViewModel viewModel,
        ViewModelTokenService viewModelTokenService)
    {
        viewModelTokenService.AutoRegister(viewModel.ViewModelToken, this);

        DataContext = viewModel;
        ViewModel = viewModel;

        InitializeComponent();

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.LogText) &&
                LogTextBox.SelectionStart == LogTextBox.SelectionEnd)
            {
                LogTextBox.CaretIndex = LogTextBox.Text?.Length ?? 0;
            }
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape || (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
