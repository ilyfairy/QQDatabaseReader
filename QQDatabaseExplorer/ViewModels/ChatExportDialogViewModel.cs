using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;

namespace QQDatabaseExplorer.ViewModels;

public partial class ChatExportDialogViewModel : ViewModelBase
{
    private readonly MessageTabViewModel _messageTabViewModel;

    public ChatExportDialogViewModel(MessageTabViewModel messageTabViewModel)
    {
        _messageTabViewModel = messageTabViewModel;
    }

    public ViewModelToken ViewModelToken { get; } = new();

    public AvaQQGroup? Conversation { get; private set; }

    public ChatExportResult? Result { get; private set; }

    [ObservableProperty]
    public partial string OutputDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ChatExportFormat SelectedFormat { get; set; } = ChatExportFormat.Html;

    [ObservableProperty]
    public partial bool IncludeAvatars { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeImages { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeVoice { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeVideos { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeFiles { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeFaceAssets { get; set; } = true;

    [ObservableProperty]
    public partial bool IsExporting { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProgressCountText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; } = true;

    [ObservableProperty]
    public partial string CompletedText { get; set; } = string.Empty;

    public bool CanStartExport => Conversation is not null &&
                                  !IsExporting &&
                                  !string.IsNullOrWhiteSpace(OutputDirectory);

    public Dictionary<ChatExportFormat, string> ExportFormats { get; } = new()
    {
        { ChatExportFormat.Html, "HTML 文件夹" },
        { ChatExportFormat.Mhtml, "MHTML 单文件" },
        { ChatExportFormat.Json, "仅 JSON 数据" },
    };

    public string FormatDescription => SelectedFormat switch
    {
        ChatExportFormat.Html => "HTML 文件夹: index.html + resources",
        ChatExportFormat.Mhtml => "MHTML 单文件: index.mhtml",
        ChatExportFormat.Json => "仅 JSON 数据: chat.json",
        _ => string.Empty,
    };

    public bool ShowContentOptions => SelectedFormat is ChatExportFormat.Html or ChatExportFormat.Mhtml;

    public bool HasResult => Result is not null;

    public bool HasCompletedText => !string.IsNullOrWhiteSpace(CompletedText);

    public void Initialize(AvaQQGroup conversation)
    {
        Conversation = conversation;
        ProgressText = string.Empty;
        CompletedText = string.Empty;
        Result = null;
        OnPropertyChanged(nameof(CanStartExport));
    }

    partial void OnOutputDirectoryChanged(string value)
    {
        OnPropertyChanged(nameof(CanStartExport));
    }

    partial void OnSelectedFormatChanged(ChatExportFormat value)
    {
        OnPropertyChanged(nameof(FormatDescription));
        OnPropertyChanged(nameof(ShowContentOptions));
    }

    partial void OnIsExportingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartExport));
    }

    partial void OnCompletedTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasCompletedText));
    }

    [RelayCommand]
    public async Task StartExportAsync(CancellationToken cancellationToken = default)
    {
        if (Conversation is null || string.IsNullOrWhiteSpace(OutputDirectory) || IsExporting)
            return;

        IsExporting = true;
        CompletedText = string.Empty;
        Result = null;
        OnPropertyChanged(nameof(HasResult));
        ProgressValue = 0;
        ProgressCountText = string.Empty;
        IsProgressIndeterminate = true;
        ProgressText = "正在准备导出...";

        try
        {
            var progress = new Progress<ChatExportProgress>(UpdateProgress);
            var options = new ChatExportOptions(
                SelectedFormat,
                new ChatExportContentOptions(
                    IncludeAvatars,
                    IncludeImages,
                    IncludeVoice,
                    IncludeVideos,
                    IncludeFiles,
                    IncludeFaceAssets));

            Result = await _messageTabViewModel.ExportConversationAsync(
                Conversation,
                OutputDirectory,
                options,
                progress,
                cancellationToken);
            OnPropertyChanged(nameof(HasResult));
            CompletedText = Result is null
                ? string.Empty
                : $"已导出 {Result.MessageCount} 条消息";
        }
        finally
        {
            IsExporting = false;
        }
    }

    private void UpdateProgress(ChatExportProgress progress)
    {
        ProgressText = string.IsNullOrWhiteSpace(progress.Detail)
            ? progress.Stage
            : $"{progress.Stage}: {progress.Detail}";

        if (progress.Total <= 0)
        {
            IsProgressIndeterminate = true;
            ProgressValue = 0;
            ProgressCountText = string.Empty;
            return;
        }

        var current = Math.Clamp(progress.Current, 0, progress.Total);
        IsProgressIndeterminate = false;
        ProgressValue = current * 100d / progress.Total;
        ProgressCountText = $"{current}/{progress.Total}";
    }
}
