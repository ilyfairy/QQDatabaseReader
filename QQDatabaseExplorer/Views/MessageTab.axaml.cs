using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.ViewModels;

namespace QQDatabaseExplorer.Views;

public partial class MessageTab : UserControl
{
    private readonly QQDatabaseService _qqDatabaseService;
    private readonly IMessenger _messenger;
    private readonly IServiceProvider _serviceProvider;

    public MessageTab(MessageTabViewModel viewModel, QQDatabaseService qqDatabaseService, IMessenger messenger, IServiceProvider serviceProvider)
    {
        _qqDatabaseService = qqDatabaseService;
        _messenger = messenger;
        _serviceProvider = serviceProvider;
        DataContext = viewModel;
        InitializeComponent();
    }


}