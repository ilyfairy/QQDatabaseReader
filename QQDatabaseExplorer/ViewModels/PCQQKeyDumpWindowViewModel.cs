using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;
using QQDatabaseKeyDumpPCQQ;

namespace QQDatabaseExplorer.ViewModels;

public partial class PCQQKeyDumpWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IDialogService _dialogService;
    private readonly StringBuilder _logBuilder = new();
    private CancellationTokenSource? _dumpCancellation;
    private PCQQKeyDumper? _dumper;
    private bool _disposed;

    public ViewModelToken ViewModelToken { get; } = new();

    [ObservableProperty]
    public partial string LogText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "未开始";

    [ObservableProperty]
    public partial string QQFilePath { get; set; } = string.Empty;

    public PCQQKeyDumpWindowViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
        QQFilePath = FindDefaultQQFilePath();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    public async Task Start()
    {
        if (IsRunning)
            return;
        if (_disposed)
            throw new ObjectDisposedException(nameof(PCQQKeyDumpWindowViewModel));

        ClearLog();

        if (!string.IsNullOrWhiteSpace(QQFilePath) && !File.Exists(QQFilePath))
        {
            AppendLog($"PCQQ 路径不存在: {QQFilePath}");
            StatusText = "启动失败";
            return;
        }

        var oldCancellation = _dumpCancellation;
        var dumpCancellation = new CancellationTokenSource();
        _dumpCancellation = dumpCancellation;
        oldCancellation?.Dispose();
        IsRunning = true;
        StatusText = "正在调试 PCQQ";

        try
        {
            AppendLog("启动 PCQQ Key 获取器.");
            AppendLog("登录 PCQQ 后, 窗口里会显示 Msg3.0.db, Info.db 等数据库对应的密钥, 请手动复制需要的密钥.");
            AppendLog("");

            var dumper = new PCQQKeyDumper(AppendLog, AppendLog);
            _dumper = dumper;
            var exitCode = await Task.Run(() =>
                dumper.Run(
                    new PCQQKeyDumpOptions
                    {
                        QQFilePath = string.IsNullOrWhiteSpace(QQFilePath) ? null : QQFilePath,
                    },
                    dumpCancellation.Token));

            StatusText = exitCode == 0
                ? "已退出"
                : $"已退出, 代码 {exitCode}";
            AppendLog("");
            AppendLog(StatusText);
        }
        catch (OperationCanceledException)
        {
            StatusText = "已停止";
            AppendLog("");
            AppendLog("已停止.");
        }
        catch (Exception ex)
        {
            StatusText = "运行失败";
            AppendLog($"运行失败: {ex.Message}");
        }
        finally
        {
            _dumper?.Dispose();
            _dumper = null;
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    public void Stop()
    {
        StopProcess();
    }

    [RelayCommand]
    public void Close()
    {
        StopProcess();
        _dialogService.Close(ViewModelToken);
    }

    private bool CanStart() => !IsRunning;

    private void StopProcess()
    {
        if (_dumpCancellation is { IsCancellationRequested: false } cancellation)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _dumper?.Stop();
    }

    private void AppendLog(string line)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            AppendLogOnUiThread(line);
            return;
        }

        _ = Dispatcher.UIThread.InvokeAsync(() => AppendLogOnUiThread(line));
    }

    private void ClearLog()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _logBuilder.Clear();
            LogText = string.Empty;
            return;
        }

        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            _logBuilder.Clear();
            LogText = string.Empty;
        });
    }

    private void AppendLogOnUiThread(string line)
    {
        _logBuilder.AppendLine(line);
        LogText = _logBuilder.ToString();
    }

    private static string FindDefaultQQFilePath()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Tencent\QQ") is { } qqRegistry &&
                    qqRegistry.GetValue("Install") is string qqInstallDirectory)
                {
                    paths.Add(Path.Combine(qqInstallDirectory, "Bin", "QQ.exe"));
                    paths.Add(Path.Combine(qqInstallDirectory, "QQ.exe"));
                }
            }
            catch
            {
            }

            paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tencent", "QQ", "Bin", "QQ.exe"));
            paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tencent", "QQ", "Bin", "QQ.exe"));
            paths.Add(@"D:\Program Files (x86)\Tencent\QQ\Bin\QQ.exe");
            paths.Add(@"D:\Program Files\Tencent\QQ\Bin\QQ.exe");
        }

        return paths.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopProcess();
        var cancellation = _dumpCancellation;
        _dumpCancellation = null;
        cancellation?.Dispose();

        var dumper = _dumper;
        _dumper = null;
        dumper?.Dispose();
    }
}
