using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ObservableCollections;
using QQDatabaseExplorer.Controls;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.ViewModels.MessageTab;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels;

public partial class MessageTabViewModel : ViewModelBase
{
    private readonly QQDatabaseService _qqDatabaseService;
    private readonly AppSettingsService _appSettingsService;
    private readonly IDialogService _dialogService;
    private readonly ObservableList<AvaQQGroup> _groups = new();
    private readonly ObservableList<AvaQQGroup> _filteredGroups = new();
    private readonly MessageDatabaseQueryRunner _messageDatabaseQueryRunner = new();
    private readonly MessageSelectionState _messageSelection;
    private readonly ConversationListState _conversationList;
    private readonly MessageFilterState _messageFilterState = new();
    private readonly MessageTimelineFacade _messageTimelineFacade;
    private readonly MessageFilterOptionProvider _messageFilterOptionProvider;
    private readonly MessageJumpTargetResolver _jumpTargetResolver;
    private readonly MessageDatabaseAvailability _databaseAvailability;
    private readonly MessageSenderCache _senderCache;
    private readonly MessageParticipantResolver _participantResolver;
    private readonly MessageConversationListApplier _conversationApplier;
    private readonly MessageDatabaseChangeCoordinator _databaseChangeCoordinator;
    private readonly ReplyTargetConversationResolver _replyTargetConversationResolver;
    private readonly PCQQDisplayMessageFactory _pcqqDisplayMessageFactory;
    private readonly AndroidMobileQQDisplayMessageFactory _androidMobileQQDisplayMessageFactory;
    private readonly IcalinguaDisplayMessageFactory _icalinguaDisplayMessageFactory;
    private readonly MessagePageDisplayBuilder _messagePageDisplayBuilder;
    private readonly MessagePageLoader _messagePageLoader;
    private readonly MessageReloadRunner _messageReloadRunner;
    private readonly MessageAppendLoadRunner _messageAppendLoadRunner;
    private readonly QqNtDisplayMessageFactory _qqNtDisplayMessageFactory;
    private readonly LatestMessagePreviewFactory _latestMessagePreviewFactory;
    private readonly MentionUinResolver _mentionUinResolver;
    private readonly ChatExportService _chatExportService;

    public ViewModelToken ViewModelToken { get; } = new();

    /// <summary>
    /// 当前在聊天栏中显示的消息
    /// </summary>
    private readonly ObservableRingBuffer<AvaQQMessage> _messages = new();

    public NotifyCollectionChangedSynchronizedViewList<AvaQQGroup> Groups { get; }
    public NotifyCollectionChangedSynchronizedViewList<AvaQQMessage> Messages { get; }

    public Views.MessageTab? View { get; set; }

    [ObservableProperty]
    public partial AvaQQGroup? SelectedGroup { get; set; }

    [ObservableProperty]
    public partial string GroupSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoadingPrevious { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingNext { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingInitialMessages { get; set; }

    [ObservableProperty]
    public partial bool IsExportingConversation { get; set; }

    [ObservableProperty]
    public partial bool IsMessageMultiSelectMode { get; set; }

    [ObservableProperty]
    public partial int SelectedMessageCount { get; set; }

    [ObservableProperty]
    public partial MessageFilterCriteria MessageFilter { get; set; } = MessageFilterCriteria.Empty;

    public IReadOnlyList<AvaQQGroup> SelectedGroups => _conversationList.SelectedGroups;

    public IReadOnlyList<AvaQQMessage> SelectedMessages => _messageSelection.SelectedMessages;

    public bool HasMessageFilter => !MessageFilter.IsEmpty;

    public string MessageFilterSummary => MessageFilterState.FormatSummary(MessageFilter);

    private const int PageSize = 50;
    private const int ExportPageSize = 200;
    private const int ExportReplyLookupLimit = 200;
    private const int JumpContextPageSize = 30;
    private readonly MessageLoadVersionTracker _messageLoadVersion = new();
    private readonly MessageWindowLoadState _messageWindowState = new();

    public bool AlwaysShowMessageTime => _appSettingsService.AlwaysShowMessageTime;

    public bool HighlightMentions => _appSettingsService.HighlightMentions;

    public void ScrollToBottom()
    {
        View?.ScrollToBottom();
    }

    public void UpdateVoicePlaybackState(string? currentPlayingPath)
    {
        VoicePlaybackMessageStateUpdater.Update(_messages, currentPlayingPath);
    }

    public bool HasNewerMessages => _messageWindowState.HasNewerMessages;

    public bool HasOlderMessages => _messageWindowState.HasOlderMessages;

    public bool HasSelectedConversation => SelectedGroup is not null;

    public bool CanExportConversation(AvaQQGroup? conversation)
    {
        return conversation is not null &&
               !IsExportingConversation &&
               IsValidConversation(conversation) &&
               _databaseAvailability.HasMessageDatabase(conversation);
    }

    public async Task<ChatExportResult?> ExportConversationAsync(
        AvaQQGroup conversation,
        string parentDirectory,
        ChatExportOptions options,
        IProgress<ChatExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!CanExportConversation(conversation))
            return null;

        try
        {
            IsExportingConversation = true;
            var exportSources = new List<ChatExportSource>();
            var sourceConversations = GetExportConversations(conversation);
            var sourceIndex = 0;
            foreach (var sourceConversation in sourceConversations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sourceIndex++;
                progress?.Report(new ChatExportProgress(
                    "读取消息",
                    $"{sourceConversation.DisplayName} ({sourceIndex}/{sourceConversations.Count})",
                    sourceIndex - 1,
                    sourceConversations.Count));
                var page = await _messageDatabaseQueryRunner.RunAsync(() =>
                    _messagePageLoader.CreatePage(
                        sourceConversation,
                        _messageTimelineFacade.LoadAllMessages(sourceConversation, ExportPageSize),
                        ExportReplyLookupLimit));

                var avaMessages = await _messagePageDisplayBuilder.CreateAsync(sourceConversation, page);
                exportSources.Add(new ChatExportSource(sourceConversation, avaMessages));
            }

            return await Task.Run(
                async () => await _chatExportService.ExportAsync(
                    conversation,
                    exportSources,
                    parentDirectory,
                    options,
                    progress,
                    cancellationToken),
                cancellationToken);
        }
        finally
        {
            IsExportingConversation = false;
        }
    }

    public async Task ReturnToLatestMessagesAsync()
    {
        if (SelectedGroup is null)
            return;

        if (!HasNewerMessages)
        {
            View?.ScrollToBottomFast();
            return;
        }

        var loadVersion = _messageLoadVersion.BeginNext();
        await LoadInitialMessagesAsync(SelectedGroup, loadVersion);
    }

    public async Task ReturnToEarliestMessagesAsync()
    {
        if (SelectedGroup is null)
            return;

        var loadVersion = _messageLoadVersion.BeginNext();
        await LoadEarliestMessagesAsync(SelectedGroup, loadVersion);
    }

    public void ScrollToMessage(long messageId)
    {
        View?.ScrollToMessage(messageId);
    }

    partial void OnSelectedGroupChanged(AvaQQGroup? value)
    {
        OnPropertyChanged(nameof(HasSelectedConversation));
        HandleSelectedGroupChanged(value);
    }

    partial void OnGroupSearchTextChanged(string value)
    {
        RefreshFilteredGroups();
    }

    partial void OnMessageFilterChanged(MessageFilterCriteria value)
    {
        OnPropertyChanged(nameof(HasMessageFilter));
        OnPropertyChanged(nameof(MessageFilterSummary));
    }

    public void ActivateGroup(AvaQQGroup? group, bool updateRangeAnchor = true)
    {
        if (!_conversationList.SetRangeAnchor(group, updateRangeAnchor))
            return;

        SelectedGroup = group;
    }

    public void SelectSingleGroup(AvaQQGroup? group)
    {
        if (!_conversationList.SelectOnly(group))
            return;

        OnPropertyChanged(nameof(SelectedGroups));
        ActivateGroup(group);
    }

    public void ToggleGroupSelection(AvaQQGroup? group)
    {
        if (!_conversationList.Toggle(group))
            return;

        OnPropertyChanged(nameof(SelectedGroups));
        ActivateGroup(group, updateRangeAnchor: false);
    }

    public void SelectGroupRange(AvaQQGroup? group, bool preserveSelection)
    {
        if (!_conversationList.SelectRange(group, preserveSelection))
            return;

        OnPropertyChanged(nameof(SelectedGroups));
        ActivateGroup(group);
    }

    public void SetSelectedMessages(IEnumerable<AvaQQMessage> selectedMessages)
    {
        UpdateMessageSelectionState(_messageSelection.SetSelectedMessages(selectedMessages));
    }

    public void AddSelectedMessages(IEnumerable<AvaQQMessage> selectedMessages)
    {
        UpdateMessageSelectionState(_messageSelection.AddSelectedMessages(selectedMessages));
    }

    public void ToggleMessageSelection(AvaQQMessage message)
    {
        UpdateMessageSelectionState(_messageSelection.ToggleMessageSelection(message));
    }

    public void ClearMessageSelection()
    {
        UpdateMessageSelectionState(_messageSelection.ClearMessageSelection());
    }

    public async Task OpenMessageFilterDialogAsync()
    {
        if (SelectedGroup is not { } conversation)
            return;

        var request = _messageFilterOptionProvider.CreateDialogRequest(
            conversation,
            MessageFilter,
            _senderCache.GetSenderFilterCandidates(conversation));
        var filter = await _dialogService.ShowMessageFilterDialog(request, ViewModelToken);
        if (filter is null)
            return;

        await ApplyMessageFilterAsync(conversation, filter);
    }

    public async Task ClearMessageFilterAsync()
    {
        if (SelectedGroup is not { } conversation)
            return;

        await ApplyMessageFilterAsync(conversation, MessageFilterCriteria.Empty);
    }

    public string ResolveMentionUin(uint groupId, string? ntUid)
    {
        return _mentionUinResolver.Resolve(groupId, ntUid);
    }

    private void OnDatabaseAdded(IQQDatabase database)
    {
        _databaseChangeCoordinator.HandleDatabaseAdded(database);
    }

    private async Task OnDatabaseAddedAsync(IQQDatabase database)
    {
        await _databaseChangeCoordinator.HandleDatabaseAddedAsync(database);
    }

    private void OnAppSettingsChanged(object? sender, EventArgs e)
    {
        MessageAppSettingsApplier.Apply(_messages, AlwaysShowMessageTime, HighlightMentions);
    }

    private void OnDatabaseRemoved(IQQDatabase database)
    {
        _databaseChangeCoordinator.HandleDatabaseRemoved(database);
    }

    private void ApplyGroupSelectionState()
    {
        _conversationList.ApplyGroupSelectionState();
        OnPropertyChanged(nameof(SelectedGroups));
    }

    private void ClearGroupSelectionState()
    {
        _conversationList.Clear();
        _senderCache.Clear();
        SelectedGroup = null;
        OnPropertyChanged(nameof(SelectedGroups));
    }

    private void HandleSelectedGroupChanged(AvaQQGroup? value)
    {
        if (value is not null && value.ConversationKey == _conversationList.ActiveConversationKey)
            return;

        if (value is not null)
        {
            MessageFilter = _messageFilterState.Get(value);
        }

        if (_conversationList.SetActiveGroup(value))
            OnPropertyChanged(nameof(SelectedGroups));

        var loadVersion = _messageLoadVersion.BeginNext();
        if (value is null)
        {
            MessageFilter = MessageFilterCriteria.Empty;
            ClearMessageSelection();
            _messages.Clear();
            ResetMessageWindowState();
            View?.ShowMessagesImmediately();
        }
        else
        {
            _ = LoadInitialMessagesAsync(value, loadVersion);
        }
    }

    private void RefreshFilteredGroups()
    {
        _conversationList.RefreshFilteredGroups(GroupSearchText);
    }

    private static bool IsValidConversation(AvaQQGroup group)
    {
        return ConversationListState.IsValidConversation(group);
    }

    private IReadOnlyList<AvaQQGroup> GetExportConversations(AvaQQGroup conversation)
    {
        var relatedConversations = _groups
            .Where(IsValidConversation)
            .Where(candidate => ChatExportConversationMatcher.IsSameLogicalConversation(candidate, conversation))
            .Where(_databaseAvailability.HasMessageDatabase)
            .OrderByDescending(static candidate => candidate.LatestMessageTime)
            .ThenBy(static candidate => candidate.ConversationType)
            .ToArray();

        return relatedConversations.Length == 0
            ? [conversation]
            : relatedConversations;
    }

    private void UpdateMessageSelectionState(int selectedCount)
    {
        SelectedMessageCount = selectedCount;
        IsMessageMultiSelectMode = selectedCount > 0;
        OnPropertyChanged(nameof(SelectedMessages));
    }

    private async Task ApplyMessageFilterAsync(AvaQQGroup conversation, MessageFilterCriteria filter)
    {
        _messageFilterState.Set(conversation, filter);
        MessageFilter = filter;
        var loadVersion = _messageLoadVersion.BeginNext();
        await LoadInitialMessagesAsync(conversation, loadVersion);
    }

    private MessageFilterCriteria GetCurrentMessageFilter(AvaQQGroup conversation)
    {
        return _messageFilterState.Get(conversation);
    }

    private MessageSenderInfo CreateMessageSenderInfo(
        MessageRecord message,
        AvaQQGroup conversation,
        IDictionary<uint, string> senderNames)
    {
        return new MessageSenderInfo(
            message.SenderId,
            message.SenderUid,
            _participantResolver.ResolveReplyTargetSenderName(message, conversation, senderNames));
    }

    /// <summary>
    /// 从数据库消息创建 AvaQQMessage。
    /// </summary>
    private AvaQQMessage CreateAvaQQMessage(
        MessageRecord item,
        AvaQQGroup conversation,
        IReadOnlyDictionary<ReplyTargetKey, MessageRecord>? replyTargetMessages = null)
    {
        if (ConversationTypeClassifier.IsPCQQ(conversation))
        {
            return _pcqqDisplayMessageFactory.Create(item, conversation);
        }

        if (ConversationTypeClassifier.IsAndroidMobileQQ(conversation))
        {
            return _androidMobileQQDisplayMessageFactory.Create(item, conversation);
        }

        if (ConversationTypeClassifier.IsIcalingua(conversation))
        {
            return _icalinguaDisplayMessageFactory.Create(item, conversation);
        }

        return _qqNtDisplayMessageFactory.Create(
            item,
            conversation,
            replyTargetMessages ?? MessagePage.EmptyReplyTargetMessages);
    }

}

internal sealed class MessageDatabaseQueryRunner
{
    private readonly SemaphoreSlim _queryLock = new(1, 1);

    public async Task<T> RunAsync<T>(Func<T> query)
    {
        await _queryLock.WaitAsync();
        try
        {
            return await Task.Run(query);
        }
        finally
        {
            _queryLock.Release();
        }
    }
}

internal sealed class MessageLoadVersionTracker
{
    private int _currentVersion;

    public int Current => _currentVersion;

    public int BeginNext()
    {
        return ++_currentVersion;
    }

    public bool IsCurrent(AvaQQGroup? selectedConversation, AvaQQGroup conversation, int loadVersion)
    {
        return loadVersion == _currentVersion &&
               selectedConversation?.ConversationKey == conversation.ConversationKey;
    }

    public bool IsCurrentVersion(int loadVersion)
    {
        return loadVersion == _currentVersion;
    }
}

internal sealed class MessageWindowLoadState
{
    public bool HasOlderMessages { get; private set; }

    public bool HasNewerMessages { get; private set; }

    public void Reset()
    {
        HasOlderMessages = false;
        HasNewerMessages = false;
    }

    public void Update(
        IReadOnlyCollection<MessageRecord> messages,
        bool olderAvailable,
        bool newerAvailable)
    {
        HasOlderMessages = messages.Count > 0 && olderAvailable;
        HasNewerMessages = messages.Count > 0 && newerAvailable;
    }

    public void UpdateOlderAvailability(int loadedCount, int pageSize)
    {
        HasOlderMessages = loadedCount == pageSize;
    }

    public void UpdateNewerAvailability(int loadedCount, int pageSize)
    {
        HasNewerMessages = loadedCount == pageSize;
    }
}

internal static class MessageAppSettingsApplier
{
    public static void Apply(
        IEnumerable<AvaQQMessage> messages,
        bool alwaysShowMessageTime,
        bool highlightMentions)
    {
        foreach (var message in messages)
        {
            message.IsHoverTimeVisible = alwaysShowMessageTime;
            message.HighlightMentions = highlightMentions;
        }
    }
}

internal sealed class MentionUinResolver
{
    private readonly MessageParticipantResolver _participantResolver;

    public MentionUinResolver(MessageParticipantResolver participantResolver)
    {
        _participantResolver = participantResolver;
    }

    public string Resolve(uint groupId, string? ntUid)
    {
        if (IsNumericString(ntUid))
            return ntUid!;

        return _participantResolver.ResolveSystemHintSourceUin(groupId, ntUid);
    }

    private static bool IsNumericString(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.All(char.IsAsciiDigit);
    }
}
