using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using QQDatabaseExplorer.ViewModels;
using QQDatabaseExplorer.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using QQDatabaseExplorer.Controls;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer;

public partial class App : Application
{
    public static IHost Host { get; private set; } = null!;

    public override void Initialize()
    {
        SmoothScrollViewer.Initialize();
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();

        builder.Services.AddSingleton<QQDatabaseService>();
        builder.Services.AddSingleton<ViewModelTokenService>();
        builder.Services.AddSingleton<ConfigService>();
        builder.Services.AddSingleton<AppSettingsService>();
        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<IClipboardService, ClipboardService>();
        builder.Services.AddTransient<ViewModelToken>();

        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<MainView>();
        builder.Services.AddSingleton<MainViewModel>();

        builder.Services.AddSingleton<MessageTab>();
        builder.Services.AddSingleton<MessageTabViewModel>();

        builder.Services.AddSingleton<DatabaseTab>();
        builder.Services.AddSingleton<DatabaseTabViewModel>();

        builder.Services.AddSingleton<GroupMessageSearchView>();
        builder.Services.AddSingleton<GroupMessageSearchViewModel>();

        builder.Services.AddSingleton<SettingsTab>();
        builder.Services.AddSingleton<SettingsTabViewModel>();

        builder.Services.AddScoped<OpenDatabaseDialog>();
        builder.Services.AddScoped<OpenDatabaseDialogViewModel>();

        builder.Services.AddScoped<ExportDatabaseDialog>();
        builder.Services.AddScoped<ExportDatabaseDialogViewModel>();

        builder.Services.AddScoped<QQDebuggerWindow>();
        builder.Services.AddScoped<QQDebuggerWindowViewModel>();

        builder.Services.AddScoped<PCQQKeyDumpWindow>();
        builder.Services.AddScoped<PCQQKeyDumpWindowViewModel>();

        builder.Services.AddScoped<ProtobufAnalyzerDialog>();
        builder.Services.AddScoped<ProtobufAnalyzerDialogViewModel>();

        builder.Services.AddScoped<MessageFilterDialog>();
        builder.Services.AddScoped<MessageFilterDialogViewModel>();

        Host = builder.Build();
        Host.Services.GetRequiredService<AppSettingsService>().Load();

        Host.Start();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.Exit += (_, _) => Host.Dispose();
            desktop.MainWindow = Host.Services.GetRequiredService<MainWindow>();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = Host.Services.GetRequiredService<MainView>();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
