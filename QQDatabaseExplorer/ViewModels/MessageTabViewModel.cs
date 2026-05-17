using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ObservableCollections;
using QQDatabaseExplorer.Controls;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels;

public partial class MessageTabViewModel : ViewModelBase
{
    private const long AndroidMobileQQImageCacheCrc64Polynomial = -7661587058870466123L;
    private static readonly Lazy<long[]> AndroidMobileQQImageCacheCrc64Table = new(CreateAndroidMobileQQImageCacheCrc64Table);

    private static readonly Regex UrlRegex = new(
        @"(?<url>(?:https?://|www\.)[^\s<>()""']+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly QQDatabaseService _qqDatabaseService;
    private readonly AppSettingsService _appSettingsService;
    private readonly IDialogService _dialogService;
    private readonly ObservableList<AvaQQGroup> _groups = new();
    private readonly ObservableList<AvaQQGroup> _filteredGroups = new();
    private readonly HashSet<string> _selectedConversationKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<uint, string>> _conversationSenderNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<long, MessageSenderInfo>> _conversationMessageSenderInfos = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MessageFilterCriteria> _conversationMessageFilters = new(StringComparer.Ordinal);
    private ProfileInfoNameCache? _profileInfoNames;

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
    public partial bool IsMessageMultiSelectMode { get; set; }

    [ObservableProperty]
    public partial int SelectedMessageCount { get; set; }

    [ObservableProperty]
    public partial MessageFilterCriteria MessageFilter { get; set; } = MessageFilterCriteria.Empty;

    private const int PageSize = 50;
    private const int JumpContextPageSize = 30;
    private int _initialMessageLoadVersion;
    private string? _activeConversationKey;
    private string? _conversationRangeAnchorKey;
    private bool _hasOlderMessages;
    private bool _hasNewerMessages;

    public MessageTabViewModel(
        QQDatabaseService qqDatabaseService,
        AppSettingsService appSettingsService,
        IDialogService dialogService)
    {
        Groups = _filteredGroups.ToNotifyCollectionChanged();
        Messages = _messages.ToNotifyCollectionChanged();
        _qqDatabaseService = qqDatabaseService;
        _appSettingsService = appSettingsService;
        _dialogService = dialogService;
        
        _qqDatabaseService.DatabaseAdded += OnDatabaseAdded;
        _qqDatabaseService.DatabaseRemoved += OnDatabaseRemoved;
        _appSettingsService.SettingsChanged += OnAppSettingsChanged;
    }

    public bool AlwaysShowMessageTime => _appSettingsService.AlwaysShowMessageTime;

    public void ScrollToBottom()
    {
        View?.ScrollToBottom();
    }

    public bool HasNewerMessages => _hasNewerMessages;

    public bool HasOlderMessages => _hasOlderMessages;

    public bool HasSelectedConversation => SelectedGroup is not null;

    public bool HasMessageFilter => !MessageFilter.IsEmpty;

    public string MessageFilterSummary
    {
        get
        {
            if (MessageFilter.IsEmpty)
                return string.Empty;

            var parts = new List<string>();
            if (MessageFilter.SelectedDayStartTimes.Count > 0)
            {
                var dates = MessageFilter.SelectedDayStartTimes
                    .Select(dayStartTime => DateTimeOffset.FromUnixTimeSeconds(dayStartTime).LocalDateTime.ToString("yyyy-MM-dd"))
                    .ToArray();
                parts.Add(dates.Length <= 3
                    ? string.Join("、", dates)
                    : $"{dates[0]} 等 {dates.Length} 天");
            }
            else if (MessageFilter.StartTime is not null ||
                MessageFilter.EndTimeExclusive is not null)
            {
                var startText = MessageFilter.StartTime is null
                    ? "最早"
                    : DateTimeOffset.FromUnixTimeSeconds(MessageFilter.StartTime.Value).LocalDateTime.ToString("yyyy-MM-dd");
                var endText = MessageFilter.EndTimeExclusive is null
                    ? "最新"
                    : DateTimeOffset.FromUnixTimeSeconds(MessageFilter.EndTimeExclusive.Value).LocalDateTime.AddDays(-1).ToString("yyyy-MM-dd");
                parts.Add($"{startText} 至 {endText}");
            }

            if (MessageFilter.SenderIds.Count > 0)
            {
                parts.Add($"发送人 {MessageFilter.SenderIds.Count} 个");
            }

            return string.Join("，", parts);
        }
    }

    partial void OnMessageFilterChanged(MessageFilterCriteria value)
    {
        OnPropertyChanged(nameof(HasMessageFilter));
        OnPropertyChanged(nameof(MessageFilterSummary));
    }

    partial void OnSelectedGroupChanged(AvaQQGroup? value)
    {
        OnPropertyChanged(nameof(HasSelectedConversation));
        HandleSelectedGroupChanged(value);
    }

    private bool HasNtMessageDatabase =>
        _qqDatabaseService.MessageDatabase is not null ||
        _qqDatabaseService.AndroidMessageDatabase is not null;

    public async Task ReturnToLatestMessagesAsync()
    {
        if (SelectedGroup is null)
            return;

        if (!_hasNewerMessages)
        {
            View?.ScrollToBottomFast();
            return;
        }

        var loadVersion = ++_initialMessageLoadVersion;
        await LoadInitialMessagesAsync(SelectedGroup, loadVersion);
    }

    public async Task ReturnToEarliestMessagesAsync()
    {
        if (SelectedGroup is null)
            return;

        var loadVersion = ++_initialMessageLoadVersion;
        await LoadEarliestMessagesAsync(SelectedGroup, loadVersion);
    }

    public async Task OpenMessageFilterDialogAsync()
    {
        if (SelectedGroup is not { } conversation)
            return;

        var request = new MessageFilterDialogRequest(
            conversation.ConversationType,
            MessageFilter,
            LoadMessageDateFilterOptions(conversation),
            LoadSenderFilterCandidates(conversation));
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

    private async Task ApplyMessageFilterAsync(AvaQQGroup conversation, MessageFilterCriteria filter)
    {
        if (filter.IsEmpty)
            _conversationMessageFilters.Remove(conversation.ConversationKey);
        else
            _conversationMessageFilters[conversation.ConversationKey] = filter;

        MessageFilter = filter;
        var loadVersion = ++_initialMessageLoadVersion;
        await LoadInitialMessagesAsync(conversation, loadVersion);
    }

    public void ScrollToMessage(long messageId)
    {
        View?.ScrollToMessage(messageId);
    }

    public async Task JumpToReplyMessageAsync(AvaQQMessage sourceMessage)
    {
        if (sourceMessage.Reply is not { } reply)
            return;

        var targetConversation = ResolveReplyTargetConversation(sourceMessage, reply);
        if (targetConversation is not null &&
            targetConversation.ConversationKey != sourceMessage.ConversationKey)
        {
            await JumpToReplyMessageInConversationAsync(targetConversation, reply);
            return;
        }

        if (TryFindLoadedReplyTarget(sourceMessage.ConversationKey, sourceMessage.ConversationType, reply, out var loadedMessage))
        {
            View?.ScrollToMessageIfNeeded(loadedMessage.MessageId);
            return;
        }

        if (sourceMessage.ConversationKey == _activeConversationKey &&
            SelectedGroup?.ConversationKey == sourceMessage.ConversationKey)
        {
            await JumpToMessageInActiveConversationAsync(sourceMessage, reply);
            return;
        }

        if (targetConversation is not null)
        {
            await JumpToReplyMessageInConversationAsync(targetConversation, reply);
            return;
        }

        if (sourceMessage.ConversationType == AvaConversationType.Group)
        {
            await JumpToMessageAsync(
                sourceMessage.GroupId,
                reply.MessageId,
                reply.MessageSeq,
                SelectedGroup?.GroupName);
        }
    }

    private AvaQQGroup? ResolveReplyTargetConversation(AvaQQMessage sourceMessage, AvaReplyMessage reply)
    {
        if (reply.SourceGroupId == 0 ||
            reply.SourceGroupId == sourceMessage.GroupId)
        {
            return SelectedGroup?.ConversationKey == sourceMessage.ConversationKey
                ? SelectedGroup
                : _groups.FirstOrDefault(group => group.ConversationKey == sourceMessage.ConversationKey);
        }

        var group = GetOrCreateGroup(reply.SourceGroupId);
        if (!string.IsNullOrWhiteSpace(reply.SourceGroupName) &&
            string.IsNullOrWhiteSpace(group.GroupName))
        {
            group.GroupName = reply.SourceGroupName;
        }

        return group;
    }

    private async Task JumpToReplyMessageInConversationAsync(AvaQQGroup conversation, AvaReplyMessage reply)
    {
        if (!HasNtMessageDatabase)
        {
            return;
        }

        long messageId;
        long messageSeq;
        switch (conversation.ConversationType)
        {
            case AvaConversationType.Group:
            {
                var targetMessage = ResolveJumpTarget(
                    conversation.GroupId,
                    GetReplyMessageIdCandidates(reply),
                    GetReplyMessageRandomCandidates(reply),
                    GetReplyMessageSeqCandidates(reply, conversation.ConversationType));
                if (targetMessage is null)
                    return;

                messageId = targetMessage.MessageId;
                messageSeq = targetMessage.MessageSeq;
                break;
            }
            case AvaConversationType.Private:
            {
                var targetMessage = ResolvePrivateJumpTarget(
                    conversation.PrivateConversationId,
                    GetReplyMessageIdCandidates(reply),
                    GetReplyMessageRandomCandidates(reply),
                    GetReplyMessageSeqCandidates(reply, conversation.ConversationType));
                if (targetMessage is null)
                    return;

                messageId = targetMessage.MessageId;
                messageSeq = targetMessage.MessageSeq;
                break;
            }
            default:
                return;
        }

        _selectedConversationKeys.Clear();
        _selectedConversationKeys.Add(conversation.ConversationKey);
        ApplyGroupSelectionState();
        ActivateGroup(conversation);

        var loadVersion = ++_initialMessageLoadVersion;
        if (View is not null)
            await View.ScrollToGroupAsync(conversation);

        await LoadMessagesAroundAsync(
            conversation,
            messageId,
            messageSeq,
            loadVersion);
    }

    private bool TryFindLoadedReplyTarget(
        string sourceConversationKey,
        AvaConversationType sourceConversationType,
        AvaReplyMessage reply,
        out AvaQQMessage loadedMessage)
    {
        loadedMessage = null!;
        if (string.IsNullOrWhiteSpace(sourceConversationKey) ||
            sourceConversationKey != _activeConversationKey ||
            _messages.Count == 0)
        {
            return false;
        }

        var messageRandoms = GetReplyMessageRandomCandidates(reply);
        var messageSeqs = GetReplyMessageSeqCandidates(reply, sourceConversationType);
        var messageIds = GetReplyMessageIdCandidates(reply);
        foreach (var messageRandom in messageRandoms)
        {
            var randomMessages = _messages
                .Where(message => message.MessageRandom == messageRandom)
                .ToArray();

            loadedMessage = randomMessages
                .FirstOrDefault(message => messageSeqs.Contains(IsPCQQConversation(sourceConversationType)
                                      ? message.PCQQMessageSeq
                                      : message.MessageSeq))!;
            if (loadedMessage is not null)
                return true;

            loadedMessage = randomMessages
                .FirstOrDefault(message => messageIds.Contains(message.MessageId))!;
            if (loadedMessage is not null)
                return true;

            if (randomMessages.Length == 1)
            {
                loadedMessage = randomMessages[0];
                return true;
            }
        }

        if (messageRandoms.Count > 0 && sourceConversationType == AvaConversationType.Group)
            return false;

        foreach (var messageId in messageIds)
        {
            loadedMessage = _messages.FirstOrDefault(message => message.MessageId == messageId)!;
            if (loadedMessage is not null)
                return true;
        }

        foreach (var messageSeq in messageSeqs)
        {
            var loadedMessages = _messages
                .Where(message => IsPCQQConversation(sourceConversationType)
                    ? message.PCQQMessageSeq == messageSeq
                    : message.MessageSeq == messageSeq)
                .Take(2)
                .ToArray();
            if (loadedMessages.Length == 1)
            {
                loadedMessage = loadedMessages[0];
                return true;
            }
        }

        return false;
    }

    private async Task JumpToMessageInActiveConversationAsync(AvaQQMessage sourceMessage, AvaReplyMessage reply)
    {
        if (IsPCQQConversation(sourceMessage.ConversationType))
        {
            await JumpToPCQQMessageInActiveConversationAsync(sourceMessage, reply);
            return;
        }

        if (!HasNtMessageDatabase)
            return;

        switch (sourceMessage.ConversationType)
        {
            case AvaConversationType.Group:
            {
                if (sourceMessage.GroupId == 0)
                    return;

                var targetMessage = ResolveJumpTarget(
                    sourceMessage.GroupId,
                    GetReplyMessageIdCandidates(reply),
                    GetReplyMessageRandomCandidates(reply),
                    GetReplyMessageSeqCandidates(reply, sourceMessage.ConversationType));
                if (targetMessage is null)
                    return;

                var loadVersion = ++_initialMessageLoadVersion;
                await LoadMessagesAroundAsync(SelectedGroup, targetMessage.MessageId, targetMessage.MessageSeq, loadVersion);
                break;
            }
            case AvaConversationType.Private:
            {
                if (sourceMessage.PrivateConversationId == 0)
                    return;

                var targetMessage = ResolvePrivateJumpTarget(
                    sourceMessage.PrivateConversationId,
                    GetReplyMessageIdCandidates(reply),
                    GetReplyMessageRandomCandidates(reply),
                    GetReplyMessageSeqCandidates(reply, sourceMessage.ConversationType));
                if (targetMessage is null)
                    return;

                var loadVersion = ++_initialMessageLoadVersion;
                await LoadMessagesAroundAsync(SelectedGroup, targetMessage.MessageId, targetMessage.MessageSeq, loadVersion);
                break;
            }
        }
    }

    private async Task JumpToPCQQMessageInActiveConversationAsync(AvaQQMessage sourceMessage, AvaReplyMessage reply)
    {
        if (_qqDatabaseService.PCQQMessageDatabase is not { } pcqqDatabase ||
            SelectedGroup is not { } selectedGroup ||
            string.IsNullOrWhiteSpace(selectedGroup.PCQQTableName))
        {
            return;
        }

        PCQQMessageRecord? targetMessage = null;
        foreach (var messageSeq in GetReplyMessageSeqCandidates(reply, sourceMessage.ConversationType))
        {
            targetMessage = pcqqDatabase.LoadMessageBySeq(selectedGroup.PCQQTableName, messageSeq);
            if (targetMessage is not null)
                break;
        }

        if (targetMessage is null)
            return;

        var loadVersion = ++_initialMessageLoadVersion;
        await LoadMessagesAroundAsync(
            selectedGroup,
            targetMessage.MessageRandom,
            targetMessage.MessageTime,
            loadVersion);
    }

    public async Task JumpToMessageAsync(uint groupId, long messageId, long messageSeq, string? groupName = null)
    {
        await JumpToMessageAsync(groupId, messageId, messageSeq, groupName, clearMessageFilter: false);
    }

    public async Task JumpToMessageAndClearFilterAsync(uint groupId, long messageId, long messageSeq, string? groupName = null)
    {
        await JumpToMessageAsync(groupId, messageId, messageSeq, groupName, clearMessageFilter: true);
    }

    private async Task JumpToMessageAsync(
        uint groupId,
        long messageId,
        long messageSeq,
        string? groupName,
        bool clearMessageFilter)
    {
        if (groupId == 0)
            return;

        if (HasNtMessageDatabase)
        {
            var targetMessage = ResolveJumpTarget(groupId, messageId, messageSeq);
            if (targetMessage is null)
                return;

            messageId = targetMessage.MessageId;
            messageSeq = targetMessage.MessageSeq;
        }

        var group = GetOrCreateGroup(groupId);
        if (!string.IsNullOrWhiteSpace(groupName) && string.IsNullOrWhiteSpace(group.GroupName))
        {
            group.GroupName = groupName;
        }

        _selectedConversationKeys.Clear();
        _selectedConversationKeys.Add(group.ConversationKey);
        if (clearMessageFilter)
            ClearMessageFilter(group);
        ApplyGroupSelectionState();
        ActivateGroup(group);

        var loadVersion = ++_initialMessageLoadVersion;
        if (View is not null)
            await View.ScrollToGroupAsync(group);

        await LoadMessagesAroundAsync(group, messageId, messageSeq, loadVersion);
    }

    private void ClearMessageFilter(AvaQQGroup conversation)
    {
        _conversationMessageFilters.Remove(conversation.ConversationKey);
        if (SelectedGroup?.ConversationKey == conversation.ConversationKey)
            MessageFilter = MessageFilterCriteria.Empty;
    }

    private MessageRecord? ResolveJumpTarget(
        uint groupId,
        long messageId,
        long messageSeq)
    {
        return ResolveJumpTarget(groupId, [messageId], [], [messageSeq]);
    }

    private MessageRecord? ResolveJumpTarget(
        uint groupId,
        IReadOnlyList<long> messageIds,
        IReadOnlyList<long> messageRandoms,
        IReadOnlyList<long> messageSeqs)
    {
        IQueryable<GroupMessage>? query = null;
        if (_qqDatabaseService.MessageDatabase is { } messageDatabase)
        {
            query = messageDatabase.DbContext.GroupMessages;
        }
        else if (_qqDatabaseService.AndroidMessageDatabase is { } androidMessageDatabase)
        {
            query = androidMessageDatabase.DbContext.GroupMessages;
        }

        if (query is null)
            return null;

        query = query
            .Where(message => message.GroupId == groupId)
            .Where(message => message.MessageType != MessageType.Empty);

        var messageRandomCandidates = messageRandoms
            .Where(value => value > 0)
            .Distinct()
            .ToArray();
        foreach (var messageRandom in messageRandomCandidates)
        {
            foreach (var messageSeq in messageSeqs.Where(value => value > 0).Distinct())
            {
                var targetMessage = query
                    .FirstOrDefault(message =>
                        message.MessageRandom == messageRandom &&
                        message.MessageSeq == messageSeq);

                if (targetMessage is not null)
                    return MessageRecord.FromGroup(targetMessage);
            }

            foreach (var messageId in messageIds.Where(value => value > 0).Distinct())
            {
                var targetMessage = query
                    .FirstOrDefault(message =>
                        message.MessageRandom == messageRandom &&
                        message.MessageId == messageId);

                if (targetMessage is not null)
                    return MessageRecord.FromGroup(targetMessage);
            }

            var randomMatches = query
                .Where(message => message.MessageRandom == messageRandom)
                .Take(2)
                .ToArray();
            if (randomMatches.Length == 1)
                return MessageRecord.FromGroup(randomMatches[0]);
        }

        if (messageRandomCandidates.Length > 0)
            return null;

        foreach (var messageId in messageIds.Where(value => value > 0).Distinct())
        {
            var targetMessage = query
                .FirstOrDefault(message => message.MessageId == messageId);

            if (targetMessage is not null)
                return MessageRecord.FromGroup(targetMessage);
        }

        foreach (var messageSeq in messageSeqs.Where(value => value > 0).Distinct())
        {
            var seqMatches = query
                .Where(message => message.MessageSeq == messageSeq)
                .Take(2)
                .ToArray();

            if (seqMatches.Length == 1)
                return MessageRecord.FromGroup(seqMatches[0]);
        }

        return null;
    }

    private MessageRecord? ResolvePrivateJumpTarget(
        long conversationId,
        IReadOnlyList<long> messageIds,
        IReadOnlyList<long> messageRandoms,
        IReadOnlyList<long> messageSeqs)
    {
        IQueryable<PrivateMessage>? query = null;
        if (_qqDatabaseService.MessageDatabase is { } messageDatabase)
        {
            query = messageDatabase.DbContext.PrivateMessages;
        }
        else if (_qqDatabaseService.AndroidMessageDatabase is { } androidMessageDatabase)
        {
            query = androidMessageDatabase.DbContext.PrivateMessages;
        }

        if (query is null)
            return null;

        query = query
            .Where(message => message.ConversationId == conversationId)
            .Where(message => message.MessageType != MessageType.Empty);

        var messageRandomCandidates = messageRandoms
            .Where(value => value > 0)
            .Distinct()
            .ToArray();
        foreach (var messageRandom in messageRandomCandidates)
        {
            foreach (var messageSeq in messageSeqs.Where(value => value > 0).Distinct())
            {
                var targetMessage = query
                    .FirstOrDefault(message =>
                        message.MessageRandom == messageRandom &&
                        message.MessageSeq == messageSeq);

                if (targetMessage is not null)
                    return MessageRecord.FromPrivate(targetMessage);
            }

            foreach (var messageId in messageIds.Where(value => value > 0).Distinct())
            {
                var targetMessage = query
                    .FirstOrDefault(message =>
                        message.MessageRandom == messageRandom &&
                        message.MessageId == messageId);

                if (targetMessage is not null)
                    return MessageRecord.FromPrivate(targetMessage);
            }

            var randomMatches = query
                .Where(message => message.MessageRandom == messageRandom)
                .Take(2)
                .ToArray();
            if (randomMatches.Length == 1)
                return MessageRecord.FromPrivate(randomMatches[0]);
        }

        if (messageRandomCandidates.Length > 0 && messageSeqs.Count == 0)
            return null;

        foreach (var messageId in messageIds.Where(value => value > 0).Distinct())
        {
            var targetMessage = query
                .FirstOrDefault(message => message.MessageId == messageId);

            if (targetMessage is not null)
                return MessageRecord.FromPrivate(targetMessage);
        }

        foreach (var messageSeq in messageSeqs.Where(value => value > 0).Distinct())
        {
            var seqMatches = query
                .Where(message => message.MessageSeq == messageSeq)
                .Take(2)
                .ToArray();

            if (seqMatches.Length == 1)
                return MessageRecord.FromPrivate(seqMatches[0]);
        }

        return null;
    }

    private bool HasMessageDatabase(AvaQQGroup conversation)
    {
        return IsPCQQConversation(conversation)
            ? _qqDatabaseService.PCQQMessageDatabase is not null
            : HasNtMessageDatabase;
    }

    private static bool IsPCQQConversation(AvaQQGroup conversation)
    {
        return IsPCQQConversation(conversation.ConversationType);
    }

    private static bool IsPCQQConversation(AvaConversationType conversationType)
    {
        return conversationType is AvaConversationType.PCQQGroup or AvaConversationType.PCQQPrivate;
    }

    private void HandleSelectedGroupChanged(AvaQQGroup? value)
    {
        if (value is not null && value.ConversationKey == _activeConversationKey)
            return;

        if (_activeConversationKey is { } previousActiveConversationKey &&
            _groups.FirstOrDefault(group => group.ConversationKey == previousActiveConversationKey) is { } previousGroup)
        {
            previousGroup.IsActive = false;
        }

        _activeConversationKey = value?.ConversationKey;
        if (value is not null)
        {
            value.IsActive = true;
            MessageFilter = _conversationMessageFilters.GetValueOrDefault(value.ConversationKey, MessageFilterCriteria.Empty);
            if (!_selectedConversationKeys.Contains(value.ConversationKey))
            {
                _selectedConversationKeys.Clear();
                _selectedConversationKeys.Add(value.ConversationKey);
                ApplyGroupSelectionState();
            }
        }

        var loadVersion = ++_initialMessageLoadVersion;
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

    partial void OnGroupSearchTextChanged(string value)
    {
        RefreshFilteredGroups();
    }

    private async Task LoadInitialMessagesAsync(AvaQQGroup conversation, int loadVersion)
    {
        View?.HideMessagesUntilNextScrollToBottom();
        ClearMessageSelection();
        _messages.Clear();
        ResetMessageWindowState();

        try
        {
            if (!HasMessageDatabase(conversation))
            {
                View?.ShowMessagesImmediately();
                return;
            }

            if (View is not null)
                await View.WaitForMessageRefreshFrameAsync();

            if (loadVersion != _initialMessageLoadVersion ||
                SelectedGroup?.ConversationKey != conversation.ConversationKey)
                return;

            var messages = LoadInitialMessages(conversation);

            if (loadVersion != _initialMessageLoadVersion ||
                SelectedGroup?.ConversationKey != conversation.ConversationKey)
                return;

            CacheSenderInfos(conversation, messages);
            _messages.AddLastRange(messages.Select(v => CreateAvaQQMessage(v, conversation)));
            UpdateMessageWindowState(messages, olderAvailable: messages.Count == PageSize, newerAvailable: false);
            UpdateCurrentConversationLatestPreview(conversation, messages);
            ScrollToBottom();
        }
        catch
        {
            if (loadVersion == _initialMessageLoadVersion)
                View?.ShowMessagesImmediately();
        }
    }

    private async Task LoadEarliestMessagesAsync(AvaQQGroup conversation, int loadVersion)
    {
        View?.HideMessagesUntilNextMessageJump();
        ClearMessageSelection();
        _messages.Clear();
        ResetMessageWindowState();

        try
        {
            if (!HasMessageDatabase(conversation))
            {
                View?.ShowMessagesImmediately();
                return;
            }

            if (View is not null)
                await View.WaitForMessageRefreshFrameAsync();

            if (loadVersion != _initialMessageLoadVersion ||
                SelectedGroup?.ConversationKey != conversation.ConversationKey)
                return;

            var messages = LoadEarliestMessages(conversation, PageSize);

            if (loadVersion != _initialMessageLoadVersion ||
                SelectedGroup?.ConversationKey != conversation.ConversationKey)
                return;

            CacheSenderInfos(conversation, messages);
            _messages.AddLastRange(messages.Select(v => CreateAvaQQMessage(v, conversation)));
            UpdateMessageWindowState(messages, olderAvailable: false, newerAvailable: messages.Count == PageSize);
            View?.ScrollToTop();
            View?.ShowMessagesImmediately();
        }
        catch
        {
            if (loadVersion == _initialMessageLoadVersion)
                View?.ShowMessagesImmediately();
        }
    }

    private async Task LoadMessagesAroundAsync(AvaQQGroup? conversation, long messageId, long messageSeq, int loadVersion)
    {
        if (conversation is null)
            return;

        View?.HideMessagesUntilNextMessageJump();
        ClearMessageSelection();
        _messages.Clear();
        ResetMessageWindowState();

        try
        {
            if (!HasMessageDatabase(conversation))
            {
                View?.ShowMessagesImmediately();
                return;
            }

            if (View is not null)
                await View.WaitForMessageRefreshFrameAsync();

            if (loadVersion != _initialMessageLoadVersion ||
                SelectedGroup?.ConversationKey != conversation.ConversationKey)
                return;

            var olderMessages = LoadOlderMessages(conversation, messageSeq, messageId, JumpContextPageSize);
            var targetAndNewerMessages = LoadTargetAndNewerMessages(conversation, messageSeq, messageId);

            if (loadVersion != _initialMessageLoadVersion ||
                SelectedGroup?.ConversationKey != conversation.ConversationKey)
                return;

            var messages = olderMessages
                .OrderBy(message => message.MessageSeq)
                .Concat(targetAndNewerMessages)
                .DistinctBy(message => message.MessageId)
                .OrderBy(message => message.MessageSeq)
                .ThenBy(message => message.MessageId)
                .ToList();

            CacheSenderInfos(conversation, messages);
            _messages.AddLastRange(messages.Select(message => CreateAvaQQMessage(message, conversation)));
            UpdateMessageWindowState(
                messages,
                olderAvailable: olderMessages.Count == JumpContextPageSize,
                newerAvailable: targetAndNewerMessages.Count == JumpContextPageSize + 1);

            View?.ScrollToMessage(messageId);
        }
        catch
        {
            if (loadVersion == _initialMessageLoadVersion)
                View?.ShowMessagesImmediately();
        }
    }

    public int LoadPreviousMessages()
    {
        if (IsLoadingPrevious ||
            !_hasOlderMessages ||
            SelectedGroup is null ||
            !HasMessageDatabase(SelectedGroup) ||
            _messages.Count == 0)
        {
            return 0;
        }

        try
        {
            IsLoadingPrevious = true;
            var oldestMessage = _messages.First();
            var messages = LoadOlderMessages(SelectedGroup, oldestMessage.MessageSeq, oldestMessage.MessageId, PageSize);

            CacheSenderInfos(SelectedGroup, messages);
            foreach (var message in messages)
            {
                _messages.AddFirst(CreateAvaQQMessage(message, SelectedGroup));
            }

            _hasOlderMessages = messages.Count == PageSize;
            OnPropertyChanged(nameof(HasOlderMessages));
            return messages.Count;
        }
        finally
        {
            IsLoadingPrevious = false;
        }
    }

    public int LoadNextMessages()
    {
        if (IsLoadingNext ||
            !_hasNewerMessages ||
            SelectedGroup is null ||
            !HasMessageDatabase(SelectedGroup) ||
            _messages.Count == 0)
        {
            return 0;
        }

        try
        {
            IsLoadingNext = true;
            var newestMessage = _messages.Last();
            var messages = LoadNewerMessages(SelectedGroup, newestMessage.MessageSeq, newestMessage.MessageId, PageSize);

            CacheSenderInfos(SelectedGroup, messages);
            _messages.AddLastRange(messages.Select(message => CreateAvaQQMessage(message, SelectedGroup)));
            _hasNewerMessages = messages.Count == PageSize;
            OnPropertyChanged(nameof(HasNewerMessages));
            if (!_hasNewerMessages)
            {
                UpdateCurrentConversationLatestPreview(SelectedGroup, messages);
            }

            return messages.Count;
        }
        finally
        {
            IsLoadingNext = false;
        }
    }

    public IReadOnlyList<AvaQQMessage> SelectedMessages => _messages
        .Where(message => message.CanSelect && message.IsSelected)
        .ToArray();

    public void SetSelectedMessages(IEnumerable<AvaQQMessage> selectedMessages)
    {
        var selectedMessageSet = selectedMessages
            .Where(message => message.CanSelect)
            .ToHashSet(ReferenceEqualityComparer.Instance);

        var selectedCount = 0;
        foreach (var message in _messages)
        {
            var isSelected = message.CanSelect && selectedMessageSet.Contains(message);
            message.IsSelected = isSelected;
            if (isSelected)
                selectedCount++;
        }

        UpdateMessageSelectionState(selectedCount);
    }

    public void AddSelectedMessages(IEnumerable<AvaQQMessage> selectedMessages)
    {
        var selectedMessageSet = selectedMessages
            .Where(message => message.CanSelect)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        if (selectedMessageSet.Count == 0)
            return;

        var selectedCount = 0;
        foreach (var message in _messages)
        {
            if (selectedMessageSet.Contains(message))
            {
                message.IsSelected = true;
            }

            if (message.IsSelected)
                selectedCount++;
        }

        UpdateMessageSelectionState(selectedCount);
    }

    public void ToggleMessageSelection(AvaQQMessage message)
    {
        if (!message.CanSelect || !_messages.Contains(message))
        {
            message.IsSelected = false;
            return;
        }

        message.IsSelected = !message.IsSelected;
        UpdateMessageSelectionState(_messages.Count(item => item.CanSelect && item.IsSelected));
    }

    public void ClearMessageSelection()
    {
        foreach (var message in _messages)
        {
            message.IsSelected = false;
        }

        UpdateMessageSelectionState(0);
    }

    private void UpdateMessageSelectionState(int selectedCount)
    {
        SelectedMessageCount = selectedCount;
        IsMessageMultiSelectMode = selectedCount > 0;
        OnPropertyChanged(nameof(SelectedMessages));
    }

    private MessageFilterCriteria GetCurrentMessageFilter(AvaQQGroup conversation)
    {
        return _conversationMessageFilters.GetValueOrDefault(conversation.ConversationKey, MessageFilterCriteria.Empty);
    }

    private void ResetMessageWindowState()
    {
        _hasOlderMessages = false;
        _hasNewerMessages = false;
        IsLoadingPrevious = false;
        IsLoadingNext = false;
        OnPropertyChanged(nameof(HasOlderMessages));
        OnPropertyChanged(nameof(HasNewerMessages));
    }

    private void UpdateMessageWindowState(
        IReadOnlyCollection<MessageRecord> messages,
        bool olderAvailable,
        bool newerAvailable)
    {
        _hasOlderMessages = messages.Count > 0 && olderAvailable;
        _hasNewerMessages = messages.Count > 0 && newerAvailable;
        OnPropertyChanged(nameof(HasOlderMessages));
        OnPropertyChanged(nameof(HasNewerMessages));
    }

    private void UpdateCurrentConversationLatestPreview(
        AvaQQGroup conversation,
        IReadOnlyCollection<MessageRecord> messages)
    {
        if (!GetCurrentMessageFilter(conversation).IsEmpty)
            return;

        if (messages.Count == 0 ||
            SelectedGroup?.ConversationKey != conversation.ConversationKey)
        {
            return;
        }

        var latestMessage = messages
            .OrderBy(message => message.MessageSeq)
            .ThenBy(message => message.MessageId)
            .Last();
        conversation.LatestMessageText = CreateLatestMessageText(conversation, latestMessage);
        conversation.LatestMessageTime = latestMessage.MessageTime;
    }

    private List<MessageRecord> LoadInitialMessages(AvaQQGroup conversation)
    {
        if (IsPCQQConversation(conversation))
        {
            return LoadInitialPCQQMessages(conversation);
        }

        var androidDatabase = _qqDatabaseService.AndroidMessageDatabase;
        var messageDatabase = _qqDatabaseService.MessageDatabase;
        return (conversation.ConversationType, messageDatabase, androidDatabase) switch
        {
            (AvaConversationType.Group, { } database, _) => ApplyMessageFilter(database.DbContext.GroupMessages
                .Where(message => message.GroupId == conversation.GroupId)
                .Where(message => message.MessageType != MessageType.Empty),
                conversation,
                GetCurrentMessageFilter(conversation))
                .OrderByDescending(message => message.MessageSeq)
                .ThenByDescending(message => message.MessageId)
                .Take(PageSize)
                .OrderBy(message => message.MessageSeq)
                .ThenBy(message => message.MessageId)
                .Select(message => MessageRecord.FromGroup(message))
                .ToList(),

            (AvaConversationType.Group, _, { } database) => ApplyMessageFilter(database.DbContext.GroupMessages
                .Where(message => message.GroupId == conversation.GroupId)
                .Where(message => message.MessageType != MessageType.Empty),
                conversation,
                GetCurrentMessageFilter(conversation))
                .OrderByDescending(message => message.MessageSeq)
                .ThenByDescending(message => message.MessageId)
                .Take(PageSize)
                .OrderBy(message => message.MessageSeq)
                .ThenBy(message => message.MessageId)
                .Select(message => MessageRecord.FromGroup(message))
                .ToList(),

            (AvaConversationType.Private, { } database, _) => ApplyMessageFilter(database.DbContext.PrivateMessages
                .Where(message => message.ConversationId == conversation.PrivateConversationId)
                .Where(message => message.MessageType != MessageType.Empty),
                conversation,
                GetCurrentMessageFilter(conversation))
                .OrderByDescending(message => message.MessageSeq)
                .ThenByDescending(message => message.MessageId)
                .Take(PageSize)
                .OrderBy(message => message.MessageSeq)
                .ThenBy(message => message.MessageId)
                .Select(message => MessageRecord.FromPrivate(message))
                .ToList(),

            (AvaConversationType.Private, _, { } database) => ApplyMessageFilter(database.DbContext.PrivateMessages
                .Where(message => message.ConversationId == conversation.PrivateConversationId)
                .Where(message => message.MessageType != MessageType.Empty),
                conversation,
                GetCurrentMessageFilter(conversation))
                .OrderByDescending(message => message.MessageSeq)
                .ThenByDescending(message => message.MessageId)
                .Take(PageSize)
                .OrderBy(message => message.MessageSeq)
                .ThenBy(message => message.MessageId)
                .Select(message => MessageRecord.FromPrivate(message))
                .ToList(),

            _ => [],
        };
    }

    private List<MessageRecord> LoadEarliestMessages(AvaQQGroup conversation, int pageSize)
    {
        if (IsPCQQConversation(conversation))
        {
            return LoadEarliestPCQQMessages(conversation, pageSize);
        }

        var androidDatabase = _qqDatabaseService.AndroidMessageDatabase;
        var messageDatabase = _qqDatabaseService.MessageDatabase;
        return (conversation.ConversationType, messageDatabase, androidDatabase) switch
        {
            (AvaConversationType.Group, { } database, _) => ApplyMessageFilter(
                    database.DbContext.GroupMessages
                        .Where(message => message.GroupId == conversation.GroupId)
                        .Where(message => message.MessageType != MessageType.Empty),
                    conversation,
                    GetCurrentMessageFilter(conversation))
                .OrderBy(message => message.MessageSeq)
                .ThenBy(message => message.MessageId)
                .Take(pageSize)
                .Select(message => MessageRecord.FromGroup(message))
                .ToList(),

            (AvaConversationType.Group, _, { } database) => ApplyMessageFilter(
                    database.DbContext.GroupMessages
                        .Where(message => message.GroupId == conversation.GroupId)
                        .Where(message => message.MessageType != MessageType.Empty),
                    conversation,
                    GetCurrentMessageFilter(conversation))
                .OrderBy(message => message.MessageSeq)
                .ThenBy(message => message.MessageId)
                .Take(pageSize)
                .Select(message => MessageRecord.FromGroup(message))
                .ToList(),

            (AvaConversationType.Private, { } database, _) => ApplyMessageFilter(
                    database.DbContext.PrivateMessages
                        .Where(message => message.ConversationId == conversation.PrivateConversationId)
                        .Where(message => message.MessageType != MessageType.Empty),
                    conversation,
                    GetCurrentMessageFilter(conversation))
                .OrderBy(message => message.MessageSeq)
                .ThenBy(message => message.MessageId)
                .Take(pageSize)
                .Select(message => MessageRecord.FromPrivate(message))
                .ToList(),

            (AvaConversationType.Private, _, { } database) => ApplyMessageFilter(
                    database.DbContext.PrivateMessages
                        .Where(message => message.ConversationId == conversation.PrivateConversationId)
                        .Where(message => message.MessageType != MessageType.Empty),
                    conversation,
                    GetCurrentMessageFilter(conversation))
                .OrderBy(message => message.MessageSeq)
                .ThenBy(message => message.MessageId)
                .Take(pageSize)
                .Select(message => MessageRecord.FromPrivate(message))
                .ToList(),

            _ => [],
        };
    }

    private static IQueryable<GroupMessage> ApplyMessageFilter(
        IQueryable<GroupMessage> query,
        AvaQQGroup conversation,
        MessageFilterCriteria filter)
    {
        if (filter.StartTime is { } startTime)
            query = query.Where(message => message.MessageTime >= startTime);
        if (filter.EndTimeExclusive is { } endTime)
            query = query.Where(message => message.MessageTime < endTime);
        if (filter.SelectedDayStartTimes.Count > 0)
            query = query.Where(message => filter.SelectedDayStartTimes.Contains(message.DayTimestamp));
        if (conversation.ConversationType is AvaConversationType.Group or AvaConversationType.PCQQGroup &&
            filter.SenderIds.Count > 0)
        {
            query = query.Where(message => filter.SenderIds.Contains(message.SenderId));
        }

        return query;
    }

    private static IQueryable<PrivateMessage> ApplyMessageFilter(
        IQueryable<PrivateMessage> query,
        AvaQQGroup conversation,
        MessageFilterCriteria filter)
    {
        if (filter.StartTime is { } startTime)
            query = query.Where(message => message.MessageTime >= startTime);
        if (filter.EndTimeExclusive is { } endTime)
            query = query.Where(message => message.MessageTime < endTime);
        if (filter.SelectedDayStartTimes.Count > 0)
            query = query.Where(message => filter.SelectedDayStartTimes.Contains(message.DayTimestamp));

        return query;
    }

    private List<MessageRecord> LoadInitialPCQQMessages(AvaQQGroup conversation)
    {
        if (_qqDatabaseService.PCQQMessageDatabase is not { } pcqqDatabase ||
            string.IsNullOrWhiteSpace(conversation.PCQQTableName))
        {
            return [];
        }

        return pcqqDatabase
            .LoadLatestMessages(conversation.PCQQTableName, PageSize, GetCurrentMessageFilter(conversation).ToQueryFilter())
            .Select(message => MessageRecord.FromPCQQ(message, conversation))
            .ToList();
    }

    private List<MessageRecord> LoadEarliestPCQQMessages(AvaQQGroup conversation, int pageSize)
    {
        if (_qqDatabaseService.PCQQMessageDatabase is not { } pcqqDatabase ||
            string.IsNullOrWhiteSpace(conversation.PCQQTableName))
        {
            return [];
        }

        return pcqqDatabase
            .LoadEarliestMessages(conversation.PCQQTableName, pageSize, GetCurrentMessageFilter(conversation).ToQueryFilter())
            .Select(message => MessageRecord.FromPCQQ(message, conversation))
            .ToList();
    }

    private List<MessageRecord> LoadOlderMessages(AvaQQGroup conversation, long messageSeq, long messageId, int pageSize)
    {
        if (IsPCQQConversation(conversation))
        {
            return LoadOlderPCQQMessages(conversation, messageSeq, messageId, pageSize);
        }

        var androidDatabase = _qqDatabaseService.AndroidMessageDatabase;
        var messageDatabase = _qqDatabaseService.MessageDatabase;
        return (conversation.ConversationType, messageDatabase, androidDatabase) switch
        {
            (AvaConversationType.Group, { } database, _) => ApplyMessageFilter(database.DbContext.GroupMessages
                .Where(message => message.GroupId == conversation.GroupId)
                .Where(message => message.MessageType != MessageType.Empty),
                conversation,
                GetCurrentMessageFilter(conversation))
                .Where(message => message.MessageSeq < messageSeq ||
                                  message.MessageSeq == messageSeq && message.MessageId < messageId)
                .OrderByDescending(message => message.MessageSeq)
                .ThenByDescending(message => message.MessageId)
                .Take(pageSize)
                .Select(message => MessageRecord.FromGroup(message))
                .ToList(),

            (AvaConversationType.Group, _, { } database) => ApplyMessageFilter(database.DbContext.GroupMessages
                .Where(message => message.GroupId == conversation.GroupId)
                .Where(message => message.MessageType != MessageType.Empty),
                conversation,
                GetCurrentMessageFilter(conversation))
                .Where(message => message.MessageSeq < messageSeq ||
                                  message.MessageSeq == messageSeq && message.MessageId < messageId)
                .OrderByDescending(message => message.MessageSeq)
                .ThenByDescending(message => message.MessageId)
                .Take(pageSize)
                .Select(message => MessageRecord.FromGroup(message))
                .ToList(),

            (AvaConversationType.Private, { } database, _) => ApplyMessageFilter(database.DbContext.PrivateMessages
                .Where(message => message.ConversationId == conversation.PrivateConversationId)
                .Where(message => message.MessageType != MessageType.Empty),
                conversation,
                GetCurrentMessageFilter(conversation))
                .Where(message => message.MessageSeq < messageSeq ||
                                  message.MessageSeq == messageSeq && message.MessageId < messageId)
                .OrderByDescending(message => message.MessageSeq)
                .ThenByDescending(message => message.MessageId)
                .Take(pageSize)
                .Select(message => MessageRecord.FromPrivate(message))
                .ToList(),

            (AvaConversationType.Private, _, { } database) => ApplyMessageFilter(database.DbContext.PrivateMessages
                .Where(message => message.ConversationId == conversation.PrivateConversationId)
                .Where(message => message.MessageType != MessageType.Empty),
                conversation,
                GetCurrentMessageFilter(conversation))
                .Where(message => message.MessageSeq < messageSeq ||
                                  message.MessageSeq == messageSeq && message.MessageId < messageId)
                .OrderByDescending(message => message.MessageSeq)
                .ThenByDescending(message => message.MessageId)
                .Take(pageSize)
                .Select(message => MessageRecord.FromPrivate(message))
                .ToList(),

            _ => [],
        };
    }

    private List<MessageRecord> LoadOlderPCQQMessages(AvaQQGroup conversation, long messageSeq, long messageId, int pageSize)
    {
        if (_qqDatabaseService.PCQQMessageDatabase is not { } pcqqDatabase ||
            string.IsNullOrWhiteSpace(conversation.PCQQTableName))
        {
            return [];
        }

        return pcqqDatabase
            .LoadOlderMessages(conversation.PCQQTableName, messageSeq, messageId, pageSize, GetCurrentMessageFilter(conversation).ToQueryFilter())
            .Select(message => MessageRecord.FromPCQQ(message, conversation))
            .ToList();
    }

    private List<MessageRecord> LoadNewerMessages(AvaQQGroup conversation, long messageSeq, long messageId, int pageSize)
    {
        if (IsPCQQConversation(conversation))
        {
            return LoadNewerPCQQMessages(conversation, messageSeq, messageId, pageSize);
        }

        var androidDatabase = _qqDatabaseService.AndroidMessageDatabase;
        var messageDatabase = _qqDatabaseService.MessageDatabase;
        return (conversation.ConversationType, messageDatabase, androidDatabase) switch
        {
            (AvaConversationType.Group, { } database, _) => ApplyMessageFilter(database.DbContext.GroupMessages
                .Where(message => message.GroupId == conversation.GroupId)
                .Where(message => message.MessageType != MessageType.Empty),
                conversation,
                GetCurrentMessageFilter(conversation))
                .Where(message => message.MessageSeq > messageSeq ||
                                  message.MessageSeq == messageSeq && message.MessageId > messageId)
                .OrderBy(message => message.MessageSeq)
                .ThenBy(message => message.MessageId)
                .Take(pageSize)
                .Select(message => MessageRecord.FromGroup(message))
                .ToList(),

            (AvaConversationType.Group, _, { } database) => ApplyMessageFilter(database.DbContext.GroupMessages
                .Where(message => message.GroupId == conversation.GroupId)
                .Where(message => message.MessageType != MessageType.Empty),
                conversation,
                GetCurrentMessageFilter(conversation))
                .Where(message => message.MessageSeq > messageSeq ||
                                  message.MessageSeq == messageSeq && message.MessageId > messageId)
                .OrderBy(message => message.MessageSeq)
                .ThenBy(message => message.MessageId)
                .Take(pageSize)
                .Select(message => MessageRecord.FromGroup(message))
                .ToList(),

            (AvaConversationType.Private, { } database, _) => ApplyMessageFilter(database.DbContext.PrivateMessages
                .Where(message => message.ConversationId == conversation.PrivateConversationId)
                .Where(message => message.MessageType != MessageType.Empty),
                conversation,
                GetCurrentMessageFilter(conversation))
                .Where(message => message.MessageSeq > messageSeq ||
                                  message.MessageSeq == messageSeq && message.MessageId > messageId)
                .OrderBy(message => message.MessageSeq)
                .ThenBy(message => message.MessageId)
                .Take(pageSize)
                .Select(message => MessageRecord.FromPrivate(message))
                .ToList(),

            (AvaConversationType.Private, _, { } database) => ApplyMessageFilter(database.DbContext.PrivateMessages
                .Where(message => message.ConversationId == conversation.PrivateConversationId)
                .Where(message => message.MessageType != MessageType.Empty),
                conversation,
                GetCurrentMessageFilter(conversation))
                .Where(message => message.MessageSeq > messageSeq ||
                                  message.MessageSeq == messageSeq && message.MessageId > messageId)
                .OrderBy(message => message.MessageSeq)
                .ThenBy(message => message.MessageId)
                .Take(pageSize)
                .Select(message => MessageRecord.FromPrivate(message))
                .ToList(),

            _ => [],
        };
    }

    private List<MessageRecord> LoadNewerPCQQMessages(AvaQQGroup conversation, long messageSeq, long messageId, int pageSize)
    {
        if (_qqDatabaseService.PCQQMessageDatabase is not { } pcqqDatabase ||
            string.IsNullOrWhiteSpace(conversation.PCQQTableName))
        {
            return [];
        }

        return pcqqDatabase
            .LoadNewerMessages(conversation.PCQQTableName, messageSeq, messageId, pageSize, GetCurrentMessageFilter(conversation).ToQueryFilter())
            .Select(message => MessageRecord.FromPCQQ(message, conversation))
            .ToList();
    }

    private List<MessageRecord> LoadTargetAndNewerMessages(AvaQQGroup conversation, long messageSeq, long messageId)
    {
        var messages = LoadNewerOrEqualMessages(conversation, messageSeq, messageId, JumpContextPageSize + 1);
        if (messages.Any(message => message.MessageId == messageId))
            return messages;

        var targetMessage = LoadMessageById(conversation, messageSeq, messageId);
        if (targetMessage is null)
            return messages;

        return messages
            .Append(targetMessage)
            .DistinctBy(message => message.MessageId)
            .OrderBy(message => message.MessageSeq)
            .ThenBy(message => message.MessageId)
            .ToList();
    }

    private List<MessageRecord> LoadNewerOrEqualMessages(AvaQQGroup conversation, long messageSeq, long messageId, int pageSize)
    {
        if (IsPCQQConversation(conversation))
        {
            var messages = LoadNewerPCQQMessages(conversation, messageSeq, messageId - 1, pageSize);
            return messages
                .Where(message => message.MessageSeq > messageSeq ||
                                  message.MessageSeq == messageSeq && message.MessageId >= messageId)
                .ToList();
        }

        var androidDatabase = _qqDatabaseService.AndroidMessageDatabase;
        var messageDatabase = _qqDatabaseService.MessageDatabase;
        return (conversation.ConversationType, messageDatabase, androidDatabase) switch
        {
            (AvaConversationType.Group, { } database, _) => ApplyMessageFilter(database.DbContext.GroupMessages
                .Where(message => message.GroupId == conversation.GroupId)
                .Where(message => message.MessageType != MessageType.Empty),
                conversation,
                GetCurrentMessageFilter(conversation))
                .Where(message => message.MessageSeq > messageSeq ||
                                  message.MessageSeq == messageSeq && message.MessageId >= messageId)
                .OrderBy(message => message.MessageSeq)
                .ThenBy(message => message.MessageId)
                .Take(pageSize)
                .Select(message => MessageRecord.FromGroup(message))
                .ToList(),

            (AvaConversationType.Group, _, { } database) => ApplyMessageFilter(database.DbContext.GroupMessages
                .Where(message => message.GroupId == conversation.GroupId)
                .Where(message => message.MessageType != MessageType.Empty),
                conversation,
                GetCurrentMessageFilter(conversation))
                .Where(message => message.MessageSeq > messageSeq ||
                                  message.MessageSeq == messageSeq && message.MessageId >= messageId)
                .OrderBy(message => message.MessageSeq)
                .ThenBy(message => message.MessageId)
                .Take(pageSize)
                .Select(message => MessageRecord.FromGroup(message))
                .ToList(),

            (AvaConversationType.Private, { } database, _) => ApplyMessageFilter(database.DbContext.PrivateMessages
                .Where(message => message.ConversationId == conversation.PrivateConversationId)
                .Where(message => message.MessageType != MessageType.Empty),
                conversation,
                GetCurrentMessageFilter(conversation))
                .Where(message => message.MessageSeq > messageSeq ||
                                  message.MessageSeq == messageSeq && message.MessageId >= messageId)
                .OrderBy(message => message.MessageSeq)
                .ThenBy(message => message.MessageId)
                .Take(pageSize)
                .Select(message => MessageRecord.FromPrivate(message))
                .ToList(),

            (AvaConversationType.Private, _, { } database) => ApplyMessageFilter(database.DbContext.PrivateMessages
                .Where(message => message.ConversationId == conversation.PrivateConversationId)
                .Where(message => message.MessageType != MessageType.Empty),
                conversation,
                GetCurrentMessageFilter(conversation))
                .Where(message => message.MessageSeq > messageSeq ||
                                  message.MessageSeq == messageSeq && message.MessageId >= messageId)
                .OrderBy(message => message.MessageSeq)
                .ThenBy(message => message.MessageId)
                .Take(pageSize)
                .Select(message => MessageRecord.FromPrivate(message))
                .ToList(),

            _ => [],
        };
    }

    private MessageRecord? LoadMessageById(AvaQQGroup conversation, long messageSeq, long messageId)
    {
        if (IsPCQQConversation(conversation))
        {
            if (_qqDatabaseService.PCQQMessageDatabase is not { } pcqqDatabase ||
                string.IsNullOrWhiteSpace(conversation.PCQQTableName))
            {
                return null;
            }

            var message = pcqqDatabase.LoadMessage(conversation.PCQQTableName, messageSeq, messageId);
            return message is null
                ? null
                : MessageRecord.FromPCQQ(message, conversation);
        }

        var androidDatabase = _qqDatabaseService.AndroidMessageDatabase;
        var messageDatabase = _qqDatabaseService.MessageDatabase;
        return (conversation.ConversationType, messageDatabase, androidDatabase) switch
        {
            (AvaConversationType.Group, { } database, _) => ApplyMessageFilter(database.DbContext.GroupMessages
                .Where(message => message.GroupId == conversation.GroupId)
                .Where(message => message.MessageType != MessageType.Empty),
                conversation,
                GetCurrentMessageFilter(conversation))
                .Where(message => message.MessageId == messageId)
                .Select(message => MessageRecord.FromGroup(message))
                .FirstOrDefault(),

            (AvaConversationType.Group, _, { } database) => ApplyMessageFilter(database.DbContext.GroupMessages
                .Where(message => message.GroupId == conversation.GroupId)
                .Where(message => message.MessageType != MessageType.Empty),
                conversation,
                GetCurrentMessageFilter(conversation))
                .Where(message => message.MessageId == messageId)
                .Select(message => MessageRecord.FromGroup(message))
                .FirstOrDefault(),

            (AvaConversationType.Private, { } database, _) => ApplyMessageFilter(database.DbContext.PrivateMessages
                .Where(message => message.ConversationId == conversation.PrivateConversationId)
                .Where(message => message.MessageType != MessageType.Empty),
                conversation,
                GetCurrentMessageFilter(conversation))
                .Where(message => message.MessageId == messageId)
                .Select(message => MessageRecord.FromPrivate(message))
                .FirstOrDefault(),

            (AvaConversationType.Private, _, { } database) => ApplyMessageFilter(database.DbContext.PrivateMessages
                .Where(message => message.ConversationId == conversation.PrivateConversationId)
                .Where(message => message.MessageType != MessageType.Empty),
                conversation,
                GetCurrentMessageFilter(conversation))
                .Where(message => message.MessageId == messageId)
                .Select(message => MessageRecord.FromPrivate(message))
                .FirstOrDefault(),

            _ => null,
        };
    }

    private void CacheSenderInfos(AvaQQGroup conversation, IEnumerable<MessageRecord> messages)
    {
        var messageList = messages as IReadOnlyList<MessageRecord> ?? messages.ToArray();
        var senderNames = GetSenderNameCache(conversation.ConversationKey);
        foreach (var message in messageList)
        {
            CacheSenderName(senderNames, message.SenderId, message.SendMemberName, message.SendNickName);
        }

        var messageSenderInfos = GetMessageSenderInfoCache(conversation.ConversationKey);
        foreach (var message in messageList)
        {
            CacheMessageSenderInfo(messageSenderInfos, message, conversation, senderNames);
        }

        CacheReferencedPrivateReplySenderInfos(conversation, messageList, messageSenderInfos, senderNames);
    }

    private Dictionary<uint, string> GetSenderNameCache(string conversationKey)
    {
        if (!_conversationSenderNames.TryGetValue(conversationKey, out var senderNames))
        {
            senderNames = new Dictionary<uint, string>();
            _conversationSenderNames[conversationKey] = senderNames;
        }

        return senderNames;
    }

    private Dictionary<long, MessageSenderInfo> GetMessageSenderInfoCache(string conversationKey)
    {
        if (!_conversationMessageSenderInfos.TryGetValue(conversationKey, out var senderInfos))
        {
            senderInfos = new Dictionary<long, MessageSenderInfo>();
            _conversationMessageSenderInfos[conversationKey] = senderInfos;
        }

        return senderInfos;
    }

    private void CacheMessageSenderInfo(
        IDictionary<long, MessageSenderInfo> senderInfos,
        MessageRecord message,
        AvaQQGroup conversation,
        IDictionary<uint, string> senderNames)
    {
        if (message.MessageSeq <= 0)
            return;

        senderInfos[message.MessageSeq] = CreateMessageSenderInfo(message, conversation, senderNames);
    }

    private MessageSenderInfo CreateMessageSenderInfo(
        MessageRecord message,
        AvaQQGroup conversation,
        IDictionary<uint, string> senderNames)
    {
        return new MessageSenderInfo(
            message.SenderId,
            message.SenderUid,
            ResolveReplyTargetSenderName(message, conversation, senderNames));
    }

    private string ResolveReplyTargetSenderName(
        MessageRecord message,
        AvaQQGroup conversation,
        IDictionary<uint, string> senderNames)
    {
        var name = FirstNonEmpty(message.SendMemberName, message.SendNickName);
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        if (message.SenderId != 0 &&
            senderNames.TryGetValue(message.SenderId, out var cachedName) &&
            !string.IsNullOrWhiteSpace(cachedName))
        {
            return cachedName;
        }

        if (conversation.ConversationType == AvaConversationType.Private &&
            IsPrivatePeerMessage(message, conversation))
        {
            if (!string.IsNullOrWhiteSpace(conversation.GroupName))
                return conversation.GroupName;

            if (GetProfileInfoNameCache().TryGetName(message.SenderId, message.SenderUid, out var peerName))
                return peerName;

            return GetProfileInfoNameCache().TryGetName(conversation.PrivateUin, conversation.PrivateUid, out peerName)
                ? peerName
                : string.Empty;
        }

        return GetProfileInfoNameCache().TryGetName(message.SenderId, message.SenderUid, out var profileName)
            ? profileName
            : string.Empty;
    }

    private void CacheReferencedPrivateReplySenderInfos(
        AvaQQGroup conversation,
        IReadOnlyList<MessageRecord> messages,
        IDictionary<long, MessageSenderInfo> senderInfos,
        IDictionary<uint, string> senderNames)
    {
        if (conversation.ConversationType != AvaConversationType.Private ||
            !HasNtMessageDatabase)
        {
            return;
        }

        var missingSeqs = messages
            .Select(message => TryParseMessageContent(message.Content))
            .Where(content => content is not null)
            .SelectMany(content => content!.Segments)
            .Select(segment => segment.Reply)
            .Where(reply => reply is not null)
            .Select(reply => reply!.MessageSeq2)
            .Where(messageSeq => messageSeq > 0 && !senderInfos.ContainsKey(messageSeq))
            .Distinct()
            .Take(PageSize)
            .ToArray();

        if (missingSeqs.Length == 0)
            return;

        var referencedMessages = _qqDatabaseService.MessageDatabase is { } messageDatabase
            ? messageDatabase.DbContext.PrivateMessages
                .Where(message => message.ConversationId == conversation.PrivateConversationId)
                .Where(message => missingSeqs.Contains(message.MessageSeq))
                .Where(message => message.MessageType != MessageType.Empty)
                .Select(message => MessageRecord.FromPrivate(message))
                .ToList()
            : _qqDatabaseService.AndroidMessageDatabase is { } androidMessageDatabase
                ? androidMessageDatabase.DbContext.PrivateMessages
                    .Where(message => message.ConversationId == conversation.PrivateConversationId)
                    .Where(message => missingSeqs.Contains(message.MessageSeq))
                    .Where(message => message.MessageType != MessageType.Empty)
                    .Select(message => MessageRecord.FromPrivate(message))
                    .ToList()
                : [];

        foreach (var message in referencedMessages)
        {
            CacheSenderName(senderNames, message.SenderId, message.SendMemberName, message.SendNickName);
            CacheMessageSenderInfo(senderInfos, message, conversation, senderNames);
        }
    }

    private static void CacheSenderName(
        IDictionary<uint, string> senderNames,
        uint senderId,
        string? sendMemberName,
        string? sendNickName)
    {
        if (senderId == 0)
            return;

        var name = FirstNonEmpty(sendMemberName, sendNickName);
        if (!string.IsNullOrWhiteSpace(name))
        {
            senderNames[senderId] = name;
        }
    }

    /// <summary>
    /// 从数据库消息创建 AvaQQMessage
    /// </summary>
    private AvaQQMessage CreateAvaQQMessage(MessageRecord item, AvaQQGroup conversation)
    {
        if (IsPCQQConversation(conversation))
        {
            return CreatePCQQAvaMessage(item, conversation);
        }

        var mediaContext = CreateLocalMediaContext();
        var content = TryParseMessageContent(item.Content);
        var segments = content is null
            ? []
            : CreateMessageSegments(item, content, mediaContext);
        var forwardedMessages = CreateForwardedMessages(item, content, mediaContext);
        var senderNames = GetSenderNameCache(conversation.ConversationKey);
        var messageSenderInfos = GetMessageSenderInfoCache(conversation.ConversationKey);
        var senderName = ResolveMessageSenderName(item, conversation, senderNames);
        var systemHint = CreateSystemHintMessage(content);
        var reply = CreateReplyMessage(
            item,
            content,
            conversation,
            senderName,
            senderNames,
            messageSenderInfos,
            mediaContext);

        if (systemHint is null && !HasDisplayContent(segments))
        {
            segments.Clear();
            segments.Add(AvaQQMessageSegment.CreateUnsupportedText(CreateUnsupportedMessageText(item)));
        }

        var displayText = CreateDisplayText(segments);
        var message = new AvaQQMessage()
        {
            MessageId = item.MessageId,
            MessageRandom = item.MessageRandom,
            MessageSeq = item.MessageSeq,
            GroupId = item.GroupId,
            ConversationType = conversation.ConversationType,
            ConversationKey = conversation.ConversationKey,
            PrivateConversationId = item.PrivateConversationId,
            PrivateUin = item.PeerUin,
            PeerUid = item.PeerUid,
            DisplayText = displayText,
            Segments = segments,
            ForwardedMessages = forwardedMessages,
            Reply = reply,
            Name = senderName,
            MessageTime = item.MessageTime,
            SenderId = item.SenderId,
            ProtobufContent = item.Content,
            IsHoverTimeVisible = AlwaysShowMessageTime,
        };

        if (systemHint is not null)
        {
            message.IsSystemHint = true;
            message.SystemHintSourceName = systemHint.Value.SourceName;
            message.SystemHintSourceUin = systemHint.Value.SourceUin;
            message.SystemHintTargetName = systemHint.Value.TargetName;
            message.SystemHintTargetUin = systemHint.Value.TargetUin;
            message.SystemHintAction = systemHint.Value.Action;
            message.SystemHintSuffix = systemHint.Value.Suffix;
            message.SystemHintActionImageUrl = systemHint.Value.ActionImageUrl;
            message.DisplayText = systemHint.Value.DisplayText;
            message.Segments = [AvaQQMessageSegment.CreateText(systemHint.Value.DisplayText)];
            message.Reply = null;
        }

        return message;
    }

    private static SystemHintDisplay? CreateSystemHintMessage(QQMessageContent? content)
    {
        if (content is null)
            return null;

        var hint = content.Segments
            .Select(segment => segment.SystemHint)
            .FirstOrDefault(systemHint => systemHint?.Participants.Count > 0);
        if (hint is null || hint.Participants.Count == 0)
            return null;

        var sourceName = hint.Participants[0].Nickname.Trim();
        var targetName = hint.Participants.Count >= 2
            ? hint.Participants[1].Nickname.Trim()
            : string.Empty;
        var sourceUin = (hint.GetProperty("uin_str1") ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(sourceUin))
        {
            sourceUin = hint.Participants[0].Uid.Trim();
        }

        var targetUin = (hint.GetProperty("uin_str2") ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(targetUin) && hint.Participants.Count >= 2)
        {
            targetUin = hint.Participants[1].Uid.Trim();
        }

        var action = (hint.Action ?? string.Empty).Trim();
        var suffix = hint.Suffix ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceName) || string.IsNullOrWhiteSpace(action))
        {
            return null;
        }

        if (!hint.IsSingleActor && string.IsNullOrWhiteSpace(targetName))
        {
            return null;
        }

        var displayText = !string.IsNullOrWhiteSpace(hint.DisplayText)
            ? hint.DisplayText.Trim()
            : $"{sourceName}{action}{targetName}{suffix}";
        return new SystemHintDisplay(
            sourceName,
            sourceUin,
            targetName,
            targetUin,
            action,
            suffix,
            hint.ActionImageUrl,
            displayText);
    }

    private AvaQQMessage CreatePCQQAvaMessage(MessageRecord item, AvaQQGroup conversation)
    {
        var parsed = PCQQMessageContentParser.Parse(item.Content);
        var segments = CreatePCQQMessageSegments(parsed, _qqDatabaseService.PCQQDataPath);
        var senderName = FirstNonEmpty(item.SendMemberName, item.SendNickName);
        if (string.IsNullOrWhiteSpace(senderName) &&
            conversation.ConversationType == AvaConversationType.PCQQPrivate &&
            IsPrivatePeerMessage(item, conversation))
        {
            senderName = conversation.DisplayName;
        }

        if (string.IsNullOrWhiteSpace(senderName) && item.SenderId != 0)
        {
            senderName = item.SenderId.ToString();
        }

        if (!HasDisplayContent(segments))
        {
            segments.Clear();
            segments.Add(AvaQQMessageSegment.CreateUnsupportedText(parsed.DisplayText));
        }

        var reply = CreatePCQQReplyMessage(parsed, _qqDatabaseService.PCQQMessageDatabase);
        return new AvaQQMessage
        {
            MessageId = item.MessageId,
            MessageRandom = item.MessageRandom,
            MessageSeq = item.MessageSeq,
            PCQQMessageSeq = parsed.MessageSeq,
            GroupId = item.GroupId,
            ConversationType = conversation.ConversationType,
            ConversationKey = conversation.ConversationKey,
            PrivateConversationId = item.PrivateConversationId,
            PrivateUin = item.PeerUin,
            PeerUid = item.PeerUid,
            DisplayText = CreateDisplayText(segments),
            Segments = segments,
            Reply = reply,
            Name = senderName,
            MessageTime = item.MessageTime,
            SenderId = item.SenderId,
            ProtobufContent = item.Content,
            IsHoverTimeVisible = AlwaysShowMessageTime,
        };
    }

    private static AvaReplyMessage? CreatePCQQReplyMessage(
        PCQQParsedMessage parsed,
        PCQQMessageReader? pcqqDatabase)
    {
        if (parsed.Reply is null)
            return null;

        var previewText = parsed.Reply.PreviewText.Trim();
        if (string.IsNullOrWhiteSpace(previewText))
            return null;

        var senderName = parsed.Reply.SenderUin == 0
            ? null
            : pcqqDatabase?.ResolveContactName(parsed.Reply.SenderUin);
        return new AvaReplyMessage
        {
            MessageSeq = parsed.Reply.MessageSeq,
            SenderId = parsed.Reply.SenderUin,
            SenderName = FirstNonEmpty(senderName, parsed.Reply.SenderUin == 0 ? null : parsed.Reply.SenderUin.ToString()),
            Segments = CreateTextSegments(previewText).ToList(),
            PreviewText = previewText,
        };
    }

    private static List<AvaQQMessageSegment> CreatePCQQMessageSegments(
        PCQQParsedMessage parsed,
        string? pcqqDataPath)
    {
        if (parsed.Segments.Count == 0)
        {
            return string.Equals(parsed.DisplayText, "[PCQQ消息]", StringComparison.Ordinal)
                ? [AvaQQMessageSegment.CreateUnsupportedText(parsed.DisplayText)]
                : CreateTextSegments(parsed.DisplayText).ToList();
        }

        var segments = new List<AvaQQMessageSegment>();
        foreach (var segment in parsed.Segments)
        {
            if (segment.Type == PCQQMessageSegmentType.Text)
            {
                segments.AddRange(CreateTextSegments(segment.Text));
                continue;
            }

            if (segment.Type == PCQQMessageSegmentType.Face && segment.FaceId is { } faceId)
            {
                var faceSegment = AvaQQMessageSegment.CreateQQFace(faceId);
                segments.Add(string.IsNullOrWhiteSpace(faceSegment.FaceAssetPath)
                    ? AvaQQMessageSegment.CreateText(segment.Text)
                    : faceSegment);
                continue;
            }

            if (segment.Type == PCQQMessageSegmentType.Image)
            {
                var localPath = ResolvePCQQImagePath(pcqqDataPath, segment.ImageRelativePath);
                var imageSize = LocalImageFile.TryGetImageSize(localPath);
                var width = segment.ImageWidth ?? imageSize?.Width;
                var height = segment.ImageHeight ?? imageSize?.Height;
                segments.Add(string.IsNullOrWhiteSpace(localPath)
                    ? AvaQQMessageSegment.CreateBrokenImage(null, null, "[图片文件未找到]")
                    : AvaQQMessageSegment.CreateImage(
                        localPath,
                        width,
                        height,
                        "[图片]"));
            }
        }

        return segments;
    }

    private string ResolveMessageSenderName(
        MessageRecord item,
        AvaQQGroup conversation,
        IReadOnlyDictionary<uint, string> senderNames)
    {
        var name = FirstNonEmpty(item.SendMemberName, item.SendNickName);
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        if (item.SenderId != 0 &&
            senderNames.TryGetValue(item.SenderId, out var cachedName) &&
            !string.IsNullOrWhiteSpace(cachedName))
        {
            return cachedName;
        }

        if (conversation.ConversationType == AvaConversationType.Private)
        {
            if (IsPrivatePeerMessage(item, conversation))
            {
                return conversation.DisplayName;
            }

            return ResolveProfileDisplayName(item.SenderId, item.SenderUid, fallback: "我");
        }

        if (item.SenderId != 0)
            return item.SenderId.ToString();

        return !string.IsNullOrWhiteSpace(item.SenderUid)
            ? item.SenderUid
            : conversation.ConversationType == AvaConversationType.Private
                ? conversation.DisplayName
                : string.Empty;
    }

    private static bool IsPrivatePeerMessage(MessageRecord item, AvaQQGroup conversation)
    {
        if (conversation.ConversationType is not (AvaConversationType.Private or AvaConversationType.PCQQPrivate))
            return false;

        if (item.SenderId != 0)
        {
            if (conversation.PrivateUin != 0 && item.SenderId == conversation.PrivateUin)
                return true;

            if (item.PeerUin != 0 && item.SenderId == item.PeerUin)
                return true;
        }

        if (!string.IsNullOrWhiteSpace(item.SenderUid))
        {
            if (!string.IsNullOrWhiteSpace(conversation.PrivateUid) &&
                string.Equals(item.SenderUid, conversation.PrivateUid, StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(item.PeerUid) &&
                string.Equals(item.SenderUid, item.PeerUid, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPrivatePeerMessage(AvaQQMessage item, AvaQQGroup conversation)
    {
        if (conversation.ConversationType is not (AvaConversationType.Private or AvaConversationType.PCQQPrivate))
            return false;

        if (item.SenderId != 0)
        {
            if (conversation.PrivateUin != 0 && item.SenderId == conversation.PrivateUin)
                return true;

            if (item.PrivateUin != 0 && item.SenderId == item.PrivateUin)
                return true;
        }

        return !string.IsNullOrWhiteSpace(item.PeerUid) &&
               !string.IsNullOrWhiteSpace(conversation.PrivateUid) &&
               string.Equals(item.PeerUid, conversation.PrivateUid, StringComparison.Ordinal) &&
               item.SenderId == conversation.PrivateUin;
    }

    private static string CreateDisplayText(IReadOnlyList<AvaQQMessageSegment> segments)
    {
        if (segments.Count == 0)
            return string.Empty;

        var displayText = new System.Text.StringBuilder();
        var isAtLineStart = true;

        for (var i = 0; i < segments.Count; i++)
        {
            var segmentText = segments[i].DisplayText;
            if (string.IsNullOrEmpty(segmentText))
                continue;

            if (segments[i].Type == AvaQQMessageSegmentType.Image)
            {
                if (!isAtLineStart)
                {
                    displayText.Append('\n');
                }

                displayText.Append(segmentText);
                isAtLineStart = false;

                if (HasDisplayTextAfter(segments, i))
                {
                    displayText.Append('\n');
                    isAtLineStart = true;
                }

                continue;
            }

            displayText.Append(segmentText);
            isAtLineStart = segmentText[^1] is '\n' or '\r';
        }

        return displayText.ToString();
    }

    private static bool HasDisplayContent(IReadOnlyList<AvaQQMessageSegment> segments)
    {
        return segments.Count > 0 && !string.IsNullOrWhiteSpace(CreateDisplayText(segments));
    }

    private static bool HasDisplayTextAfter(IReadOnlyList<AvaQQMessageSegment> segments, int index)
    {
        for (var i = index + 1; i < segments.Count; i++)
        {
            if (!string.IsNullOrEmpty(segments[i].DisplayText))
                return true;
        }

        return false;
    }

    private static QQMessageContent? TryParseMessageContent(byte[]? content)
    {
        if (content is null)
            return null;

        try
        {
            return QQMessageReader.ParseMessage(content);
        }
        catch
        {
            return null;
        }
    }

    private static List<AvaQQMessageSegment> CreateMessageSegments(
        MessageRecord item,
        QQMessageContent content,
        LocalMediaContext mediaContext)
    {
        var segments = new List<AvaQQMessageSegment>();

        foreach (var segment in content.Segments)
        {
            AddMessageSegment(
                segments,
                segment,
                item.MessageType,
                item.SubMessageType,
                item.MessageTime,
                mediaContext,
                CreateUnsupportedMessageText(item, segment));
        }

        return segments;
    }

    private AvaReplyMessage? CreateReplyMessage(
        MessageRecord item,
        QQMessageContent? content,
        AvaQQGroup conversation,
        string currentSenderName,
        IReadOnlyDictionary<uint, string> groupSenderNames,
        IReadOnlyDictionary<long, MessageSenderInfo> messageSenderInfos,
        LocalMediaContext mediaContext)
    {
        if (content is null)
            return null;

        var reply = content.Segments
            .Select(segment => segment.Reply)
            .FirstOrDefault(reply => reply is not null);
        if (reply is null)
            return null;

        var replySegments = CreateReplyPreviewSegments(
            item.MessageType,
            item.SubMessageType,
            item.MessageTime,
            reply,
            mediaContext);
        var previewText = CreateDisplayText(replySegments);
        if (string.IsNullOrWhiteSpace(previewText))
        {
            previewText = !string.IsNullOrWhiteSpace(reply.PreviewText)
                ? reply.PreviewText
                : "[原消息]";
        }

        var sourceGroupId = ResolveReplySourceGroupId(conversation, reply);
        return new AvaReplyMessage
        {
            MessageId = reply.MessageId,
            InternalMessageId = reply.InternalMessageId,
            MessageRandom = reply.MessageRandom,
            MessageSeq = ResolveReplyMessageSeq(item.ReplyToMessageSeq, reply),
            AlternateMessageSeq = reply.MessageSeq2,
            SenderId = reply.SenderId,
            SenderName = ResolveReplySenderName(
                currentSenderName,
                item.SenderId,
                conversation,
                reply,
                groupSenderNames,
                messageSenderInfos),
            MessageTime = reply.MessageTime,
            SourceGroupId = sourceGroupId,
            SourceGroupName = sourceGroupId == 0 ? string.Empty : ResolveReplySourceGroupName(reply),
            Segments = replySegments,
            PreviewText = previewText.Trim(),
        };
    }

    private static List<AvaQQMessageSegment> CreateReplyPreviewSegments(
        MessageType messageType,
        SubMessageType subMessageType,
        int messageTime,
        QQReplyMessage reply,
        LocalMediaContext mediaContext)
    {
        var segments = new List<AvaQQMessageSegment>();
        foreach (var segment in reply.Segments)
        {
            AddMessageSegment(
                segments,
                segment,
                messageType,
                subMessageType,
                reply.MessageTime > 0 ? reply.MessageTime : messageTime,
                mediaContext,
                "[原消息]");
        }

        if (HasDisplayContent(segments))
            return segments;

        segments.Clear();
        if (!string.IsNullOrWhiteSpace(reply.PreviewText))
        {
            segments.AddRange(CreateTextSegments(reply.PreviewText));
        }

        return segments;
    }

    private string ResolveReplySenderName(
        string? currentSenderName,
        uint currentSenderId,
        AvaQQGroup conversation,
        QQReplyMessage reply,
        IReadOnlyDictionary<uint, string> groupSenderNames,
        IReadOnlyDictionary<long, MessageSenderInfo> messageSenderInfos)
    {
        if (conversation.ConversationType == AvaConversationType.Group &&
            reply.SourceGroupId != 0 &&
            reply.SourceGroupId != conversation.GroupId &&
            !string.IsNullOrWhiteSpace(reply.SourceSenderName))
        {
            return reply.SourceSenderName;
        }

        if (reply.SenderId != 0 && reply.SenderId == currentSenderId)
            return currentSenderName ?? reply.SenderId.ToString();

        if (conversation.ConversationType == AvaConversationType.Private &&
            !string.IsNullOrWhiteSpace(reply.SourceSenderName))
        {
            return reply.SourceSenderName;
        }

        if (conversation.ConversationType == AvaConversationType.Private &&
            TryResolvePrivateReplySenderName(reply, messageSenderInfos, out var loadedSenderName))
        {
            return loadedSenderName;
        }

        if (reply.SenderId == 0)
            return string.Empty;

        if (conversation.ConversationType == AvaConversationType.Private)
        {
            if (conversation.PrivateUin != 0 &&
                reply.SenderId == conversation.PrivateUin)
            {
                return conversation.DisplayName;
            }

            return !string.IsNullOrWhiteSpace(reply.SourceSenderName)
                ? reply.SourceSenderName
                : ResolveProfileDisplayName(reply.SenderId, null, fallback: "我");
        }

        return groupSenderNames.TryGetValue(reply.SenderId, out var senderName) &&
               !string.IsNullOrWhiteSpace(senderName)
            ? senderName
            : !string.IsNullOrWhiteSpace(reply.SourceSenderName)
                ? reply.SourceSenderName
                : ResolveProfileDisplayName(reply.SenderId, null, fallback: reply.SenderId.ToString());
    }

    private static bool TryResolvePrivateReplySenderName(
        QQReplyMessage reply,
        IReadOnlyDictionary<long, MessageSenderInfo> messageSenderInfos,
        out string senderName)
    {
        senderName = string.Empty;

        if (reply.MessageSeq2 <= 0)
            return false;

        if (!messageSenderInfos.TryGetValue(reply.MessageSeq2, out var senderInfo) ||
            string.IsNullOrWhiteSpace(senderInfo.Name))
        {
            return false;
        }

        senderName = senderInfo.Name;
        return true;
    }

    private uint ResolveReplySourceGroupId(AvaQQGroup conversation, QQReplyMessage reply)
    {
        if (reply.SourceGroupId == 0)
            return 0;

        if (conversation.ConversationType == AvaConversationType.Group)
        {
            return reply.SourceGroupId == conversation.GroupId
                ? 0
                : reply.SourceGroupId;
        }

        if (conversation.ConversationType != AvaConversationType.Private)
            return 0;

        return reply.SourceGroupId;
    }

    private string ResolveProfileDisplayName(uint uin, string? ntUid, string fallback)
    {
        return GetProfileInfoNameCache().TryGetName(uin, ntUid, out var name)
            ? name
            : fallback;
    }

    private ProfileInfoNameCache GetProfileInfoNameCache()
    {
        var database = _qqDatabaseService.ProfileInfoDatabase;
        if (_profileInfoNames is { } cache &&
            ReferenceEquals(cache.Database, database))
        {
            return cache;
        }

        _profileInfoNames = ProfileInfoNameCache.Create(database);
        return _profileInfoNames;
    }

    private LocalMediaContext CreateLocalMediaContext()
    {
        return _qqDatabaseService.AndroidMessageDatabase is not null &&
               _qqDatabaseService.MessageDatabase is null
            ? new LocalMediaContext(
                DatabasePlatformType.AndroidQQNT,
                null,
                _qqDatabaseService.AndroidMobileQQPath)
            : new LocalMediaContext(
                DatabasePlatformType.QQNT,
                _qqDatabaseService.NtDataPath,
                null);
    }

    private static string ResolveReplySourceGroupName(QQReplyMessage reply)
    {
        return !string.IsNullOrWhiteSpace(reply.SourceGroupName)
            ? reply.SourceGroupName
            : reply.SourceGroupId.ToString();
    }

    private static IReadOnlyList<AvaQQMessage> CreateForwardedMessages(
        MessageRecord item,
        QQMessageContent? content,
        LocalMediaContext mediaContext)
    {
        if (item.SubContent is not { Length: > 0 } subContent ||
            item.MessageType != MessageType.Forwarded && !ContainsForwardedMessageCard(content))
        {
            return [];
        }

        try
        {
            var messages = QQMessageReader.ParseForwardedMessages(subContent);
            return CreateForwardedMessages(messages, mediaContext);
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<AvaQQMessage> CreateForwardedMessages(
        IReadOnlyList<QQForwardedMessage> messages,
        LocalMediaContext mediaContext)
    {
        if (messages.Count == 0)
            return [];

        var senderNames = messages
            .Where(message => message.SenderId != 0)
            .Select(message => new
            {
                message.SenderId,
                Name = message.SendNickName | message.SendMemberName,
            })
            .Where(message => !string.IsNullOrWhiteSpace(message.Name))
            .GroupBy(message => message.SenderId)
            .ToDictionary(message => message.Key, message => message.First().Name!);

        return messages
            .Select(message =>
            {
                var segments = CreateMessageSegments(message, mediaContext);
                if (!HasDisplayContent(segments))
                {
                    segments.Clear();
                    segments.Add(AvaQQMessageSegment.CreateUnsupportedText(CreateUnsupportedForwardedMessageText(message)));
                }

                var senderName = message.SendNickName | message.SendMemberName ?? string.Empty;
                return new AvaQQMessage
                {
                    MessageId = message.MessageId,
                    MessageSeq = message.MessageSeq,
                    MessageRandom = 0,
                    Name = senderName,
                    SenderId = message.SenderId,
                    CachedAvatarUrl = message.AvatarUrl,
                    MessageTime = message.MessageTime,
                    Segments = segments,
                    ForwardedMessages = CreateForwardedMessages(message.NestedForwardedMessages, mediaContext),
                    Reply = CreateReplyMessage(message, senderName, senderNames, mediaContext),
                    DisplayText = CreateDisplayText(segments),
                    IsHoverTimeVisible = true,
                };
            })
            .ToArray();
    }

    private static bool ContainsForwardedMessageCard(QQMessageContent? content)
    {
        if (content is null)
            return false;

        return content.Segments.Any(segment =>
            ForwardedMessageCardParser.TryParse(
                segment.AppJson,
                segment.AppResid,
                segment.AppUniseq,
                segment.Xml,
                segment.XmlResid,
                segment.XmlFileName,
                out var card) &&
            card is not null);
    }

    private static List<AvaQQMessageSegment> CreateMessageSegments(
        QQForwardedMessage item,
        LocalMediaContext mediaContext)
    {
        if (item.Segments.Count == 0 && item.NestedForwardedMessages.Count > 0)
        {
            return [AvaQQMessageSegment.CreateForwardedMessage(CreateForwardedMessageCard(item.NestedForwardedMessages))];
        }

        var segments = new List<AvaQQMessageSegment>();
        foreach (var segment in item.Segments)
        {
            if (TryCreateNestedForwardedMessageSegment(item, segment) is { } nestedForwardedSegment)
            {
                segments.Add(nestedForwardedSegment);
                continue;
            }

            AddMessageSegment(
                segments,
                segment,
                item.MessageType,
                item.SubMessageType,
                item.MessageTime,
                mediaContext,
                CreateUnsupportedForwardedMessageText(item, segment));
        }

        return segments;
    }

    private static AvaQQMessageSegment? TryCreateNestedForwardedMessageSegment(
        QQForwardedMessage item,
        QQMessageSegment segment)
    {
        if (item.NestedForwardedMessages.Count == 0 ||
            SharedContactCardParser.TryParse(segment.AppJson, out _) ||
            ForwardedMessageCardParser.TryParse(
                segment.AppJson,
                segment.AppResid,
                segment.AppUniseq,
                segment.Xml,
                segment.XmlResid,
                segment.XmlFileName,
                out _))
        {
            return null;
        }

        if (segment.Type is not (MessageSegmentType.RichMedia or MessageSegmentType.App or MessageSegmentType.Xml) &&
            item.MessageType != MessageType.Forwarded &&
            !string.Equals(segment.GetDisplayText(), "[聊天记录]", StringComparison.Ordinal))
        {
            return null;
        }

        return AvaQQMessageSegment.CreateForwardedMessage(CreateForwardedMessageCard(item.NestedForwardedMessages));
    }

    private static ForwardedMessageCard CreateForwardedMessageCard(IReadOnlyList<QQForwardedMessage> messages)
    {
        var previewLines = messages
            .Take(3)
            .Select(CreateForwardedMessagePreviewLine)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        return new ForwardedMessageCard(
            "群聊的聊天记录",
            $"查看{messages.Count}条转发消息",
            previewLines,
            null,
            null,
            null,
            messages.Count,
            string.Empty);
    }

    private static string CreateForwardedMessagePreviewLine(QQForwardedMessage message)
    {
        var senderName = message.SendNickName | message.SendMemberName;
        var text = string.Concat(message.Segments.Select(segment => segment.GetDisplayText()));
        if (string.IsNullOrWhiteSpace(text) && message.NestedForwardedMessages.Count > 0)
        {
            text = "[聊天记录]";
        }

        if (string.IsNullOrWhiteSpace(senderName))
            return text;

        return string.IsNullOrWhiteSpace(text)
            ? senderName
            : $"{senderName}: {text}";
    }

    private static AvaReplyMessage? CreateReplyMessage(
        QQForwardedMessage item,
        string senderName,
        IReadOnlyDictionary<uint, string> forwardedSenderNames,
        LocalMediaContext mediaContext)
    {
        var reply = item.Segments
            .Select(segment => segment.Reply)
            .FirstOrDefault(reply => reply is not null);
        if (reply is null)
            return null;

        var replySegments = CreateReplyPreviewSegments(
            item.MessageType,
            item.SubMessageType,
            item.MessageTime,
            reply,
            mediaContext);
        var previewText = CreateDisplayText(replySegments);
        if (string.IsNullOrWhiteSpace(previewText))
        {
            previewText = !string.IsNullOrWhiteSpace(reply.PreviewText)
                ? reply.PreviewText
                : "[原消息]";
        }

        return new AvaReplyMessage
        {
            MessageId = reply.MessageId,
            InternalMessageId = reply.InternalMessageId,
            MessageRandom = reply.MessageRandom,
            MessageSeq = reply.MessageSeq,
            AlternateMessageSeq = reply.MessageSeq2,
            SenderId = reply.SenderId,
            SenderName = ResolveForwardedReplySenderName(item.SenderId, senderName, reply, forwardedSenderNames),
            MessageTime = reply.MessageTime,
            SourceGroupId = reply.SourceGroupId,
            SourceGroupName = reply.SourceGroupName ?? string.Empty,
            Segments = replySegments,
            PreviewText = previewText.Trim(),
        };
    }

    private static long ResolveReplyMessageSeq(long replyToMessageSeq, QQReplyMessage reply)
    {
        if (replyToMessageSeq != 0)
            return replyToMessageSeq;

        if (reply.MessageSeq != 0)
            return reply.MessageSeq;

        return reply.MessageSeq2;
    }

    private static IReadOnlyList<long> GetReplyMessageIdCandidates(AvaReplyMessage reply)
    {
        return new[]
            {
                reply.MessageId,
                reply.InternalMessageId,
            }
            .Where(value => value > 0)
            .Distinct()
            .ToArray();
    }

    private static IReadOnlyList<long> GetReplyMessageRandomCandidates(AvaReplyMessage reply)
    {
        return reply.MessageRandom > 0
            ? [reply.MessageRandom]
            : [];
    }

    private static IReadOnlyList<long> GetReplyMessageSeqCandidates(
        AvaReplyMessage reply,
        AvaConversationType conversationType)
    {
        var candidates = conversationType == AvaConversationType.Private
            ? new[]
            {
                // 私聊的 47402/40850 经常是另一套序号；47419 才是当前 c2c_msg_table 会话内的消息序号。
                reply.AlternateMessageSeq,
                reply.MessageSeq,
            }
            : new[]
            {
                reply.MessageSeq,
                reply.AlternateMessageSeq,
            };

        return candidates
            .Where(value => value > 0)
            .Distinct()
            .ToArray();
    }

    private static string ResolveForwardedReplySenderName(
        uint currentSenderId,
        string currentSenderName,
        QQReplyMessage reply,
        IReadOnlyDictionary<uint, string> forwardedSenderNames)
    {
        if (reply.SourceGroupId != 0 && !string.IsNullOrWhiteSpace(reply.SourceSenderName))
            return reply.SourceSenderName;

        if (reply.SenderId == 0)
            return string.Empty;

        if (reply.SenderId == currentSenderId && !string.IsNullOrWhiteSpace(currentSenderName))
            return currentSenderName;

        return forwardedSenderNames.TryGetValue(reply.SenderId, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : !string.IsNullOrWhiteSpace(reply.SourceSenderName)
                ? reply.SourceSenderName
                : reply.SenderId.ToString();
    }

    private static void AddMessageSegment(
        List<AvaQQMessageSegment> segments,
        QQMessageSegment segment,
        MessageType messageType,
        SubMessageType subMessageType,
        int messageTime,
        LocalMediaContext mediaContext,
        string unsupportedText)
    {
        if (segment.Type == MessageSegmentType.Reply)
            return;

        if (SharedContactCardParser.TryParse(segment.AppJson, out var sharedContact) &&
            sharedContact is not null)
        {
            segments.Add(AvaQQMessageSegment.CreateSharedContact(sharedContact));
            return;
        }

        if (ForwardedMessageCardParser.TryParse(
                segment.AppJson,
                segment.AppResid,
                segment.AppUniseq,
                segment.Xml,
                segment.XmlResid,
                segment.XmlFileName,
                out var forwardedMessage) &&
            forwardedMessage is not null)
        {
            segments.Add(AvaQQMessageSegment.CreateForwardedMessage(forwardedMessage));
            return;
        }

        if (segment.Type == MessageSegmentType.Xml &&
            (!string.IsNullOrWhiteSpace(segment.Xml) ||
             !string.IsNullOrWhiteSpace(segment.XmlResid) ||
             !string.IsNullOrWhiteSpace(segment.XmlFileName)))
        {
            return;
        }

        if (segment.IsQQFace && segment.FaceId is { } faceId)
        {
            var faceSegment = AvaQQMessageSegment.CreateQQFace(faceId);
            segments.Add(string.IsNullOrWhiteSpace(faceSegment.FaceAssetPath)
                ? AvaQQMessageSegment.CreateUnsupportedText(faceSegment.DisplayText)
                : faceSegment);
            return;
        }

        if (segment.IsMarketFace)
        {
            var localPath = ResolveMarketFacePath(mediaContext.NtDataPath, segment);
            var width = LimitMarketFaceSize(segment.MarketFaceWidth);
            var height = LimitMarketFaceSize(segment.MarketFaceHeight);
            var displayText = string.IsNullOrWhiteSpace(segment.MarketFaceName)
                ? "[商城表情]"
                : segment.MarketFaceName;

            segments.Add(string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath)
                ? AvaQQMessageSegment.CreateBrokenImage(
                    width,
                    height,
                    "[商城表情文件未找到]",
                    MarketFaceMaxDisplaySize,
                    MarketFaceMaxDisplaySize)
                : AvaQQMessageSegment.CreateImage(
                    localPath,
                    width,
                    height,
                    displayText,
                    MarketFaceMaxDisplaySize,
                    MarketFaceMaxDisplaySize));
            return;
        }

        if (segment.IsImage || IsStickerMediaSegment(segment))
        {
            var localPath = ResolveLocalMediaPath(mediaContext, messageTime, segment, subMessageType);
            if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
            {
                segments.Add(AvaQQMessageSegment.CreateBrokenImage(
                    segment.ImageWidth,
                    segment.ImageHeight,
                    CreateUnavailableMediaText(subMessageType)));
            }
            else
            {
                segments.Add(AvaQQMessageSegment.CreateImage(
                    localPath,
                    segment.ImageWidth,
                    segment.ImageHeight,
                    IsStickerMessage(subMessageType) ? "[动画表情]" : "[图片]"));
            }

            return;
        }

        var text = segment.GetDisplayText();
        if (!string.IsNullOrEmpty(text))
        {
            if (IsUnsupportedDisplaySegment(segment))
            {
                segments.Add(AvaQQMessageSegment.CreateUnsupportedText(text));
            }
            else
            {
                segments.AddRange(CreateTextSegments(text));
            }

            return;
        }

        segments.Add(AvaQQMessageSegment.CreateUnsupportedText(unsupportedText));
    }

    private static IReadOnlyList<AvaQQMessageSegment> CreateTextSegments(
        string text,
        AvaQQMessageSegmentTone tone = AvaQQMessageSegmentTone.Normal)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var segments = new List<AvaQQMessageSegment>();
        var index = 0;

        foreach (Match match in UrlRegex.Matches(text))
        {
            if (match.Index > index)
            {
                segments.Add(AvaQQMessageSegment.CreateText(text[index..match.Index], tone));
            }

            var urlText = match.Groups["url"].Value.TrimEnd('.', ',', ';', ':', '!', '?', '，', '。', '；', '：', '！', '？');
            var trailingText = match.Groups["url"].Value[urlText.Length..];
            segments.Add(AvaQQMessageSegment.CreateText(urlText, tone, NormalizeUrl(urlText)));

            if (!string.IsNullOrEmpty(trailingText))
            {
                segments.Add(AvaQQMessageSegment.CreateText(trailingText, tone));
            }

            index = match.Index + match.Length;
        }

        if (index < text.Length)
        {
            segments.Add(AvaQQMessageSegment.CreateText(text[index..], tone));
        }

        return segments;
    }

    private static string NormalizeUrl(string url)
    {
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? url
            : $"https://{url}";
    }

    private static bool IsUnsupportedDisplaySegment(QQMessageSegment segment)
    {
        return segment.Type is MessageSegmentType.File
            or MessageSegmentType.Record
            or MessageSegmentType.System
            or MessageSegmentType.App
            or MessageSegmentType.RichMedia
            or MessageSegmentType.Xml
            or MessageSegmentType.Call
            or MessageSegmentType.Dynamic;
    }

    private static string CreateUnavailableMediaText(MessageRecord item)
    {
        return CreateUnavailableMediaText(item.SubMessageType);
    }

    private static string CreateUnavailableMediaText(SubMessageType subMessageType)
    {
        return IsStickerMessage(subMessageType)
            ? "[动画表情文件未找到]"
            : "[图片文件未找到]";
    }

    private static string CreateUnsupportedMessageText(MessageRecord item, QQMessageSegment? segment = null)
    {
        if (item.MessageType is MessageType.System)
        {
            return item.SubMessageType switch
            {
                SubMessageType.MessageRecalled => "[撤回消息]",
                SubMessageType.Nudge => "[互动消息]",
                SubMessageType.Pat => "[拍一拍]",
                _ => "[系统消息]",
            };
        }

        if (item.MessageType is MessageType.GroupFile)
        {
            return item.SubMessageType switch
            {
                SubMessageType.GroupFileImage => "[群文件图片]",
                SubMessageType.GroupFileVideo => "[群文件视频]",
                SubMessageType.GroupFileAudio => "[群文件音频]",
                SubMessageType.GroupFileDocx => "[群文件 DOCX]",
                SubMessageType.GroupFilePptx => "[群文件 PPTX]",
                SubMessageType.GroupFileXlsx => "[群文件 XLSX]",
                SubMessageType.GroupFileZip => "[群文件 ZIP]",
                SubMessageType.GroupFileExe => "[群文件 EXE]",
                _ => "[群文件]",
            };
        }

        var knownText = item.MessageType switch
        {
            MessageType.None => "[空消息/损坏消息]",
            MessageType.Voice => "[语音消息]",
            MessageType.Video => "[视频消息]",
            MessageType.Forwarded => "[合并转发消息]",
            MessageType.Reply => "[回复消息]",
            MessageType.RedPacket => "[红包消息]",
            MessageType.App => "[应用消息]",
            _ => item.SubMessageType switch
            {
                SubMessageType.GroupAnnouncement => "[群公告]",
                SubMessageType.PlatformText => "[平台文本消息]",
                SubMessageType.ContainsLink => "[链接消息]",
                SubMessageType.Sticker => "[动画表情]",
                _ => null,
            },
        };

        if (knownText is not null)
            return knownText;

        var messageType = $"{item.MessageType}({(int)item.MessageType})";
        var subMessageType = $"{item.SubMessageType}({(int)item.SubMessageType})";
        if (segment is null)
            return $"[未支持消息: {messageType}, {subMessageType}]";

        return $"[未支持消息: {messageType}, {subMessageType}, 段类型 {(int)segment.Type}]";
    }

    private static string CreateUnsupportedForwardedMessageText(QQForwardedMessage item, QQMessageSegment? segment = null)
    {
        var messageType = $"{item.MessageType}({(int)item.MessageType})";
        var subMessageType = $"{item.SubMessageType}({(int)item.SubMessageType})";
        if (segment is null)
            return $"[未支持转发消息: {messageType}, {subMessageType}]";

        return $"[未支持转发消息: {messageType}, {subMessageType}, 段类型 {(int)segment.Type}]";
    }

    private static string? ResolveLocalMediaPath(
        LocalMediaContext mediaContext,
        int messageTime,
        QQMessageSegment segment,
        SubMessageType subMessageType)
    {
        if (mediaContext.PlatformType is DatabasePlatformType.AndroidQQNT)
        {
            var androidPath = ResolveAndroidMobileQQImagePath(mediaContext.MobileQQPath, segment);
            if (!string.IsNullOrWhiteSpace(androidPath))
                return androidPath;

            return ResolveExplicitLocalImagePath(segment.ImageLocalPath);
        }

        if (!string.IsNullOrWhiteSpace(mediaContext.NtDataPath))
        {
            var candidateFileNames = CreateMediaFileNameCandidates(segment).ToArray();
            if (candidateFileNames.Length == 0)
                return ResolveExplicitLocalImagePath(segment.ImageLocalPath);

            if (!TryGetMessageUtcMonth(messageTime, out var month))
                return ResolveExplicitLocalImagePath(segment.ImageLocalPath);

            var primaryDir = GetMediaMonthDirectory(mediaContext.NtDataPath, subMessageType, month);
            var result = ResolveMediaPath(primaryDir, candidateFileNames);
            if (result is not null)
                return result;

            // 回退到另一个目录：QQNT 有时会把 sticker 的图片放在 Pic，反之亦然。
            var fallbackDir = IsStickerMessage(subMessageType)
                ? Path.Combine(mediaContext.NtDataPath, "Pic", month)
                : Path.Combine(mediaContext.NtDataPath, "Emoji", "emoji-recv", month);

            result = ResolveMediaPath(fallbackDir, candidateFileNames);
            if (result is not null)
                return result;

            return ResolveExplicitLocalImagePath(segment.ImageLocalPath);
        }

        return ResolveExplicitLocalImagePath(segment.ImageLocalPath);
    }

    private static string? ResolvePCQQImagePath(string? pcqqDataPath, string? imageRelativePath)
    {
        if (string.IsNullOrWhiteSpace(pcqqDataPath) ||
            string.IsNullOrWhiteSpace(imageRelativePath))
        {
            return null;
        }

        var relativePath = NormalizePCQQImageRelativePath(imageRelativePath);
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        var imageRoot = Path.Combine(pcqqDataPath, "Image");
        var localPath = Path.GetFullPath(Path.Combine(imageRoot, relativePath));
        if (File.Exists(localPath))
            return localPath;

        var thumbnailPath = CreatePCQQThumbnailPath(localPath);
        return File.Exists(thumbnailPath) ? thumbnailPath : null;
    }

    private static string? ResolveAndroidMobileQQImagePath(string? mobileQQPath, QQMessageSegment segment)
    {
        if (string.IsNullOrWhiteSpace(mobileQQPath) ||
            segment.ImageMd5 is not { Length: > 0 } imageMd5)
        {
            return null;
        }

        var chatPicPath = Path.Combine(mobileQQPath, "chatpic");
        var md5 = Convert.ToHexString(imageMd5);
        foreach (var folderName in new[] { "chatraw", "chatimg", "chatthumb" })
        {
            var cacheName = CreateAndroidMobileQQImageCacheName(folderName, md5);
            var candidatePath = Path.Combine(chatPicPath, folderName, cacheName[^3..], cacheName);
            if (LocalImageFile.IsDisplayableImageFile(candidatePath))
                return candidatePath;
        }

        return null;
    }

    private static string CreateAndroidMobileQQImageCacheName(string folderName, string md5)
    {
        var crc64 = ComputeAndroidMobileQQImageCacheCrc64($"{folderName}:{md5.ToUpperInvariant()}");
        return "Cache_" + FormatAndroidMobileQQImageCacheCrc64(crc64);
    }

    private static long ComputeAndroidMobileQQImageCacheCrc64(string value)
    {
        var table = AndroidMobileQQImageCacheCrc64Table.Value;
        var crc64 = -1L;
        foreach (var ch in value)
        {
            crc64 = table[((int)ch ^ (int)crc64) & 0xff] ^ (crc64 >> 8);
        }

        return crc64;
    }

    private static long[] CreateAndroidMobileQQImageCacheCrc64Table()
    {
        var table = new long[256];
        for (var i = 0; i < table.Length; i++)
        {
            var value = (long)i;
            for (var j = 0; j < 8; j++)
            {
                value = (value & 1) != 0
                    ? (value >> 1) ^ AndroidMobileQQImageCacheCrc64Polynomial
                    : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    private static string FormatAndroidMobileQQImageCacheCrc64(long value)
    {
        return value < 0
            ? "-" + unchecked((ulong)-value).ToString("x")
            : value.ToString("x");
    }

    private static string NormalizePCQQImageRelativePath(string imageRelativePath)
    {
        var path = imageRelativePath.Trim();
        foreach (var prefix in new[] { "UserDataImage:", "DataImage:" })
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[prefix.Length..];
                break;
            }
        }

        path = path.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path;
    }

    private static string CreatePCQQThumbnailPath(string localPath)
    {
        var directory = Path.GetDirectoryName(localPath);
        var fileName = Path.GetFileNameWithoutExtension(localPath);
        var extension = Path.GetExtension(localPath);
        var thumbnailFileName = string.IsNullOrEmpty(extension)
            ? fileName + "_tmb"
            : fileName + "_tmb" + extension;

        return string.IsNullOrWhiteSpace(directory)
            ? thumbnailFileName
            : Path.Combine(directory, thumbnailFileName);
    }

    private static IEnumerable<string> CreateMediaFileNameCandidates(QQMessageSegment segment)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fileName in CreateImageFileNameCandidates(segment.ImageFileName))
        {
            if (seen.Add(fileName))
                yield return fileName;
        }

        if (segment.ImageMd5 is { Length: > 0 } md5)
        {
            var extension = GetImageExtension(segment.ImageFileName);
            var md5Name = Convert.ToHexString(md5).ToLowerInvariant();
            var md5FileName = string.IsNullOrEmpty(extension) ? md5Name : md5Name + extension;
            if (seen.Add(md5FileName))
                yield return md5FileName;
        }
    }

    private static IEnumerable<string> CreateImageFileNameCandidates(string? imageFileName)
    {
        if (string.IsNullOrWhiteSpace(imageFileName))
            yield break;

        var fileName = Path.GetFileName(imageFileName);
        if (string.IsNullOrWhiteSpace(fileName))
            yield break;

        yield return fileName;

        var lowerFileName = fileName.ToLowerInvariant();
        if (!string.Equals(lowerFileName, fileName, StringComparison.Ordinal))
            yield return lowerFileName;

        var extension = Path.GetExtension(fileName);
        var normalizedName = NormalizeMediaHashName(Path.GetFileNameWithoutExtension(fileName));
        if (!string.IsNullOrWhiteSpace(normalizedName))
        {
            yield return normalizedName + extension;

            var lowerNormalizedFileName = (normalizedName + extension).ToLowerInvariant();
            if (!string.Equals(lowerNormalizedFileName, normalizedName + extension, StringComparison.Ordinal))
                yield return lowerNormalizedFileName;
        }
    }

    private static string NormalizeMediaHashName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var normalized = name.Trim();
        if (normalized.Length >= 2 && normalized[0] == '{' && normalized[^1] == '}')
            normalized = normalized[1..^1];

        normalized = normalized.Replace("-", string.Empty, StringComparison.Ordinal);
        return normalized.Length > 0 && normalized.All(char.IsAsciiHexDigit)
            ? normalized.ToLowerInvariant()
            : string.Empty;
    }

    private static string GetImageExtension(string? imageFileName)
    {
        return string.IsNullOrWhiteSpace(imageFileName)
            ? string.Empty
            : Path.GetExtension(Path.GetFileName(imageFileName)).ToLowerInvariant();
    }

    private static string GetMediaMonthDirectory(string ntDataPath, SubMessageType subMessageType, string month)
    {
        return IsStickerMessage(subMessageType)
            ? Path.Combine(ntDataPath, "Emoji", "emoji-recv", month)
            : Path.Combine(ntDataPath, "Pic", month);
    }

    private static bool TryGetMessageUtcMonth(int messageTime, out string month)
    {
        if (messageTime <= 0)
        {
            month = string.Empty;
            return false;
        }

        try
        {
            month = DateTimeOffset.FromUnixTimeSeconds(messageTime)
                .UtcDateTime
                .ToString("yyyy-MM");
            return true;
        }
        catch
        {
            month = string.Empty;
            return false;
        }
    }

    private const int MarketFaceMaxDisplaySize = 200;

    private static string? ResolveMarketFacePath(string? ntDataPath, QQMessageSegment segment)
    {
        if (string.IsNullOrWhiteSpace(ntDataPath) ||
            segment.MarketFacePackageId is not { } packageId)
        {
            return null;
        }

        var packageDirectory = Path.Combine(ntDataPath, "Emoji", "marketface", packageId.ToString());
        if (!Directory.Exists(packageDirectory))
            return null;

        var imageId = segment.MarketFaceImageId;
        var result = string.IsNullOrWhiteSpace(imageId)
            ? null
            : ResolveMarketFaceImagePath(packageDirectory, imageId);
        if (result is not null)
            return result;

        imageId = ResolveMarketFaceImageIdFromMetadata(ntDataPath, packageId, segment.MarketFaceName);
        return string.IsNullOrWhiteSpace(imageId)
            ? null
            : ResolveMarketFaceImagePath(packageDirectory, imageId);
    }

    private static string? ResolveMarketFaceImagePath(string packageDirectory, string imageId)
    {
        var normalizedImageId = NormalizeMarketFaceImageId(imageId);
        if (string.IsNullOrWhiteSpace(normalizedImageId))
            return null;

        var originalPath = ResolveExistingFile(packageDirectory, normalizedImageId);
        if (LocalImageFile.IsDisplayableImageFile(originalPath))
            return originalPath;

        // 商城表情只认 nt_data/Emoji/marketface/<表情包 ID>/<图片 ID> 这个本体文件；
        // 同目录里的静态预览图不能拿来替代动图本体。
        return null;
    }

    private static string? ResolveMarketFaceImageIdFromMetadata(
        string ntDataPath,
        int packageId,
        string? marketFaceName)
    {
        var normalizedName = NormalizeMarketFaceName(marketFaceName);
        if (string.IsNullOrWhiteSpace(normalizedName))
            return null;

        var metadataPath = Path.Combine(ntDataPath, "Emoji", "marketface", "json", $"{packageId}.jtmp");
        if (!File.Exists(metadataPath))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            if (!document.RootElement.TryGetProperty("imgs", out var images) ||
                images.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var image in images.EnumerateArray())
            {
                if (!IsMatchingMarketFaceName(image, normalizedName))
                    continue;

                if (TryGetMarketFaceImageId(image, out var imageId))
                    return imageId;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool TryGetMarketFaceImageId(JsonElement image, out string imageId)
    {
        imageId = string.Empty;
        if (!image.TryGetProperty("id", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        imageId = NormalizeMarketFaceImageId(idElement.GetString());
        return !string.IsNullOrWhiteSpace(imageId);
    }

    private static bool IsMatchingMarketFaceName(JsonElement image, string normalizedName)
    {
        if (image.TryGetProperty("name", out var nameElement) &&
            nameElement.ValueKind == JsonValueKind.String &&
            string.Equals(NormalizeMarketFaceName(nameElement.GetString()), normalizedName, StringComparison.Ordinal))
        {
            return true;
        }

        if (!image.TryGetProperty("keywords", out var keywordsElement) ||
            keywordsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return keywordsElement
            .EnumerateArray()
            .Any(keyword =>
                keyword.ValueKind == JsonValueKind.String &&
                string.Equals(NormalizeMarketFaceName(keyword.GetString()), normalizedName, StringComparison.Ordinal));
    }

    private static string NormalizeMarketFaceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var normalized = name.Trim();
        while (normalized.Length >= 2 &&
               ((normalized[0] == '[' && normalized[^1] == ']') ||
                (normalized[0] == '【' && normalized[^1] == '】')))
        {
            normalized = normalized[1..^1].Trim();
        }

        return normalized;
    }

    private static string NormalizeMarketFaceImageId(string? imageId)
    {
        if (string.IsNullOrWhiteSpace(imageId))
            return string.Empty;

        return Path.GetFileNameWithoutExtension(imageId.Trim()).ToLowerInvariant();
    }

    private static int LimitMarketFaceSize(int? value)
    {
        return value is > 0
            ? Math.Min(value.Value, MarketFaceMaxDisplaySize)
            : MarketFaceMaxDisplaySize;
    }

    private static string? ResolveOriginalImagePath(string picMonthDirectory, string imageFileName)
    {
        var oriDirectory = Path.Combine(picMonthDirectory, "Ori");
        return ResolveExistingFile(oriDirectory, imageFileName) ??
               ResolveExistingFile(oriDirectory, imageFileName.ToLowerInvariant());
    }

    private static string? ResolveMediaPath(string mediaMonthDirectory, IReadOnlyList<string> candidateFileNames)
    {
        foreach (var imageFileName in candidateFileNames)
        {
            var result = ResolveOriginalImagePath(mediaMonthDirectory, imageFileName);
            if (result is not null)
                return result;
        }

        foreach (var imageFileName in candidateFileNames)
        {
            var result = ResolveThumbnailImagePath(mediaMonthDirectory, imageFileName);
            if (result is not null)
                return result;
        }

        return null;
    }

    private static string? ResolveThumbnailImagePath(string picMonthDirectory, string imageFileName)
    {
        var thumbDirectory = Path.Combine(picMonthDirectory, "Thumb");
        if (!Directory.Exists(thumbDirectory))
            return null;

        var imageName = Path.GetFileNameWithoutExtension(imageFileName);
        if (string.IsNullOrWhiteSpace(imageName))
            return null;

        var preferredExtension = Path.GetExtension(imageFileName);
        var thumbNamePrefix = imageName.ToLowerInvariant();
        var candidates = Directory.EnumerateFiles(thumbDirectory, $"{thumbNamePrefix}_*.*")
            .Select(path => CreateThumbnailCandidate(path, thumbNamePrefix, preferredExtension))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!.Value)
            .ToList();

        // QQNT 的 Thumb 图片命名是 <图片hash>_<规格>.扩展名。优先使用 _0 作为聊天预览图；
        // 如果 _0 不存在，就取数字规格最大的文件，例如 _720。
        return candidates
                   .Where(candidate => candidate.Spec == 0)
                   .OrderByDescending(candidate => candidate.MatchesPreferredExtension)
                   .Select(candidate => candidate.Path)
                   .FirstOrDefault()
               ?? candidates
                   .OrderByDescending(candidate => candidate.Spec)
                   .ThenByDescending(candidate => candidate.MatchesPreferredExtension)
                   .Select(candidate => candidate.Path)
                   .FirstOrDefault();
    }

    private static ThumbnailCandidate? CreateThumbnailCandidate(
        string path,
        string thumbNamePrefix,
        string preferredExtension)
    {
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
        if (!fileNameWithoutExtension.StartsWith($"{thumbNamePrefix}_", StringComparison.OrdinalIgnoreCase))
            return null;

        var specText = fileNameWithoutExtension[(thumbNamePrefix.Length + 1)..];
        if (!int.TryParse(specText, out var spec))
            return null;

        var matchesPreferredExtension = string.IsNullOrEmpty(preferredExtension) ||
                                        string.Equals(Path.GetExtension(path), preferredExtension, StringComparison.OrdinalIgnoreCase);
        return new ThumbnailCandidate(path, spec, matchesPreferredExtension);
    }

    private static string? ResolveExplicitLocalImagePath(string? imageLocalPath)
    {
        return !string.IsNullOrWhiteSpace(imageLocalPath) && File.Exists(imageLocalPath)
            ? imageLocalPath
            : null;
    }

    private static string? ResolveExistingFile(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        return File.Exists(path) ? path : null;
    }

    private static bool IsStickerMessage(MessageRecord item)
    {
        return IsStickerMessage(item.SubMessageType);
    }

    private static bool IsStickerMessage(SubMessageType subMessageType)
    {
        return subMessageType is SubMessageType.Sticker;
    }

    private static bool IsStickerMediaSegment(MessageRecord item, QQMessageSegment segment)
    {
        return IsStickerMediaSegment(segment);
    }

    private static bool IsStickerMediaSegment(QQMessageSegment segment)
    {
        return !string.IsNullOrWhiteSpace(segment.ImageFileName) ||
               !string.IsNullOrWhiteSpace(segment.ImageLocalPath);
    }

    private readonly record struct ThumbnailCandidate(string Path, int Spec, bool MatchesPreferredExtension);

    private void OnDatabaseAdded(IQQDatabase database)
    {
        if (database is QQMessageReader messageDatabase)
        {
            LoadMessageConversations(messageDatabase);
            RefreshFilteredGroups();
        }
        else if (database is QQAndroidMessageReader androidMessageDatabase)
        {
            LoadAndroidMessageConversations(androidMessageDatabase);
            RefreshFilteredGroups();
        }
        else if (database is PCQQMessageReader pcqqMessageDatabase)
        {
            LoadPCQQMessageConversations(pcqqMessageDatabase);
            RefreshFilteredGroups();
        }
        else if (database is QQGroupInfoReader groupDatabase)
        {
            var rawGroups = groupDatabase.DbContext.GroupList
                .Where(v => v.GroupId != 0)
                .ToList();

            // 给现有的Groups加上群名
            foreach (var item in _groups
                         .Where(group => group.ConversationType == AvaConversationType.Group)
                         .Join(rawGroups,
                v => v.GroupId,
                v => v.GroupId,
                (group, rawGroup) => (group, rawGroup)))
            {
                item.group.GroupName = item.rawGroup.GroupName;
            }

            // 新增的Groups
            var existingGroupIds = _groups
                .Where(group => group.ConversationType == AvaConversationType.Group)
                .Select(group => group.GroupId);
            var newGroups = rawGroups.ExceptBy(existingGroupIds, v => v.GroupId)
                .Select(v => new AvaQQGroup()
                {
                    ConversationType = AvaConversationType.Group,
                    GroupId = v.GroupId,
                    GroupName = v.GroupName,
                    IsSelected = _selectedConversationKeys.Contains($"group:{v.GroupId}"),
                    IsActive = _activeConversationKey == $"group:{v.GroupId}",
                });
            _groups.AddRange(newGroups);
            RefreshFilteredGroups();
        }
        else if (database is QQProfileInfoReader)
        {
            _profileInfoNames = null;
            ApplyProfileInfoNames();
        }
    }

    private void OnAppSettingsChanged(object? sender, EventArgs e)
    {
        foreach (var message in _messages)
        {
            message.IsHoverTimeVisible = AlwaysShowMessageTime;
        }
    }

    private AvaQQGroup GetOrCreateGroup(uint groupId)
    {
        var group = _groups.FirstOrDefault(v =>
            v.ConversationType == AvaConversationType.Group &&
            v.GroupId == groupId);
        if (group is not null)
            return group;

        group = new AvaQQGroup
        {
            ConversationType = AvaConversationType.Group,
            GroupId = groupId,
        };
        _groups.Add(group);
        return group;
    }

    private AvaQQGroup GetOrCreatePrivateConversation(long conversationId)
    {
        var conversation = _groups.FirstOrDefault(v =>
            v.ConversationType == AvaConversationType.Private &&
            v.PrivateConversationId == conversationId);
        if (conversation is not null)
            return conversation;

        conversation = new AvaQQGroup
        {
            ConversationType = AvaConversationType.Private,
            PrivateConversationId = conversationId,
        };
        _groups.Add(conversation);
        return conversation;
    }

    private void LoadMessageConversations(QQMessageReader messageDatabase)
    {
        var recentContacts = ReadRecentConversationContacts(messageDatabase);
        var profileInfoNames = GetProfileInfoNameCache();
        foreach (var contact in recentContacts)
        {
            var conversation = contact.ConversationType switch
            {
                AvaConversationType.Group when contact.GroupId != 0 => GetOrCreateGroup(contact.GroupId),
                AvaConversationType.Private when contact.PrivateConversationId != 0 => GetOrCreatePrivateConversation(contact.PrivateConversationId),
                _ => null,
            };
            if (conversation is null)
                continue;

            conversation.PrivateUin = contact.PrivateUin;
            conversation.PrivateUid = contact.PrivateUid;
            conversation.GroupName = contact.ConversationType == AvaConversationType.Private &&
                                     profileInfoNames.TryGetName(contact.PrivateUin, contact.PrivateUid, out var profileName)
                ? profileName
                : FirstNonEmpty(contact.DisplayName, conversation.GroupName);
            conversation.LatestMessageText = CreateLatestMessageText(contact);
            conversation.LatestMessageTime = contact.LastTime;
            conversation.IsSelected = _selectedConversationKeys.Contains(conversation.ConversationKey);
            conversation.IsActive = _activeConversationKey == conversation.ConversationKey;
        }

        foreach (var groupId in messageDatabase.DbContext.GroupMessages
                     .Select(v => v.GroupId)
                     .Where(v => v != 0)
                     .Distinct()
                     .ToList())
        {
            var group = GetOrCreateGroup(groupId);
            group.IsSelected = _selectedConversationKeys.Contains(group.ConversationKey);
            group.IsActive = _activeConversationKey == group.ConversationKey;
        }

        foreach (var conversationInfo in messageDatabase.DbContext.PrivateMessages
                     .Where(message => message.ConversationId != 0)
                     .Where(message => message.MessageType != MessageType.Empty)
                     .GroupBy(message => message.ConversationId)
                     .Select(group => new
                     {
                         ConversationId = group.Key,
                         PrivateUid = group.Max(message => message.PeerUid),
                         PrivateUin = group.Max(message => message.PeerUin),
                         LastTime = group.Max(message => message.MessageTime),
                     })
                     .ToList())
        {
            var conversation = GetOrCreatePrivateConversation(conversationInfo.ConversationId);
            conversation.PrivateUid = conversationInfo.PrivateUid;
            conversation.PrivateUin = conversationInfo.PrivateUin;
            if (profileInfoNames.TryGetName(conversation.PrivateUin, conversation.PrivateUid, out var profileName))
            {
                conversation.GroupName = profileName;
            }
            conversation.LatestMessageTime = conversationInfo.LastTime;
            conversation.IsSelected = _selectedConversationKeys.Contains(conversation.ConversationKey);
            conversation.IsActive = _activeConversationKey == conversation.ConversationKey;
        }
    }

    private void LoadAndroidMessageConversations(QQAndroidMessageReader messageDatabase)
    {
        var recentContacts = ReadAndroidRecentConversationContacts(messageDatabase);
        var profileInfoNames = GetProfileInfoNameCache();
        foreach (var contact in recentContacts)
        {
            var conversation = contact.ConversationType switch
            {
                AvaConversationType.Group when contact.GroupId != 0 => GetOrCreateGroup(contact.GroupId),
                AvaConversationType.Private when contact.PrivateConversationId != 0 => GetOrCreatePrivateConversation(contact.PrivateConversationId),
                _ => null,
            };
            if (conversation is null)
                continue;

            conversation.PrivateUin = contact.PrivateUin;
            conversation.PrivateUid = contact.PrivateUid;
            conversation.GroupName = contact.ConversationType == AvaConversationType.Private &&
                                     profileInfoNames.TryGetName(contact.PrivateUin, contact.PrivateUid, out var profileName)
                ? profileName
                : FirstNonEmpty(contact.DisplayName, conversation.GroupName);
            conversation.LatestMessageText = CreateLatestMessageText(contact);
            conversation.LatestMessageTime = contact.LastTime;
            conversation.IsSelected = _selectedConversationKeys.Contains(conversation.ConversationKey);
            conversation.IsActive = _activeConversationKey == conversation.ConversationKey;
        }

        foreach (var groupId in messageDatabase.DbContext.GroupMessages
                     .Select(v => v.GroupId)
                     .Where(v => v != 0)
                     .Distinct()
                     .ToList())
        {
            var group = GetOrCreateGroup(groupId);
            group.IsSelected = _selectedConversationKeys.Contains(group.ConversationKey);
            group.IsActive = _activeConversationKey == group.ConversationKey;
        }

        foreach (var conversationInfo in messageDatabase.DbContext.PrivateMessages
                     .Where(message => message.ConversationId != 0)
                     .Where(message => message.MessageType != MessageType.Empty)
                     .GroupBy(message => message.ConversationId)
                     .Select(group => new
                     {
                         ConversationId = group.Key,
                         PrivateUid = group.Max(message => message.PeerUid),
                         PrivateUin = group.Max(message => message.PeerUin),
                         LastTime = group.Max(message => message.MessageTime),
                     })
                     .ToList())
        {
            var conversation = GetOrCreatePrivateConversation(conversationInfo.ConversationId);
            conversation.PrivateUid = conversationInfo.PrivateUid;
            conversation.PrivateUin = conversationInfo.PrivateUin;
            if (profileInfoNames.TryGetName(conversation.PrivateUin, conversation.PrivateUid, out var profileName))
            {
                conversation.GroupName = profileName;
            }
            conversation.LatestMessageTime = conversationInfo.LastTime;
            conversation.IsSelected = _selectedConversationKeys.Contains(conversation.ConversationKey);
            conversation.IsActive = _activeConversationKey == conversation.ConversationKey;
        }
    }

    private void LoadPCQQMessageConversations(PCQQMessageReader messageDatabase)
    {
        foreach (var item in messageDatabase.GetConversations())
        {
            var conversation = item.ConversationType switch
            {
                PCQQConversationType.Group when item.PeerId != 0 => GetOrCreatePCQQGroup(item.PeerId),
                PCQQConversationType.Private when item.PeerId != 0 => GetOrCreatePCQQPrivateConversation(item.PeerId),
                _ => null,
            };
            if (conversation is null)
                continue;

            conversation.PCQQTableName = item.TableName;
            if (item.ConversationType == PCQQConversationType.Group &&
                !string.IsNullOrWhiteSpace(item.DisplayNameOverride))
            {
                conversation.GroupName = item.DisplayNameOverride;
                conversation.PCQQHasInfo = item.InfoGroupId != 0 || item.InfoGroupCode != 0;
            }
            else if (item.ConversationType == PCQQConversationType.Private &&
                     !string.IsNullOrWhiteSpace(item.DisplayNameOverride))
            {
                conversation.GroupName = item.DisplayNameOverride;
            }

            conversation.LatestMessageText = CreatePCQQLatestMessageText(
                item.ConversationType,
                item.LatestMessageText,
                item.LatestMessageSenderUin,
                item.LatestMessageSenderNickname);
            conversation.LatestMessageTime = ClampUnixTime(item.LatestMessageTime);
            conversation.IsSelected = _selectedConversationKeys.Contains(conversation.ConversationKey);
            conversation.IsActive = _activeConversationKey == conversation.ConversationKey;
        }
    }

    private void ApplyProfileInfoNames()
    {
        var profileInfoNames = GetProfileInfoNameCache();
        var changed = false;

        foreach (var conversation in _groups.Where(group => group.ConversationType == AvaConversationType.Private))
        {
            if (profileInfoNames.TryGetName(conversation.PrivateUin, conversation.PrivateUid, out var profileName) &&
                !string.Equals(conversation.GroupName, profileName, StringComparison.Ordinal))
            {
                conversation.GroupName = profileName;
                changed = true;
            }
        }

        if (changed)
        {
            RefreshFilteredGroups();
        }

        RefreshVisiblePrivateMessageSenderNames();
    }

    private void RefreshVisiblePrivateMessageSenderNames()
    {
        var selectedGroup = SelectedGroup;
        if (selectedGroup?.ConversationType != AvaConversationType.Private)
            return;

        var senderNames = GetSenderNameCache(selectedGroup.ConversationKey);
        foreach (var message in _messages)
        {
            if (message.ConversationType != AvaConversationType.Private ||
                message.IsSystemHint ||
                message.SenderId == 0 ||
                IsPrivatePeerMessage(message, selectedGroup))
            {
                continue;
            }

            var name = ResolveProfileDisplayName(message.SenderId, null, fallback: string.Empty);
            if (string.IsNullOrWhiteSpace(name) ||
                string.Equals(message.Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            message.Name = name;
            senderNames[message.SenderId] = name;
        }
    }

    private AvaQQGroup GetOrCreatePCQQGroup(uint groupId)
    {
        var group = _groups.FirstOrDefault(v =>
            v.ConversationType == AvaConversationType.PCQQGroup &&
            v.GroupId == groupId);
        if (group is not null)
            return group;

        group = new AvaQQGroup
        {
            ConversationType = AvaConversationType.PCQQGroup,
            GroupId = groupId,
        };
        _groups.Add(group);
        return group;
    }

    private AvaQQGroup GetOrCreatePCQQPrivateConversation(uint privateUin)
    {
        var conversation = _groups.FirstOrDefault(v =>
            v.ConversationType == AvaConversationType.PCQQPrivate &&
            v.PrivateUin == privateUin);
        if (conversation is not null)
            return conversation;

        conversation = new AvaQQGroup
        {
            ConversationType = AvaConversationType.PCQQPrivate,
            PrivateUin = privateUin,
        };
        _groups.Add(conversation);
        return conversation;
    }

    private static IReadOnlyList<RecentConversationContact> ReadRecentConversationContacts(QQMessageReader messageDatabase)
    {
        try
        {
            var recentContacts = messageDatabase.DbContext.RecentContacts
                .Where(v => v.ChatType == ChatType.GroupMessage || v.ChatType == ChatType.PrivateMessage)
                .OrderByDescending(v => v.SortTime == 0 ? v.LastTime : v.SortTime)
                .Select(v => new
                {
                    v.ChatType,
                    v.PeerUin,
                    v.Uin,
                    v.LastMessage,
                    v.LastTime,
                    v.MessageSeq,
                    v.MessageRandom,
                    v.SortTime,
                    v.Source,
                    v.SendremarkName,
                    v.SendMemberName,
                    v.SendNickName,
                    v.Uin2,
                    v.NtUid,
                })
                .ToList();

            var recentGroupMessageKeys = recentContacts
                .Where(contact => contact.ChatType == ChatType.GroupMessage)
                .Select(contact => TryParseRecentGroupId(contact.PeerUin, out var groupId)
                    ? new RecentGroupMessageKey(groupId, 0, contact.MessageSeq, contact.MessageRandom)
                    : (RecentGroupMessageKey?)null)
                .Where(key => key is not null)
                .Select(key => key!.Value)
                .ToList();
            var recentGroupMessages = ReadRecentGroupMessageMatches(messageDatabase, recentGroupMessageKeys);

            var recentPrivateMessageKeys = recentContacts
                .Where(contact => contact.ChatType == ChatType.PrivateMessage)
                .Where(contact => !string.IsNullOrWhiteSpace(contact.PeerUin))
                .Select(contact => new RecentPrivateMessageKey(contact.PeerUin!, 0, contact.MessageSeq, contact.MessageRandom))
                .ToList();
            var recentPrivateMessages = ReadRecentPrivateMessageMatches(messageDatabase, recentPrivateMessageKeys);

            return recentContacts
                .Select(contact =>
                {
                    if (contact.ChatType == ChatType.GroupMessage)
                    {
                        var groupId = TryParseRecentGroupId(contact.PeerUin, out var parsedGroupId)
                            ? parsedGroupId
                            : 0;
                        var messageKey = new RecentGroupMessageKey(
                            groupId,
                            0,
                            contact.MessageSeq,
                            contact.MessageRandom);
                        var latestMessage = recentGroupMessages.TryGetValue(messageKey, out var matchedMessage)
                            ? matchedMessage
                            : (MessageRecord?)null;

                        return new RecentConversationContact(
                            AvaConversationType.Group,
                            groupId,
                            0,
                            0,
                            null,
                            contact.Source,
                            contact.LastMessage,
                            contact.LastTime,
                            contact.Uin2,
                            contact.NtUid,
                            contact.SendremarkName,
                            contact.SendMemberName,
                            contact.SendNickName,
                            latestMessage);
                    }

                    if (!string.IsNullOrWhiteSpace(contact.PeerUin))
                    {
                        var messageKey = new RecentPrivateMessageKey(
                            contact.PeerUin,
                            0,
                            contact.MessageSeq,
                            contact.MessageRandom);
                        if (!recentPrivateMessages.TryGetValue(messageKey, out var privateMessage))
                            return null;

                        return new RecentConversationContact(
                            AvaConversationType.Private,
                            0,
                            privateMessage.ConversationId,
                            privateMessage.PrivateUin != 0 ? privateMessage.PrivateUin : contact.Uin,
                            contact.PeerUin,
                            FirstNonEmpty(contact.SendremarkName, contact.Source, contact.SendNickName, contact.SendMemberName),
                            contact.LastMessage,
                            contact.LastTime != 0 ? contact.LastTime : privateMessage.LastTime,
                            contact.Uin2,
                            contact.NtUid,
                            contact.SendremarkName,
                            contact.SendMemberName,
                            contact.SendNickName,
                            privateMessage.LatestMessage);
                    }

                    return null;
                })
                .Where(contact => contact is not null)
                .Select(contact => contact!)
                .Where(contact =>
                    contact.ConversationType == AvaConversationType.Group && contact.GroupId != 0 ||
                    contact.ConversationType == AvaConversationType.Private && contact.PrivateConversationId != 0)
                .DistinctBy(contact => contact.ConversationKey)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<RecentConversationContact> ReadAndroidRecentConversationContacts(
        QQAndroidMessageReader messageDatabase)
    {
        try
        {
            var recentContacts = messageDatabase.DbContext.RecentContacts
                .Where(v => v.ChatType == ChatType.GroupMessage || v.ChatType == ChatType.PrivateMessage)
                .OrderByDescending(v => v.SortTime == 0 ? v.LastTime : v.SortTime)
                .Select(v => new
                {
                    v.LastMessageId,
                    v.ChatType,
                    v.PeerUin,
                    v.Uin,
                    v.LastMessage,
                    v.LastTime,
                    v.MessageSeq,
                    v.SortTime,
                    v.Source,
                    v.SendremarkName,
                    v.SendMemberName,
                    v.SendNickName,
                    v.Uin2,
                    v.NtUid,
                })
                .ToList();

            var recentGroupMessageKeys = recentContacts
                .Where(contact => contact.ChatType == ChatType.GroupMessage)
                .Select(contact => TryParseRecentGroupId(contact.PeerUin, out var groupId)
                    ? new RecentGroupMessageKey(groupId, contact.LastMessageId, contact.MessageSeq, 0)
                    : (RecentGroupMessageKey?)null)
                .Where(key => key is not null)
                .Select(key => key!.Value)
                .ToList();
            var recentGroupMessages = ReadAndroidRecentGroupMessageMatches(messageDatabase, recentGroupMessageKeys);

            var recentPrivateMessageKeys = recentContacts
                .Where(contact => contact.ChatType == ChatType.PrivateMessage)
                .Where(contact => !string.IsNullOrWhiteSpace(contact.PeerUin))
                .Select(contact => new RecentPrivateMessageKey(contact.PeerUin!, contact.LastMessageId, contact.MessageSeq, 0))
                .ToList();
            var recentPrivateMessages = ReadAndroidRecentPrivateMessageMatches(messageDatabase, recentPrivateMessageKeys);

            return recentContacts
                .Select(contact =>
                {
                    if (contact.ChatType == ChatType.GroupMessage)
                    {
                        var groupId = TryParseRecentGroupId(contact.PeerUin, out var parsedGroupId)
                            ? parsedGroupId
                            : 0;
                        var messageKey = new RecentGroupMessageKey(
                            groupId,
                            contact.LastMessageId,
                            contact.MessageSeq,
                            0);
                        var latestMessage = recentGroupMessages.TryGetValue(messageKey, out var matchedMessage)
                            ? matchedMessage
                            : (MessageRecord?)null;

                        return new RecentConversationContact(
                            AvaConversationType.Group,
                            groupId,
                            0,
                            0,
                            null,
                            contact.Source,
                            contact.LastMessage,
                            contact.LastTime,
                            contact.Uin2,
                            contact.NtUid,
                            contact.SendremarkName,
                            contact.SendMemberName,
                            contact.SendNickName,
                            latestMessage);
                    }

                    if (!string.IsNullOrWhiteSpace(contact.PeerUin))
                    {
                        var messageKey = new RecentPrivateMessageKey(
                            contact.PeerUin,
                            contact.LastMessageId,
                            contact.MessageSeq,
                            0);
                        if (!recentPrivateMessages.TryGetValue(messageKey, out var privateMessage))
                            return null;

                        return new RecentConversationContact(
                            AvaConversationType.Private,
                            0,
                            privateMessage.ConversationId,
                            privateMessage.PrivateUin != 0 ? privateMessage.PrivateUin : contact.Uin,
                            contact.PeerUin,
                            FirstNonEmpty(contact.SendremarkName, contact.Source, contact.SendNickName, contact.SendMemberName),
                            contact.LastMessage,
                            contact.LastTime != 0 ? contact.LastTime : privateMessage.LastTime,
                            contact.Uin2,
                            contact.NtUid,
                            contact.SendremarkName,
                            contact.SendMemberName,
                            contact.SendNickName,
                            privateMessage.LatestMessage);
                    }

                    return null;
                })
                .Where(contact => contact is not null)
                .Select(contact => contact!)
                .Where(contact =>
                    contact.ConversationType == AvaConversationType.Group && contact.GroupId != 0 ||
                    contact.ConversationType == AvaConversationType.Private && contact.PrivateConversationId != 0)
                .DistinctBy(contact => contact.ConversationKey)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyDictionary<RecentGroupMessageKey, MessageRecord> ReadRecentGroupMessageMatches(
        QQMessageReader messageDatabase,
        IReadOnlyCollection<RecentGroupMessageKey> messageKeys)
    {
        if (messageKeys.Count == 0)
            return new Dictionary<RecentGroupMessageKey, MessageRecord>();

        var result = new Dictionary<RecentGroupMessageKey, MessageRecord>();
        foreach (var key in messageKeys.Distinct())
        {
            var message = FindRecentGroupMessageCandidate(messageDatabase, key);
            if (message is { } matchedMessage)
            {
                result[key] = matchedMessage;
            }
        }

        return result;
    }

    private static MessageRecord? FindRecentGroupMessageCandidate(QQMessageReader messageDatabase, RecentGroupMessageKey key)
    {
        if (key.GroupId == 0 || key.MessageId == 0 && key.MessageSeq == 0 && key.MessageRandom == 0)
            return null;

        var query = messageDatabase.DbContext.GroupMessages
            .Where(message => message.GroupId == key.GroupId)
            .Where(message => message.MessageType != MessageType.Empty);

        if (key.MessageId != 0)
        {
            var idMessage = query
                .Where(message => message.MessageId == key.MessageId)
                .Take(1)
                .AsEnumerable()
                .Select(message => (MessageRecord?)MessageRecord.FromGroup(message))
                .FirstOrDefault();
            if (idMessage is not null)
                return idMessage;
        }

        if (key.MessageSeq != 0 && key.MessageRandom != 0)
        {
            var exactMessage = query
                .Where(message => message.MessageSeq == key.MessageSeq &&
                                  message.MessageRandom == key.MessageRandom)
                .OrderByDescending(message => message.MessageSeq)
                .ThenByDescending(message => message.MessageId)
                .Take(1)
                .AsEnumerable()
                .Select(message => (MessageRecord?)MessageRecord.FromGroup(message))
                .FirstOrDefault();
            if (exactMessage is not null)
                return exactMessage;
        }

        if (key.MessageRandom != 0)
        {
            var randomMessage = query
                .Where(message => message.MessageRandom == key.MessageRandom)
                .OrderByDescending(message => message.MessageSeq)
                .ThenByDescending(message => message.MessageId)
                .Take(1)
                .AsEnumerable()
                .Select(message => (MessageRecord?)MessageRecord.FromGroup(message))
                .FirstOrDefault();
            if (randomMessage is not null)
                return randomMessage;
        }

        if (key.MessageSeq == 0)
            return null;

        return query
            .Where(message => message.MessageSeq == key.MessageSeq)
            .OrderByDescending(message => message.MessageId)
            .Take(1)
            .AsEnumerable()
            .Select(message => (MessageRecord?)MessageRecord.FromGroup(message))
            .FirstOrDefault();
    }

    private static IReadOnlyDictionary<RecentPrivateMessageKey, RecentPrivateMessageMatch> ReadRecentPrivateMessageMatches(
        QQMessageReader messageDatabase,
        IReadOnlyCollection<RecentPrivateMessageKey> messageKeys)
    {
        if (messageKeys.Count == 0)
            return new Dictionary<RecentPrivateMessageKey, RecentPrivateMessageMatch>();

        var result = new Dictionary<RecentPrivateMessageKey, RecentPrivateMessageMatch>();
        foreach (var key in messageKeys.Distinct())
        {
            var latestMessage = FindRecentPrivateMessageCandidate(messageDatabase, key);
            var conversationMessage = latestMessage ?? FindLatestPrivateConversationMessage(messageDatabase, key.PeerUid);
            if (conversationMessage is not { } message || message.PrivateConversationId == 0)
                continue;

            result[key] = new RecentPrivateMessageMatch(
                message.PrivateConversationId,
                message.PeerUin,
                message.MessageTime,
                latestMessage);
        }

        return result;
    }

    private static IReadOnlyDictionary<RecentGroupMessageKey, MessageRecord> ReadAndroidRecentGroupMessageMatches(
        QQAndroidMessageReader messageDatabase,
        IReadOnlyCollection<RecentGroupMessageKey> messageKeys)
    {
        if (messageKeys.Count == 0)
            return new Dictionary<RecentGroupMessageKey, MessageRecord>();

        var result = new Dictionary<RecentGroupMessageKey, MessageRecord>();
        foreach (var key in messageKeys.Distinct())
        {
            var message = FindAndroidRecentGroupMessageCandidate(messageDatabase, key);
            if (message is { } matchedMessage)
            {
                result[key] = matchedMessage;
            }
        }

        return result;
    }

    private static MessageRecord? FindAndroidRecentGroupMessageCandidate(
        QQAndroidMessageReader messageDatabase,
        RecentGroupMessageKey key)
    {
        if (key.GroupId == 0 || key.MessageId == 0 && key.MessageSeq == 0)
            return null;

        var query = messageDatabase.DbContext.GroupMessages
            .Where(message => message.GroupId == key.GroupId)
            .Where(message => message.MessageType != MessageType.Empty);

        if (key.MessageId != 0)
        {
            var idMessage = query
                .Where(message => message.MessageId == key.MessageId)
                .Take(1)
                .AsEnumerable()
                .Select(message => (MessageRecord?)MessageRecord.FromGroup(message))
                .FirstOrDefault();
            if (idMessage is not null)
                return idMessage;
        }

        if (key.MessageSeq == 0)
            return null;

        return query
            .Where(message => message.MessageSeq == key.MessageSeq)
            .OrderByDescending(message => message.MessageId)
            .Take(1)
            .AsEnumerable()
            .Select(message => (MessageRecord?)MessageRecord.FromGroup(message))
            .FirstOrDefault();
    }

    private static IReadOnlyDictionary<RecentPrivateMessageKey, RecentPrivateMessageMatch> ReadAndroidRecentPrivateMessageMatches(
        QQAndroidMessageReader messageDatabase,
        IReadOnlyCollection<RecentPrivateMessageKey> messageKeys)
    {
        if (messageKeys.Count == 0)
            return new Dictionary<RecentPrivateMessageKey, RecentPrivateMessageMatch>();

        var result = new Dictionary<RecentPrivateMessageKey, RecentPrivateMessageMatch>();
        foreach (var key in messageKeys.Distinct())
        {
            var latestMessage = FindAndroidRecentPrivateMessageCandidate(messageDatabase, key);
            var conversationMessage = latestMessage ?? FindAndroidLatestPrivateConversationMessage(messageDatabase, key.PeerUid);
            if (conversationMessage is not { } message || message.PrivateConversationId == 0)
                continue;

            result[key] = new RecentPrivateMessageMatch(
                message.PrivateConversationId,
                message.PeerUin,
                message.MessageTime,
                latestMessage);
        }

        return result;
    }

    private static MessageRecord? FindAndroidRecentPrivateMessageCandidate(
        QQAndroidMessageReader messageDatabase,
        RecentPrivateMessageKey key)
    {
        if (string.IsNullOrWhiteSpace(key.PeerUid) ||
            key.MessageId == 0 && key.MessageSeq == 0)
        {
            return null;
        }

        var query = messageDatabase.DbContext.PrivateMessages
            .Where(message => message.PeerUid == key.PeerUid)
            .Where(message => message.MessageType != MessageType.Empty);

        if (key.MessageId != 0)
        {
            var idMessage = query
                .Where(message => message.MessageId == key.MessageId)
                .Take(1)
                .AsEnumerable()
                .Select(message => (MessageRecord?)MessageRecord.FromPrivate(message))
                .FirstOrDefault();
            if (idMessage is not null)
                return idMessage;
        }

        if (key.MessageSeq == 0)
            return null;

        return query
            .Where(message => message.MessageSeq == key.MessageSeq)
            .OrderByDescending(message => message.MessageId)
            .Take(1)
            .AsEnumerable()
            .Select(message => (MessageRecord?)MessageRecord.FromPrivate(message))
            .FirstOrDefault();
    }

    private static MessageRecord? FindAndroidLatestPrivateConversationMessage(
        QQAndroidMessageReader messageDatabase,
        string peerUid)
    {
        if (string.IsNullOrWhiteSpace(peerUid))
            return null;

        return messageDatabase.DbContext.PrivateMessages
            .Where(message => message.PeerUid == peerUid)
            .Where(message => message.ConversationId != 0)
            .Where(message => message.MessageType != MessageType.Empty)
            .OrderByDescending(message => message.MessageSeq)
            .ThenByDescending(message => message.MessageId)
            .Take(1)
            .AsEnumerable()
            .Select(message => (MessageRecord?)MessageRecord.FromPrivate(message))
            .FirstOrDefault();
    }

    private static MessageRecord? FindRecentPrivateMessageCandidate(
        QQMessageReader messageDatabase,
        RecentPrivateMessageKey key)
    {
        if (string.IsNullOrWhiteSpace(key.PeerUid) ||
            key.MessageId == 0 && key.MessageSeq == 0 && key.MessageRandom == 0)
        {
            return null;
        }

        var query = messageDatabase.DbContext.PrivateMessages
            .Where(message => message.PeerUid == key.PeerUid)
            .Where(message => message.MessageType != MessageType.Empty);

        if (key.MessageId != 0)
        {
            var idMessage = query
                .Where(message => message.MessageId == key.MessageId)
                .Take(1)
                .AsEnumerable()
                .Select(message => (MessageRecord?)MessageRecord.FromPrivate(message))
                .FirstOrDefault();
            if (idMessage is not null)
                return idMessage;
        }

        if (key.MessageSeq != 0 && key.MessageRandom != 0)
        {
            var exactMessage = query
                .Where(message => message.MessageSeq == key.MessageSeq &&
                                  message.MessageRandom == key.MessageRandom)
                .OrderByDescending(message => message.MessageSeq)
                .ThenByDescending(message => message.MessageId)
                .Take(1)
                .AsEnumerable()
                .Select(message => (MessageRecord?)MessageRecord.FromPrivate(message))
                .FirstOrDefault();
            if (exactMessage is not null)
                return exactMessage;
        }

        if (key.MessageRandom != 0)
        {
            var randomMessage = query
                .Where(message => message.MessageRandom == key.MessageRandom)
                .OrderByDescending(message => message.MessageSeq)
                .ThenByDescending(message => message.MessageId)
                .Take(1)
                .AsEnumerable()
                .Select(message => (MessageRecord?)MessageRecord.FromPrivate(message))
                .FirstOrDefault();
            if (randomMessage is not null)
                return randomMessage;
        }

        if (key.MessageSeq == 0)
            return null;

        return query
            .Where(message => message.MessageSeq == key.MessageSeq)
            .OrderByDescending(message => message.MessageId)
            .Take(1)
            .AsEnumerable()
            .Select(message => (MessageRecord?)MessageRecord.FromPrivate(message))
            .FirstOrDefault();
    }

    private static MessageRecord? FindLatestPrivateConversationMessage(
        QQMessageReader messageDatabase,
        string peerUid)
    {
        if (string.IsNullOrWhiteSpace(peerUid))
            return null;

        return messageDatabase.DbContext.PrivateMessages
            .Where(message => message.PeerUid == peerUid)
            .Where(message => message.ConversationId != 0)
            .Where(message => message.MessageType != MessageType.Empty)
            .OrderByDescending(message => message.MessageSeq)
            .ThenByDescending(message => message.MessageId)
            .Take(1)
            .AsEnumerable()
            .Select(message => (MessageRecord?)MessageRecord.FromPrivate(message))
            .FirstOrDefault();
    }

    private static bool TryParseRecentGroupId(string? peerUin, out uint groupId)
    {
        groupId = 0;
        return !string.IsNullOrWhiteSpace(peerUin) &&
               uint.TryParse(peerUin, out groupId) &&
               groupId != 0;
    }

    private string CreateLatestMessageText(RecentConversationContact contact)
    {
        if (contact.LatestMessage is { } latestMessage)
        {
            return CreateLatestMessageText(contact.ConversationType, latestMessage);
        }

        var messageText = RecentMessagePreviewParser.Parse(contact.LastMessage);
        if (string.IsNullOrWhiteSpace(messageText))
            return string.Empty;

        if (contact.ConversationType == AvaConversationType.Private)
            return messageText;

        var senderName = FirstNonEmpty(contact.SendMemberName, contact.SendNickName, contact.SendremarkName);
        if (string.IsNullOrWhiteSpace(senderName))
        {
            senderName = ResolveProfileDisplayName(contact.SenderUin, contact.SenderUid, string.Empty);
        }

        return string.IsNullOrWhiteSpace(senderName)
            ? messageText
            : $"{senderName}: {messageText}";
    }

    private string CreateLatestMessageText(AvaQQGroup conversation, MessageRecord message)
    {
        return CreateLatestMessageText(conversation.ConversationType, message);
    }

    private string CreateLatestMessageText(AvaConversationType conversationType, MessageRecord message)
    {
        var messageText = CreateLatestMessagePreviewText(message);
        if (string.IsNullOrWhiteSpace(messageText))
            return string.Empty;

        if (conversationType is AvaConversationType.Private or AvaConversationType.PCQQPrivate)
            return messageText;

        var senderName = FirstNonEmpty(message.SendMemberName, message.SendNickName);
        if (string.IsNullOrWhiteSpace(senderName) && message.SenderId != 0)
        {
            senderName = ResolveProfileDisplayName(message.SenderId, message.SenderUid, message.SenderId.ToString());
        }

        return string.IsNullOrWhiteSpace(senderName)
            ? messageText
            : $"{senderName}: {messageText}";
    }

    private static string CreatePCQQLatestMessageText(
        PCQQConversationType conversationType,
        string messageText,
        uint senderUin,
        string? senderNickname)
    {
        if (string.IsNullOrWhiteSpace(messageText) ||
            conversationType == PCQQConversationType.Private)
        {
            return messageText;
        }

        var senderName = FirstNonEmpty(senderNickname, senderUin == 0 ? null : senderUin.ToString());
        return string.IsNullOrWhiteSpace(senderName)
            ? messageText
            : $"{senderName}: {messageText}";
    }

    private static string CreateLatestMessagePreviewText(MessageRecord message)
    {
        if (message.MessageType == MessageType.Text &&
            message.SubMessageType == SubMessageType.Text &&
            message.Content is { Length: > 0 } pcqqContent &&
            pcqqContent.AsSpan().StartsWith("MSG"u8))
        {
            return PCQQMessageContentParser.GetDisplayText(pcqqContent);
        }

        if (message.Content is { Length: > 0 })
        {
            var messageText = RecentMessagePreviewParser.Parse(message.Content);
            if (!string.IsNullOrWhiteSpace(messageText))
            {
                return IsStickerMessage(message) && string.Equals(messageText, "[图片]", StringComparison.Ordinal)
                    ? "[动画表情]"
                    : messageText;
            }
        }

        return CreateUnsupportedMessageText(message);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private IReadOnlyList<MessageDateFilterOption> LoadMessageDateFilterOptions(AvaQQGroup conversation)
    {
        if (IsPCQQConversation(conversation))
        {
            return LoadPCQQMessageDateFilterOptions(conversation);
        }

        try
        {
            return LoadNtMessageDateFilterOptions(conversation);
        }
        catch
        {
            return [];
        }
    }

    private IReadOnlyList<MessageDateFilterOption> LoadPCQQMessageDateFilterOptions(AvaQQGroup conversation)
    {
        if (_qqDatabaseService.PCQQMessageDatabase is not { } pcqqDatabase ||
            string.IsNullOrWhiteSpace(conversation.PCQQTableName))
        {
            return [];
        }

        try
        {
            return pcqqDatabase
                .LoadMessageDates(conversation.PCQQTableName)
                .Select(day => new MessageDateFilterOption(day.DayStartTime, day.MessageCount))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private IReadOnlyList<MessageDateFilterOption> LoadNtMessageDateFilterOptions(AvaQQGroup conversation)
    {
        var connection = (conversation.ConversationType, _qqDatabaseService.MessageDatabase, _qqDatabaseService.AndroidMessageDatabase) switch
        {
            (AvaConversationType.Group, { } database, _) => database.DbContext.Database.GetDbConnection(),
            (AvaConversationType.Group, _, { } database) => database.DbContext.Database.GetDbConnection(),
            (AvaConversationType.Private, { } database, _) => database.DbContext.Database.GetDbConnection(),
            (AvaConversationType.Private, _, { } database) => database.DbContext.Database.GetDbConnection(),
            _ => null,
        };

        if (connection is null)
            return [];

        if (connection.State != ConnectionState.Open)
            connection.Open();

        var (tableName, indexName, conversationColumn, conversationId) = conversation.ConversationType switch
        {
            AvaConversationType.Group => ("group_msg_table", "group_msg_table_idx40027_40058", "40027", unchecked((int)conversation.GroupId)),
            AvaConversationType.Private => ("c2c_msg_table", "c2c_msg_table_idx40027_40058", "40027", conversation.PrivateConversationId),
            _ => (null, null, null, 0L),
        };

        if (string.IsNullOrWhiteSpace(tableName) ||
            string.IsNullOrWhiteSpace(indexName) ||
            string.IsNullOrWhiteSpace(conversationColumn) ||
            conversationId == 0)
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT [40058], COUNT(*)
            FROM {tableName} INDEXED BY {indexName}
            WHERE [{conversationColumn}] = $conversationId
              AND [40058] > 0
            GROUP BY [40058]
            ORDER BY [40058]
            """;
        AddDbParameter(command, "$conversationId", conversationId);

        using var reader = command.ExecuteReader();
        var result = new List<MessageDateFilterOption>();
        while (reader.Read())
        {
            result.Add(new MessageDateFilterOption(
                reader.GetInt32(0),
                reader.GetInt32(1)));
        }

        return result;
    }

    private static void AddDbParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private IReadOnlyList<MessageSenderFilterOption> LoadSenderFilterCandidates(AvaQQGroup conversation)
    {
        if (conversation.ConversationType == AvaConversationType.Group)
        {
            return LoadNtGroupSenderFilterCandidates(conversation.GroupId);
        }

        if (conversation.ConversationType == AvaConversationType.PCQQGroup)
        {
            return LoadPCQQSenderFilterCandidates();
        }

        return [];
    }

    private IReadOnlyList<MessageSenderFilterOption> LoadNtGroupSenderFilterCandidates(uint groupId)
    {
        if (groupId == 0 ||
            _qqDatabaseService.GroupInfoDatabase is not { } groupInfoDatabase)
        {
            return GetCachedSenderFilterCandidates(SelectedGroup);
        }

        try
        {
            var candidates = groupInfoDatabase.DbContext.GroupMembers
                .Where(member => member.GroupId == groupId)
                .Where(member => member.Uin != 0)
                .Select(member => new
                {
                    member.Uin,
                    member.NtUid,
                    member.MemberName,
                    member.NickName,
                })
                .ToList()
                .Select(member => new MessageSenderFilterOption(
                    member.Uin,
                    FirstNonEmpty(member.MemberName, member.NickName, member.Uin.ToString()),
                    member.NtUid))
                .DistinctBy(member => member.SenderId)
                .OrderBy(member => member.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(member => member.SenderId)
                .ToArray();
            if (candidates.Length > 0)
                return candidates;
        }
        catch
        {
            return GetCachedSenderFilterCandidates(SelectedGroup);
        }

        return GetCachedSenderFilterCandidates(SelectedGroup);
    }

    private IReadOnlyList<MessageSenderFilterOption> LoadPCQQSenderFilterCandidates()
    {
        var infoDatabase = _qqDatabaseService.PCQQMessageDatabase?.InfoDatabase;
        if (infoDatabase is null)
            return GetCachedSenderFilterCandidates(SelectedGroup);

        try
        {
            return infoDatabase.GetContacts()
                .Values
                .Where(contact => contact.Uin != 0)
                .Select(contact => new MessageSenderFilterOption(
                    contact.Uin,
                    FirstNonEmpty(contact.RemarkName, contact.Nickname, contact.Uin.ToString())))
                .OrderBy(contact => contact.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(contact => contact.SenderId)
                .ToArray();
        }
        catch
        {
            return GetCachedSenderFilterCandidates(SelectedGroup);
        }
    }

    private IReadOnlyList<MessageSenderFilterOption> GetCachedSenderFilterCandidates(AvaQQGroup? conversation)
    {
        if (conversation is null)
            return [];

        var cachedNames = _conversationSenderNames.GetValueOrDefault(conversation.ConversationKey);
        if (cachedNames is null || cachedNames.Count == 0)
            return [];

        return cachedNames
            .Where(item => item.Key != 0)
            .Select(item => new MessageSenderFilterOption(item.Key, FirstNonEmpty(item.Value, item.Key.ToString())))
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.SenderId)
            .ToArray();
    }

    private static int ClampUnixTime(long timestamp)
    {
        if (timestamp > int.MaxValue)
            return int.MaxValue;

        if (timestamp < int.MinValue)
            return int.MinValue;

        return (int)timestamp;
    }

    private sealed record RecentConversationContact(
        AvaConversationType ConversationType,
        uint GroupId,
        long PrivateConversationId,
        uint PrivateUin,
        string? PrivateUid,
        string? DisplayName,
        byte[]? LastMessage,
        int LastTime,
        uint SenderUin,
        string? SenderUid,
        string? SendremarkName,
        string? SendMemberName,
        string? SendNickName,
        MessageRecord? LatestMessage)
    {
        public string ConversationKey => ConversationType switch
        {
            AvaConversationType.Group => $"group:{GroupId}",
            AvaConversationType.Private => $"private:{PrivateConversationId}",
            _ => $"{ConversationType}:{GroupId}:{PrivateConversationId}",
        };
    }

    private readonly record struct RecentGroupMessageKey(
        uint GroupId,
        long MessageId,
        long MessageSeq,
        long MessageRandom);

    private readonly record struct RecentPrivateMessageKey(
        string PeerUid,
        long MessageId,
        long MessageSeq,
        long MessageRandom);

    private sealed record RecentPrivateMessageMatch(
        long ConversationId,
        uint PrivateUin,
        int LastTime,
        MessageRecord? LatestMessage);

    private readonly record struct MessageSenderInfo(
        uint SenderId,
        string SenderUid,
        string Name);

    private readonly record struct SystemHintDisplay(
        string SourceName,
        string SourceUin,
        string TargetName,
        string TargetUin,
        string Action,
        string Suffix,
        string? ActionImageUrl,
        string DisplayText);

    private void OnDatabaseRemoved(IQQDatabase database)
    {
        if (database is QQMessageReader or QQAndroidMessageReader)
        {
            ClearMessageSelection();
            _messages.Clear();
            _conversationSenderNames.Clear();
            _conversationMessageSenderInfos.Clear();
            if (_qqDatabaseService.GroupInfoDatabase is null)
            {
                _groups.Clear();
                ClearGroupSelectionState();
                RefreshFilteredGroups();
            }
        }
        else if (database is PCQQMessageReader)
        {
            ClearMessageSelection();
            _messages.Clear();
            _conversationSenderNames.Clear();
            _conversationMessageSenderInfos.Clear();
            foreach (var group in _groups
                         .Where(static group => group.ConversationType is AvaConversationType.PCQQGroup or AvaConversationType.PCQQPrivate)
                         .ToArray())
            {
                _groups.Remove(group);
            }
            ClearGroupSelectionState();
            RefreshFilteredGroups();
        }
        else if (database is QQGroupInfoReader)
        {
            if (!HasNtMessageDatabase)
            {
                _groups.Clear();
                ClearGroupSelectionState();
                RefreshFilteredGroups();
            }
            else
            {
                foreach (var group in _groups)
                {
                    if (group.ConversationType == AvaConversationType.Group)
                    {
                        group.GroupName = null;
                    }
                }

                RefreshFilteredGroups();
            }
        }
        else if (database is QQProfileInfoReader)
        {
            _profileInfoNames = null;
            ApplyProfileInfoNames();
        }
    }

    private void RefreshFilteredGroups()
    {
        var query = GroupSearchText.Trim();
        var nonEmptyGroups = _groups.Where(IsValidConversation);
        var groups = string.IsNullOrWhiteSpace(query)
            ? nonEmptyGroups
            : nonEmptyGroups.Where(group =>
                group.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (group.ConversationType is AvaConversationType.Group or AvaConversationType.PCQQGroup &&
                 group.GroupId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                (group.ConversationType is AvaConversationType.Private or AvaConversationType.PCQQPrivate &&
                 (group.PrivateConversationId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
                  group.PrivateUin.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
                  (group.PrivateUid?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))));

        ApplyFilteredGroups(groups
            .OrderByDescending(group => group.LatestMessageTime)
            .ThenBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray());
    }

    private void ApplyFilteredGroups(IReadOnlyList<AvaQQGroup> targetGroups)
    {
        if (_filteredGroups.Count == 0)
        {
            _filteredGroups.AddRange(targetGroups);
            return;
        }

        var targetSet = new HashSet<AvaQQGroup>(targetGroups);
        for (var i = _filteredGroups.Count - 1; i >= 0; i--)
        {
            if (!targetSet.Contains(_filteredGroups[i]))
                _filteredGroups.RemoveAt(i);
        }

        for (var targetIndex = 0; targetIndex < targetGroups.Count; targetIndex++)
        {
            var targetGroup = targetGroups[targetIndex];
            if (targetIndex < _filteredGroups.Count &&
                ReferenceEquals(_filteredGroups[targetIndex], targetGroup))
            {
                continue;
            }

            var currentIndex = IndexOfFilteredGroup(targetGroup, targetIndex + 1);
            if (currentIndex >= 0)
            {
                _filteredGroups.Move(currentIndex, targetIndex);
            }
            else
            {
                _filteredGroups.Insert(targetIndex, targetGroup);
            }
        }
    }

    private int IndexOfFilteredGroup(AvaQQGroup targetGroup, int startIndex)
    {
        for (var i = Math.Max(startIndex, 0); i < _filteredGroups.Count; i++)
        {
            if (ReferenceEquals(_filteredGroups[i], targetGroup))
                return i;
        }

        return -1;
    }

    private static bool IsValidConversation(AvaQQGroup group)
    {
        return group.ConversationType switch
        {
            AvaConversationType.Group => group.GroupId != 0,
            AvaConversationType.Private => group.PrivateConversationId != 0,
            AvaConversationType.PCQQGroup => group.GroupId != 0 && !string.IsNullOrWhiteSpace(group.PCQQTableName),
            AvaConversationType.PCQQPrivate => group.PrivateUin != 0 && !string.IsNullOrWhiteSpace(group.PCQQTableName),
            _ => false,
        };
    }

    public IReadOnlyList<AvaQQGroup> SelectedGroups => _groups
        .Where(group => _selectedConversationKeys.Contains(group.ConversationKey))
        .ToArray();

    public void ActivateGroup(AvaQQGroup? group, bool updateRangeAnchor = true)
    {
        if (group is null || !IsValidConversation(group))
            return;

        if (updateRangeAnchor || _conversationRangeAnchorKey is null)
        {
            _conversationRangeAnchorKey = group.ConversationKey;
        }

        SelectedGroup = group;
    }

    public void SelectSingleGroup(AvaQQGroup? group)
    {
        if (group is null || !IsValidConversation(group))
            return;

        _selectedConversationKeys.Clear();
        _selectedConversationKeys.Add(group.ConversationKey);
        ApplyGroupSelectionState();
        ActivateGroup(group);
    }

    public void ToggleGroupSelection(AvaQQGroup? group)
    {
        if (group is null || !IsValidConversation(group))
            return;

        _conversationRangeAnchorKey = group.ConversationKey;
        if (!_selectedConversationKeys.Remove(group.ConversationKey))
        {
            _selectedConversationKeys.Add(group.ConversationKey);
        }

        if (_selectedConversationKeys.Count == 0)
        {
            _selectedConversationKeys.Add(group.ConversationKey);
        }

        ApplyGroupSelectionState();
        ActivateGroup(group, updateRangeAnchor: false);
    }

    public void SelectGroupRange(AvaQQGroup? group, bool preserveSelection)
    {
        if (group is null || !IsValidConversation(group))
            return;

        var visibleGroups = _filteredGroups.ToArray();
        var targetIndex = Array.IndexOf(visibleGroups, group);
        if (targetIndex < 0)
        {
            SelectSingleGroup(group);
            return;
        }

        var anchorGroup = _conversationRangeAnchorKey is { } anchorKey
            ? visibleGroups.FirstOrDefault(item => item.ConversationKey == anchorKey)
            : null;
        var anchorIndex = anchorGroup is null ? -1 : Array.IndexOf(visibleGroups, anchorGroup);
        if (anchorIndex < 0)
            anchorIndex = 0;

        if (!preserveSelection)
        {
            _selectedConversationKeys.Clear();
        }

        var start = Math.Min(anchorIndex, targetIndex);
        var end = Math.Max(anchorIndex, targetIndex);
        for (var i = start; i <= end; i++)
        {
            if (IsValidConversation(visibleGroups[i]))
            {
                _selectedConversationKeys.Add(visibleGroups[i].ConversationKey);
            }
        }

        ApplyGroupSelectionState();
        ActivateGroup(group);
    }

    private void ApplyGroupSelectionState()
    {
        foreach (var group in _groups)
        {
            group.IsSelected = _selectedConversationKeys.Contains(group.ConversationKey);
            group.IsActive = _activeConversationKey == group.ConversationKey;
        }

        OnPropertyChanged(nameof(SelectedGroups));
    }

    private void ClearGroupSelectionState()
    {
        _selectedConversationKeys.Clear();
        _conversationSenderNames.Clear();
        _conversationMessageSenderInfos.Clear();
        _activeConversationKey = null;
        _conversationRangeAnchorKey = null;
        SelectedGroup = null;
        OnPropertyChanged(nameof(SelectedGroups));
    }

    private sealed class ProfileInfoNameCache
    {
        private static readonly IReadOnlyDictionary<uint, string> EmptyUinNames =
            new Dictionary<uint, string>();
        private static readonly IReadOnlyDictionary<string, string> EmptyNtUidNames =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly ProfileInfoNameCache Empty = new(null, EmptyUinNames, EmptyNtUidNames);

        private ProfileInfoNameCache(
            QQProfileInfoReader? database,
            IReadOnlyDictionary<uint, string> namesByUin,
            IReadOnlyDictionary<string, string> namesByNtUid)
        {
            Database = database;
            _namesByUin = namesByUin;
            _namesByNtUid = namesByNtUid;
        }

        private readonly IReadOnlyDictionary<uint, string> _namesByUin;
        private readonly IReadOnlyDictionary<string, string> _namesByNtUid;

        public QQProfileInfoReader? Database { get; }

        public static ProfileInfoNameCache Create(QQProfileInfoReader? database)
        {
            if (database is null)
                return Empty;

            var namesByUin = new Dictionary<uint, string>();
            var namesByNtUid = new Dictionary<string, string>(StringComparer.Ordinal);

            try
            {
                foreach (var profile in database.DbContext.ProfileInfo
                             .Select(profile => new
                             {
                                 profile.Uin,
                                 profile.NtUid,
                                 profile.NickName,
                                 profile.RemarkName,
                             })
                             .ToList())
                {
                    CacheProfileName(namesByUin, namesByNtUid, profile.Uin, profile.NtUid, profile.RemarkName, profile.NickName);
                }

                foreach (var buddy in database.DbContext.BuddyList
                             .Select(buddy => new
                             {
                                 buddy.Uin,
                                 buddy.NtUid,
                             })
                             .ToList())
                {
                    if (buddy.Uin != 0 &&
                        !string.IsNullOrWhiteSpace(buddy.NtUid) &&
                        namesByUin.TryGetValue(buddy.Uin, out var name) &&
                        !namesByNtUid.ContainsKey(buddy.NtUid))
                    {
                        namesByNtUid[buddy.NtUid] = name;
                    }
                }
            }
            catch
            {
                return new ProfileInfoNameCache(database, EmptyUinNames, EmptyNtUidNames);
            }

            return new ProfileInfoNameCache(database, namesByUin, namesByNtUid);
        }

        public bool TryGetName(uint uin, string? ntUid, out string name)
        {
            if (uin != 0 &&
                _namesByUin.TryGetValue(uin, out name!) &&
                !string.IsNullOrWhiteSpace(name))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(ntUid) &&
                _namesByNtUid.TryGetValue(ntUid, out name!) &&
                !string.IsNullOrWhiteSpace(name))
            {
                return true;
            }

            name = string.Empty;
            return false;
        }

        private static void CacheProfileName(
            IDictionary<uint, string> namesByUin,
            IDictionary<string, string> namesByNtUid,
            uint uin,
            string? ntUid,
            string? remarkName,
            string? nickName)
        {
            var name = FirstNonEmpty(remarkName, nickName);
            if (string.IsNullOrWhiteSpace(name))
                return;

            if (uin != 0)
            {
                namesByUin[uin] = name;
            }

            if (!string.IsNullOrWhiteSpace(ntUid))
            {
                namesByNtUid[ntUid] = name;
            }
        }
    }

    private sealed record MessageRecord(
        long MessageId,
        long MessageRandom,
        long MessageSeq,
        MessageType MessageType,
        SubMessageType SubMessageType,
        int SendType,
        string SenderUid,
        string PeerUid,
        uint GroupId,
        long PrivateConversationId,
        uint PeerUin,
        int MessageTime,
        string? SendMemberName,
        string? SendNickName,
        byte[]? Content,
        byte[]? SubContent,
        long ReplyToMessageSeq,
        uint SenderId)
    {
        public static MessageRecord FromGroup(GroupMessage message)
        {
            return new MessageRecord(
                message.MessageId,
                message.MessageRandom,
                message.MessageSeq,
                message.MessageType,
                message.SubMessageType,
                message.SendType,
                message.SenderUid,
                message.PeerUid,
                message.GroupId,
                0,
                0,
                message.MessageTime,
                message.SendMemberName,
                message.SendNickName,
                message.Content,
                message.SubContent,
                message.ReplyToMessageSeq,
                message.SenderId);
        }

        public static MessageRecord FromPrivate(PrivateMessage message)
        {
            return new MessageRecord(
                message.MessageId,
                message.MessageRandom,
                message.MessageSeq,
                message.MessageType,
                message.SubMessageType,
                message.SendType,
                message.SenderUid,
                message.PeerUid,
                0,
                message.ConversationId,
                message.PeerUin,
                message.MessageTime,
                message.SendMemberName,
                message.SendNickName,
                message.Content,
                message.SubContent,
                message.ReplyToMessageSeq,
                message.SenderId);
        }

        public static MessageRecord FromPCQQ(PCQQMessageRecord message, AvaQQGroup conversation)
        {
            return new MessageRecord(
                message.MessageRandom,
                message.MessageRandom,
                message.MessageTime,
                MessageType.Text,
                SubMessageType.Text,
                0,
                string.Empty,
                conversation.ConversationType == AvaConversationType.PCQQPrivate
                    ? conversation.PrivateUin.ToString()
                    : string.Empty,
                conversation.ConversationType == AvaConversationType.PCQQGroup ? conversation.GroupId : 0,
                conversation.ConversationType == AvaConversationType.PCQQPrivate ? conversation.PrivateUin : 0,
                conversation.ConversationType == AvaConversationType.PCQQPrivate ? conversation.PrivateUin : 0,
                ClampUnixTime(message.MessageTime),
                null,
                message.SenderNickname,
                message.Content,
                message.Info,
                0,
                message.SenderUin);
        }
    }

    private sealed record LocalMediaContext(
        DatabasePlatformType PlatformType,
        string? NtDataPath,
        string? MobileQQPath);
}


