using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ObservableCollections;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels;

public partial class GroupMessageSearchViewModel : ViewModelBase
{
    private readonly QQDatabaseService _qqDatabaseService;
    private readonly ConversationSearchFilterParser _searchFilterParser;
    private readonly ConversationMessageSearchProviderFactory _searchProviderFactory;
    private readonly ConversationMessageSearchWorkflow _searchWorkflow;
    private readonly ConversationSearchResultNavigator _resultNavigator;
    private readonly MessageTabViewModel _messageTabViewModel;
    private readonly ObservableList<AvaGroupMessageSearchGroup> _groups = new();
    private readonly ObservableList<AvaGroupMessageSearchResult> _results = new();
    private readonly ConversationMessageSearchResultStore _resultStore;
    private readonly ConversationMessageSearchSession _searchSession = new();
    private readonly ConversationMessageSearchVersionTracker _versionTracker = new();
    private bool _isLoadingMore;
    private bool _suppressSelectedGroupRefresh;
    private int _lastVisibleResultCount;
    private string? _visibleGroupKey;

    public NotifyCollectionChangedSynchronizedViewList<AvaGroupMessageSearchGroup> Groups { get; }
    public NotifyCollectionChangedSynchronizedViewList<AvaGroupMessageSearchResult> Results { get; }

    [ObservableProperty]
    public partial string QueryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GroupFilterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingMore { get; set; }

    public bool CanSearch => !IsSearching;

    public bool ShowSearchOverlay => IsSearching && _results.Count == 0;

    public bool ShowInlineSearchProgress => IsSearching && _results.Count > 0;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "打开消息数据库后可以搜索聊天记录";

    [ObservableProperty]
    public partial bool HasSearchDatabase { get; set; }

    [ObservableProperty]
    public partial AvaGroupMessageSearchGroup? SelectedGroup { get; set; }

    public GroupMessageSearchViewModel(
        QQDatabaseService qqDatabaseService,
        MessageTabViewModel messageTabViewModel,
        MainViewModel mainViewModel)
    {
        _messageTabViewModel = messageTabViewModel;
        _resultNavigator = new ConversationSearchResultNavigator(messageTabViewModel, mainViewModel);
        _resultStore = new ConversationMessageSearchResultStore(_groups);
        Groups = _groups.ToNotifyCollectionChanged();
        Results = _results.ToNotifyCollectionChanged();
        _qqDatabaseService = qqDatabaseService;
        var composition = ConversationMessageSearchCompositionFactory.Create(
            qqDatabaseService,
            _groups,
            _resultStore,
            _searchSession);
        _searchFilterParser = composition.FilterParser;
        _searchProviderFactory = composition.ProviderFactory;
        _searchWorkflow = composition.Workflow;
        _qqDatabaseService.DatabaseAdded += OnDatabaseAdded;
        _qqDatabaseService.DatabaseRemoved += OnDatabaseRemoved;
        RefreshDatabaseState();
    }

    public async Task OpenResultInMessageTabAsync(AvaGroupMessageSearchResult result)
    {
        await _resultNavigator.OpenAsync(result, clearMessageFilter: false);
    }

    public async Task OpenResultInMessageTabAndClearFilterAsync(AvaGroupMessageSearchResult result)
    {
        await _resultNavigator.OpenAsync(result, clearMessageFilter: true);
    }

    public Task<MessageCopyPayload> CreateSearchResultCopyPayloadAsync(AvaGroupMessageSearchResult result)
    {
        return _messageTabViewModel.CreateSearchResultCopyPayloadAsync(result);
    }

    partial void OnIsSearchingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSearch));
        OnPropertyChanged(nameof(CanLoadMore));
        OnPropertyChanged(nameof(ShowSearchOverlay));
        OnPropertyChanged(nameof(ShowInlineSearchProgress));
    }

    partial void OnSelectedGroupChanged(AvaGroupMessageSearchGroup? value)
    {
        OnPropertyChanged(nameof(CanLoadMore));
        if (!_suppressSelectedGroupRefresh)
            _ = RefreshVisibleResultsAsync(value, _versionTracker.BeginVisibleResultsRefresh());
    }

    partial void OnIsLoadingMoreChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLoadMore));
    }

    private void OnDatabaseAdded(IQQDatabase database)
    {
        if (_searchProviderFactory.ShouldRefreshFor(database))
            RefreshDatabaseState();
    }

    private void OnDatabaseRemoved(IQQDatabase database)
    {
        if (!_searchProviderFactory.ShouldRefreshFor(database))
            return;

        _versionTracker.CancelSearch();
        ResetSearchSessionState(clearInputs: false);
        RefreshDatabaseState();
    }

    private void RefreshDatabaseState()
    {
        HasSearchDatabase = _searchProviderFactory.HasSearchDatabase;
        if (!HasSearchDatabase)
        {
            _versionTracker.CancelSearch();
            ResetSearchSessionState(clearInputs: false);
            SetNoSearchDatabaseStatus();
        }
        else if (_results.Count == 0)
        {
            StatusText = _searchProviderFactory.ReadyStatusText;
        }
    }

    [RelayCommand]
    public async Task Search()
    {
        if (IsSearching)
            return;

        if (!_searchProviderFactory.HasSearchDatabase)
        {
            RefreshDatabaseState();
            return;
        }

        if (string.IsNullOrWhiteSpace(QueryText))
        {
            StatusText = "请输入要搜索的内容";
            return;
        }

        if (!_searchFilterParser.TryParse(GroupFilterText, out var conversationFilter, out var filterErrorMessage))
        {
            StatusText = filterErrorMessage ?? "只能填写群号、QQ号或 Icalingua roomId";
            return;
        }

        var searchVersion = _versionTracker.BeginSearch();
        try
        {
            IsSearching = true;
            StatusText = "搜索中...";
            ResetSearchResultState();
            var query = QueryText.Trim();
            var searchResult = await _searchWorkflow.SearchAsync(
                query,
                conversationFilter,
                SelectedGroup);

            if (!_versionTracker.IsCurrentSearch(searchVersion))
                return;

            await SetSelectedGroupAndRefreshAsync(searchResult.SelectedGroup);
            UpdateStatusText();
            _ = StreamDiscoveryResultsInBackgroundAsync(searchVersion);
        }
        catch (Exception ex)
        {
            if (_versionTracker.IsCurrentSearch(searchVersion))
            {
                StatusText = $"搜索失败: {ex.Message}";
                IsSearching = false;
            }
        }
    }

    public bool CanLoadMore =>
        HasSearchDatabase &&
        !IsSearching &&
        !IsLoadingMore &&
        _searchSession.HasQuery &&
        (SelectedGroup is { } selectedGroup && _resultStore.HasConversationCursor(selectedGroup)
            ? _resultStore.HasMoreConversationResults(selectedGroup)
            : _resultStore.HasMoreDiscoveryResults);

    public async Task LoadMoreResultsAsync()
    {
        if (!CanLoadMore || _isLoadingMore)
            return;

        if (!_searchWorkflow.IsLoadMoreAvailable)
            return;

        var searchVersion = _versionTracker.CurrentSearchVersion;
        try
        {
            _isLoadingMore = true;
            IsLoadingMore = true;
            var selectedGroup = await _searchWorkflow.LoadMoreAsync(SelectedGroup);

            if (!_versionTracker.IsCurrentSearch(searchVersion))
                return;

            if (selectedGroup is not null && !ReferenceEquals(SelectedGroup, selectedGroup))
                SelectedGroup = selectedGroup;

            await RefreshVisibleResultsAsync(SelectedGroup, _versionTracker.BeginVisibleResultsRefresh());
            UpdateStatusText();
        }
        catch (Exception ex)
        {
            if (_versionTracker.IsCurrentSearch(searchVersion))
                StatusText = $"加载更多失败: {ex.Message}";
        }
        finally
        {
            if (_versionTracker.IsCurrentSearch(searchVersion))
                IsLoadingMore = false;

            _isLoadingMore = false;
        }
    }

    [RelayCommand]
    public void Clear()
    {
        _versionTracker.CancelSearch();
        ResetSearchSessionState(clearInputs: true);
        RefreshDatabaseState();
    }

    private void ResetSearchSessionState(bool clearInputs)
    {
        IsSearching = false;
        IsLoadingMore = false;
        if (clearInputs)
        {
            QueryText = string.Empty;
            GroupFilterText = string.Empty;
        }

        ResetSearchResultState();
        _searchSession.Reset();
    }

    private void ResetSearchResultState()
    {
        _resultStore.Clear();
        ClearVisibleResults();
        _lastVisibleResultCount = 0;
        OnPropertyChanged(nameof(ShowSearchOverlay));
        OnPropertyChanged(nameof(ShowInlineSearchProgress));
        SelectedGroup = null;
        _versionTracker.BeginVisibleResultsRefresh();
    }

    private void SetNoSearchDatabaseStatus()
    {
        StatusText = ConversationMessageSearchStatusFormatter.CreateNoSearchDatabaseStatus();
    }

    private async Task RefreshVisibleResultsAsync(AvaGroupMessageSearchGroup? selectedGroup, int version)
    {
        if (selectedGroup is null)
        {
            ClearVisibleResults();
            return;
        }

        var selectedGroupKey = ConversationMessageSearchResultStore.GetGroupKey(selectedGroup);
        if (!string.Equals(_visibleGroupKey, selectedGroupKey, StringComparison.Ordinal))
            ClearVisibleResults(selectedGroupKey);

        if (_searchWorkflow.ShouldLoadConversationPage(selectedGroup, _searchSession.DatabaseKind))
        {
            ConversationSearchPageLoadResult pageLoadResult;
            try
            {
                IsLoadingMore = true;
                pageLoadResult = await _searchWorkflow.LoadConversationPageAsync(
                    selectedGroup,
                    _searchSession.Query,
                    _searchSession.DatabaseKind);
            }
            catch (Exception ex)
            {
                if (_versionTracker.IsCurrentVisibleResultsRefresh(version, SelectedGroup, selectedGroup))
                    StatusText = $"加载会话搜索结果失败: {ex.Message}";

                return;
            }
            finally
            {
                if (_versionTracker.IsCurrentVisibleResultsRefresh(version, SelectedGroup, selectedGroup))
                    IsLoadingMore = false;
            }

            if (!_versionTracker.IsCurrentVisibleResultsRefresh(version, SelectedGroup, selectedGroup))
                return;

            _resultStore.AppendGroupPage(pageLoadResult.GroupKey, pageLoadResult.Page);
            UpdateStatusText();
        }

        var enrichedResults = await _searchWorkflow.GetEnrichedVisibleResultsAsync(selectedGroup);

        if (!_versionTracker.IsCurrentVisibleResultsRefresh(version, SelectedGroup, selectedGroup))
            return;

        ApplyVisibleResults(enrichedResults);
        _lastVisibleResultCount = _results.Count;
        OnPropertyChanged(nameof(ShowSearchOverlay));
        OnPropertyChanged(nameof(ShowInlineSearchProgress));
    }

    private void UpdateStatusText()
    {
        StatusText = ConversationMessageSearchStatusFormatter.CreateSearchSummary(
            _resultStore.DiscoveryResultCount,
            _searchSession.TotalMatchCount,
            _groups.Count,
            _searchSession.IsDiscoveringResults,
            _resultStore.HasMoreDiscoveryResults,
            _searchSession.DatabaseKind);
        OnPropertyChanged(nameof(CanLoadMore));
    }

    private async Task StreamDiscoveryResultsAsync(int searchVersion)
    {
        while (_versionTracker.IsCurrentSearch(searchVersion) && _resultStore.HasMoreDiscoveryResults)
        {
            var selectedGroup = await _searchWorkflow.LoadMoreDiscoveryAsync(SelectedGroup);
            if (!_versionTracker.IsCurrentSearch(searchVersion))
                return;

            await SetSelectedGroupAndRefreshIfChangedAsync(selectedGroup);
            UpdateStatusText();
            await Task.Yield();
        }

        if (!_versionTracker.IsCurrentSearch(searchVersion))
            return;

        var sortedSelectedGroup = _searchWorkflow.CompleteDiscovery(SelectedGroup);
        await SetSelectedGroupAndRefreshAsync(sortedSelectedGroup);
        UpdateStatusText();
    }

    private async Task StreamDiscoveryResultsInBackgroundAsync(int searchVersion)
    {
        try
        {
            await StreamDiscoveryResultsAsync(searchVersion);
        }
        catch (Exception ex)
        {
            if (_versionTracker.IsCurrentSearch(searchVersion))
                StatusText = $"搜索失败: {ex.Message}";
        }
        finally
        {
            if (_versionTracker.IsCurrentSearch(searchVersion))
                IsSearching = false;
        }
    }

    private async Task SetSelectedGroupAndRefreshIfChangedAsync(AvaGroupMessageSearchGroup? selectedGroup)
    {
        if (!ReferenceEquals(SelectedGroup, selectedGroup))
        {
            await SetSelectedGroupAndRefreshAsync(selectedGroup);
            return;
        }

        if (selectedGroup is null)
            return;

        var visibleResultCount = _resultStore.GetVisibleResultCount(selectedGroup);
        if (visibleResultCount != _lastVisibleResultCount)
            await RefreshVisibleResultsAsync(selectedGroup, _versionTracker.BeginVisibleResultsRefresh());
    }

    private async Task SetSelectedGroupAndRefreshAsync(AvaGroupMessageSearchGroup? selectedGroup)
    {
        if (!ReferenceEquals(SelectedGroup, selectedGroup))
        {
            _suppressSelectedGroupRefresh = true;
            try
            {
                SelectedGroup = selectedGroup;
            }
            finally
            {
                _suppressSelectedGroupRefresh = false;
            }
        }

        await RefreshVisibleResultsAsync(SelectedGroup, _versionTracker.BeginVisibleResultsRefresh());
    }

    private void ClearVisibleResults(string? visibleGroupKey = null)
    {
        _results.Clear();
        _lastVisibleResultCount = 0;
        _visibleGroupKey = visibleGroupKey;
        OnPropertyChanged(nameof(ShowSearchOverlay));
        OnPropertyChanged(nameof(ShowInlineSearchProgress));
    }

    private void ApplyVisibleResults(IReadOnlyList<AvaGroupMessageSearchResult> results)
    {
        if (_results.Count <= results.Count && HasSameResultPrefix(results))
        {
            for (var index = 0; index < _results.Count; index++)
            {
                if (!HasSameResultDisplay(_results[index], results[index]))
                    _results[index] = results[index];
            }

            _results.AddRange(results.Skip(_results.Count));
            return;
        }

        _results.Clear();
        _results.AddRange(results);
    }

    private bool HasSameResultPrefix(IReadOnlyList<AvaGroupMessageSearchResult> results)
    {
        for (var index = 0; index < _results.Count; index++)
        {
            if (!IsSameResult(_results[index], results[index]))
                return false;
        }

        return true;
    }

    private static bool IsSameResult(AvaGroupMessageSearchResult left, AvaGroupMessageSearchResult right)
    {
        return left.ConversationType == right.ConversationType &&
            left.GroupId == right.GroupId &&
            left.PrivateConversationId == right.PrivateConversationId &&
            left.IcalinguaRoomId == right.IcalinguaRoomId &&
            left.PeerUin == right.PeerUin &&
            left.MessageId == right.MessageId &&
            left.MessageSeq == right.MessageSeq;
    }

    private static bool HasSameResultDisplay(AvaGroupMessageSearchResult left, AvaGroupMessageSearchResult right)
    {
        return left.SenderId == right.SenderId &&
            string.Equals(left.SenderName, right.SenderName, StringComparison.Ordinal) &&
            string.Equals(left.SenderUid, right.SenderUid, StringComparison.Ordinal) &&
            string.Equals(left.GroupName, right.GroupName, StringComparison.Ordinal) &&
            string.Equals(left.PeerUid, right.PeerUid, StringComparison.Ordinal) &&
            string.Equals(left.PreviewText, right.PreviewText, StringComparison.Ordinal);
    }

}
