using System;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using QQDatabaseExplorer.Models.Messenger;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.ViewModels;
using Ursa.Controls;

namespace QQDatabaseExplorer.Views;

public partial class MainWindow : Window, IRecipient<ShowMessageBoxMessage>, IRecipient<ShowQQDebuggerWindowMessage>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IMessenger _messenger;
    private readonly ViewModelTokenService _viewModelTokenService;

    public MainWindow(MainViewModel mainViewModel, MainView mainView, IServiceProvider serviceProvider, IMessenger messenger, ViewModelTokenService viewModelTokenService)
    {
        DataContext = mainViewModel;
        Content = mainView;
        _serviceProvider = serviceProvider;
        _messenger = messenger;
        _viewModelTokenService = viewModelTokenService;
        _messenger.Register<ShowMessageBoxMessage>(this);
        _messenger.Register<ShowQQDebuggerWindowMessage>(this);

        InitializeComponent();
    }


    public async void Receive(ShowMessageBoxMessage message)
    {
        if (!_viewModelTokenService.Tokens.TryGetValue(message.Token, out var owner))
        {
            owner = this;
        }
        await MessageBox.ShowAsync(GetTopLevel(owner) as Window ?? this, message.Message, message.Title ?? string.Empty);
        message.Completion.SetResult();
    }

    public async void Receive(ShowQQDebuggerWindowMessage message)
    {
        if(message.Token is null || !_viewModelTokenService.Tokens.TryGetValue(message.Token, out var owner))
        {
            owner = this;
        }

        var scope = _serviceProvider.CreateScope();
        var window = scope.ServiceProvider.GetRequiredService<QQDebuggerWindow>();
        await window.ShowDialog(GetTopLevel(owner) as Window ?? this);
        message.Completion.SetResult(scope.ServiceProvider.GetRequiredService<QQDebuggerWindowViewModel>());
    }

}
