using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using QQDatabaseExplorer.Models.Messenger;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.ViewModels;
using Ursa.Controls;

namespace QQDatabaseExplorer;

public partial class QQDebuggerWindow : Window
{
    private readonly IServiceProvider _serviceProvider;

    public QQDebuggerWindowViewModel ViewModel { get; }

    public QQDebuggerWindow(QQDebuggerWindowViewModel viewModel, IMessenger messenger, IServiceProvider serviceProvider, ViewModelTokenService viewModelTokenService)
    {
        viewModelTokenService.AutoRegister(viewModel.ViewModelToken, this);

        DataContext = viewModel;
        ViewModel = viewModel;
        _serviceProvider = serviceProvider;
        InitializeComponent();
    }

}