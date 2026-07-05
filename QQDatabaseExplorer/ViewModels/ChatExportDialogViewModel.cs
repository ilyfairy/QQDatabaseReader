using System;
using System.Collections.Generic;
using System.Linq;
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
    private CancellationTokenSource? _exportCancellationTokenSource;

    public ChatExportDialogViewModel(MessageTabViewModel messageTabViewModel)
    {
        _messageTabViewModel = messageTabViewModel;
    }

    public ViewModelToken ViewModelToken { get; } = new();

    public AvaQQGroup? Conversation { get; private set; }

    public IReadOnlyList<AvaQQGroup> Conversations { get; private set; } = [];

    public ChatExportResult? Result { get; private set; }

    public string? ResultDirectory { get; private set; }

    [ObservableProperty]
    public partial string OutputDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ChatExportFormat SelectedFormat { get; set; } = ChatExportFormat.Html;

    [ObservableProperty]
    public partial ChatExportMode SelectedMode { get; set; } = ChatExportMode.Separate;

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

    public string ConversationSummary => Conversations.Count switch
    {
        0 => string.Empty,
        1 => Conversations[0].DisplayName,
        _ => $"已选择 {Conversations.Count} 个聊天",
    };

    public bool CanStartExport => Conversations.Count > 0 &&
                                  !IsExporting &&
                                  !string.IsNullOrWhiteSpace(OutputDirectory);

    public Dictionary<ChatExportFormat, string> ExportFormats { get; } = new()
    {
        { ChatExportFormat.Html, "HTML 文件夹" },
        { ChatExportFormat.Mhtml, "MHTML 单文件" },
        { ChatExportFormat.Markdown, "Markdown" },
        { ChatExportFormat.Json, "仅 JSON 数据" },
    };

    public Dictionary<ChatExportMode, string> ExportModes { get; } = new()
    {
        { ChatExportMode.Separate, "独立导出" },
        { ChatExportMode.Merged, "合并导出" },
    };

    public bool ShowExportMode => Conversations.Count > 1;

    public string FormatDescription => SelectedFormat switch
    {
        ChatExportFormat.Html => "HTML 文件夹: index.html + resources",
        ChatExportFormat.Mhtml => "MHTML 单文件: index.mhtml",
        ChatExportFormat.Markdown => "Markdown: chat.md + resources",
        ChatExportFormat.Json => "仅 JSON 数据: chat.json",
        _ => string.Empty,
    };

    public bool ShowContentOptions => SelectedFormat is ChatExportFormat.Html or ChatExportFormat.Mhtml or ChatExportFormat.Markdown;

    public bool HasResult => Result is not null;

    public bool HasCompletedText => !string.IsNullOrWhiteSpace(CompletedText);

    public void Initialize(AvaQQGroup conversation)
    {
        Initialize([conversation]);
    }

    public void Initialize(IReadOnlyList<AvaQQGroup> conversations)
    {
        Conversations = conversations.Where(_messageTabViewModel.CanExportConversation).ToArray();
        Conversation = Conversations.FirstOrDefault();
        if (Conversations.Count <= 1)
            SelectedMode = ChatExportMode.Separate;
        ProgressText = string.Empty;
        CompletedText = string.Empty;
        Result = null;
        ResultDirectory = null;
        OnPropertyChanged(nameof(ConversationSummary));
        OnPropertyChanged(nameof(ShowExportMode));
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
        if (Conversations.Count == 0 || string.IsNullOrWhiteSpace(OutputDirectory) || IsExporting)
            return;

        _exportCancellationTokenSource?.Cancel();
        _exportCancellationTokenSource?.Dispose();
        var exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _exportCancellationTokenSource = exportCancellation;

        IsExporting = true;
        CompletedText = string.Empty;
        Result = null;
        ResultDirectory = null;
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
                    IncludeFaceAssets),
                Conversations.Count > 1 ? SelectedMode : ChatExportMode.Separate);

            var exportedCount = 0;
            var messageCount = 0;
            if (options.Mode == ChatExportMode.Merged)
            {
                var result = await _messageTabViewModel.ExportMergedConversationsAsync(
                    Conversations,
                    OutputDirectory,
                    options,
                    progress,
                    exportCancellation.Token);

                if (result is not null)
                {
                    Result = result;
                    exportedCount = 1;
                    messageCount = result.MessageCount;
                }
            }
            else
            {
                for (var i = 0; i < Conversations.Count; i++)
                {
                    var conversation = Conversations[i];
                    var conversationProgress = new Progress<ChatExportProgress>(item =>
                        UpdateProgress(PrefixProgress(item, conversation, i, Conversations.Count)));
                    var result = await _messageTabViewModel.ExportConversationAsync(
                        conversation,
                        OutputDirectory,
                        options,
                        conversationProgress,
                        exportCancellation.Token);

                    if (result is null)
                        continue;

                    Result = result;
                    exportedCount++;
                    messageCount += result.MessageCount;
                }
            }

            ResultDirectory = exportedCount > 1 ? OutputDirectory : Result?.ExportDirectory;
            OnPropertyChanged(nameof(HasResult));
            CompletedText = exportedCount == 0
                ? string.Empty
                : options.Mode == ChatExportMode.Merged
                    ? $"已合并导出 {Conversations.Count} 个聊天, 共 {messageCount} 条消息"
                    : Conversations.Count == 1
                    ? $"已导出 {messageCount} 条消息"
                    : $"已导出 {exportedCount} 个聊天, 共 {messageCount} 条消息";
        }
        catch (OperationCanceledException) when (exportCancellation.IsCancellationRequested)
        {
            Result = null;
            ResultDirectory = null;
            OnPropertyChanged(nameof(HasResult));
            ProgressText = "导出已取消";
            ProgressCountText = string.Empty;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            CompletedText = "导出已取消";
        }
        finally
        {
            if (ReferenceEquals(_exportCancellationTokenSource, exportCancellation))
                _exportCancellationTokenSource = null;

            exportCancellation.Dispose();
            IsExporting = false;
        }
    }

    public void CancelExport()
    {
        if (_exportCancellationTokenSource is { IsCancellationRequested: false } cancellation)
            cancellation.Cancel();
    }

    private static ChatExportProgress PrefixProgress(
        ChatExportProgress progress,
        AvaQQGroup conversation,
        int index,
        int total)
    {
        if (total <= 1)
            return progress;

        var detail = string.IsNullOrWhiteSpace(progress.Detail) ||
                     progress.Detail.StartsWith(conversation.DisplayName, StringComparison.CurrentCulture)
            ? progress.Detail
            : $"{conversation.DisplayName}: {progress.Detail}";
        return progress with
        {
            Stage = $"{progress.Stage} ({index + 1}/{total})",
            Detail = detail,
        };
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
