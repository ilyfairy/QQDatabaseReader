using Avalonia.Controls;
using Avalonia.Input;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.ViewModels;

namespace QQDatabaseExplorer.Views;

public partial class MessageFilterDialog : Window
{
    public MessageFilterDialogViewModel ViewModel { get; }

    public MessageFilterDialog(MessageFilterDialogViewModel viewModel, ViewModelTokenService viewModelTokenService)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        viewModelTokenService.AutoRegister(viewModel.ViewModelToken, this);
        InitializeComponent();
    }

    private void SenderCandidateItem_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MessageSenderFilterOption senderOption)
        {
            ViewModel.AddSender(senderOption);
            e.Handled = true;
        }
    }

    private void SenderCandidateList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as ListBox)?.SelectedItem is MessageSenderFilterOption senderOption)
        {
            ViewModel.AddSender(senderOption);
            e.Handled = true;
        }
    }
}
