using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using QQDatabaseExplorer.ViewModels;
using QQDatabaseExplorer.Views;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer;

public partial class App : Application
{
    public static IHost Host { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();

        builder.Services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
        builder.Services.AddSingleton<QQDatabaseService>();
        builder.Services.AddTransient<MessageBoxService>();
        builder.Services.AddSingleton<ViewModelTokenService>();
        builder.Services.AddTransient<ViewModelToken>();

        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<MainView>();
        builder.Services.AddSingleton<MainViewModel>();

        builder.Services.AddSingleton<MessageTab>();
        builder.Services.AddSingleton<MessageTabViewModel>();

        builder.Services.AddSingleton<DatabaseTab>();
        builder.Services.AddSingleton<DatabaseTabViewModel>();

        builder.Services.AddScoped<OpenDatabaseDialog>();
        builder.Services.AddScoped<OpenDatabaseDialogViewModel>();

        builder.Services.AddScoped<ExportDatabaseDialog>();
        builder.Services.AddScoped<ExportDatabaseDialogViewModel>();

        builder.Services.AddScoped<QQDebuggerWindow>();
        builder.Services.AddScoped<QQDebuggerWindowViewModel>();

        Host = builder.Build();

        Host.Start();

        // Line below is needed to remove Avalonia data validation.
        // Without this line you will get duplicate validations from both Avalonia and CT
        BindingPlugins.DataValidators.RemoveAt(0);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = Host.Services.GetRequiredService<MainWindow>();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = Host.Services.GetRequiredService<MainView>();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
