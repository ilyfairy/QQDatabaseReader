using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    private const int SearchPageSize = 100;
    private const int GroupCountScanPageSize = 5000;
    private const int GroupCountStatusUpdateInterval = 100000;
    private readonly QQDatabaseService _qqDatabaseService;
    private readonly object _messageDatabaseGate = new();
    private readonly object _groupInfoDatabaseGate = new();
    private readonly MessageTabViewModel _messageTabViewModel;
    private readonly MainViewModel _mainViewModel;
    private readonly ObservableList<AvaGroupMessageSearchGroup> _groups = new();
    private readonly ObservableList<AvaGroupMessageSearchResult> _results = new();
    private List<AvaGroupMessageSearchResult> _allResults = [];
    private readonly List<AvaGroupMessageSearchResult> _discoveryResults = [];
    private readonly Dictionary<string, AvaGroupMessageSearchGroup> _groupMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long?> _groupNextBeforeRowIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _groupsWithMoreResults = new(StringComparer.Ordinal);
    private string _currentQuery = string.Empty;
    private uint? _currentGroupFilter;
    private long? _nextDiscoveryBeforeRowId;
    private long? _totalMatchCount;
    private bool _isCountingGroups;
    private bool _hasExactGroupCounts;
    private bool _hasMoreResults;
    private bool _isLoadingMore;
    private int _searchVersion;

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

    [ObservableProperty]
    public partial string StatusText { get; set; } = "打开 group_msg_fts.db 后可以搜索群消息";

    [ObservableProperty]
    public partial bool HasGroupMessageFtsDatabase { get; set; }

    [ObservableProperty]
    public partial AvaGroupMessageSearchGroup? SelectedGroup { get; set; }

    private int _visibleResultsVersion;

    public GroupMessageSearchViewModel(
        QQDatabaseService qqDatabaseService,
        MessageTabViewModel messageTabViewModel,
        MainViewModel mainViewModel)
    {
        _messageTabViewModel = messageTabViewModel;
        _mainViewModel = mainViewModel;
        Groups = _groups.ToNotifyCollectionChanged();
        Results = _results.ToNotifyCollectionChanged();
        _qqDatabaseService = qqDatabaseService;
        _qqDatabaseService.DatabaseAdded += OnDatabaseChanged;
        _qqDatabaseService.DatabaseRemoved += OnDatabaseChanged;
        RefreshDatabaseState();
    }

    public async Task OpenResultInMessageTabAsync(AvaGroupMessageSearchResult result)
    {
        _mainViewModel.ShowMessageTab();

        if (result.GroupId == 0 || result.MessageSeq <= 0)
            return;

        await _messageTabViewModel.JumpToMessageAsync(
            result.GroupId,
            result.MessageId,
            result.MessageSeq,
            result.GroupName);
    }

    public async Task OpenResultInMessageTabAndClearFilterAsync(AvaGroupMessageSearchResult result)
    {
        _mainViewModel.ShowMessageTab();

        if (result.GroupId == 0 || result.MessageSeq <= 0)
            return;

        await _messageTabViewModel.JumpToMessageAndClearFilterAsync(
            result.GroupId,
            result.MessageId,
            result.MessageSeq,
            result.GroupName);
    }

    partial void OnIsSearchingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSearch));
        OnPropertyChanged(nameof(CanLoadMore));
    }

    partial void OnSelectedGroupChanged(AvaGroupMessageSearchGroup? value)
    {
        OnPropertyChanged(nameof(CanLoadMore));
        _ = RefreshVisibleResultsAsync(value, ++_visibleResultsVersion);
    }

    partial void OnIsLoadingMoreChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLoadMore));
    }

    private void OnDatabaseChanged(IQQDatabase database)
    {
        if (database.DatabaseType == QQDatabaseType.GroupMessageFts)
            RefreshDatabaseState();
    }

    private void RefreshDatabaseState()
    {
        HasGroupMessageFtsDatabase = _qqDatabaseService.GroupMessageFtsDatabase is not null;
        if (!HasGroupMessageFtsDatabase)
        {
            Interlocked.Increment(ref _searchVersion);
            IsSearching = false;
            IsLoadingMore = false;
            _allResults = [];
            _discoveryResults.Clear();
            _groupMap.Clear();
            _groupNextBeforeRowIds.Clear();
            _groupsWithMoreResults.Clear();
            _groups.Clear();
            _results.Clear();
            SelectedGroup = null;
            _currentQuery = string.Empty;
            _currentGroupFilter = null;
            _nextDiscoveryBeforeRowId = null;
            _totalMatchCount = null;
            _isCountingGroups = false;
            _hasExactGroupCounts = false;
            _hasMoreResults = false;
            StatusText = "打开 group_msg_fts.db 后可以搜索群消息";
        }
        else if (_results.Count == 0)
        {
            StatusText = "输入关键词，支持中文、拼音、首字母、URL";
        }
    }

    [RelayCommand]
    public async Task Search()
    {
        if (IsSearching)
            return;

        var ftsDatabase = _qqDatabaseService.GroupMessageFtsDatabase;
        if (ftsDatabase is null)
        {
            RefreshDatabaseState();
            return;
        }

        if (string.IsNullOrWhiteSpace(QueryText))
        {
            StatusText = "请输入要搜索的内容";
            return;
        }

        if (!TryParseGroupFilter(out var groupId))
            return;

        var searchVersion = Interlocked.Increment(ref _searchVersion);
        try
        {
            IsSearching = true;
            StatusText = "搜索中...";
            _allResults = [];
            _discoveryResults.Clear();
            _groupMap.Clear();
            _groupNextBeforeRowIds.Clear();
            _groupsWithMoreResults.Clear();
            _groups.Clear();
            _results.Clear();
            SelectedGroup = null;
            _visibleResultsVersion++;
            var query = QueryText.Trim();
            _currentQuery = query;
            _currentGroupFilter = groupId;
            _nextDiscoveryBeforeRowId = null;
            _totalMatchCount = null;
            _isCountingGroups = true;
            _hasExactGroupCounts = false;
            _hasMoreResults = false;

            var messageDatabase = _qqDatabaseService.MessageDatabase;
            var groupInfoDatabase = _qqDatabaseService.GroupInfoDatabase;

            var searchResult = await Task.Run(() =>
            {
                var totalCount = groupId is null
                    ? ftsDatabase.Count(new GroupMessageFtsCountRequest(query))
                    : (long?)null;
                var firstPage = LoadSearchPage(
                    ftsDatabase,
                    messageDatabase,
                    groupInfoDatabase,
                    query,
                    groupId,
                    beforeRowId: null);

                return new InitialSearchResult(firstPage, totalCount);
            });

            if (searchVersion != _searchVersion)
                return;

            _totalMatchCount = searchResult.TotalCount;
            AppendDiscoveryPage(searchResult.Page);

            SelectedGroup = _groups.FirstOrDefault();

            UpdateStatusText();

            _ = CountGroupsInBackgroundAsync(ftsDatabase, query, groupId, searchVersion);
        }
        catch (Exception ex)
        {
            if (searchVersion == _searchVersion)
                StatusText = $"搜索失败: {ex.Message}";
        }
        finally
        {
            if (searchVersion == _searchVersion)
                IsSearching = false;
        }
    }

    public bool CanLoadMore =>
        HasGroupMessageFtsDatabase &&
        !IsSearching &&
        !IsLoadingMore &&
        !string.IsNullOrWhiteSpace(_currentQuery) &&
        (SelectedGroup is { } selectedGroup && _groupNextBeforeRowIds.ContainsKey(GetGroupKey(selectedGroup))
            ? _groupsWithMoreResults.Contains(GetGroupKey(selectedGroup))
            : _hasMoreResults);

    public async Task LoadMoreResultsAsync()
    {
        if (!CanLoadMore || _isLoadingMore)
            return;

        var ftsDatabase = _qqDatabaseService.GroupMessageFtsDatabase;
        if (ftsDatabase is null)
            return;

        var searchVersion = _searchVersion;
        try
        {
            _isLoadingMore = true;
            IsLoadingMore = true;

            var selectedGroup = SelectedGroup;
            var selectedGroupKey = selectedGroup is null ? null : GetGroupKey(selectedGroup);
            var page = await Task.Run(() =>
            {
                if (selectedGroup is not null &&
                    selectedGroup.GroupId != 0 &&
                    selectedGroupKey is not null &&
                    _groupNextBeforeRowIds.TryGetValue(selectedGroupKey, out var groupBeforeRowId))
                {
                    return LoadSearchPage(
                        ftsDatabase,
                        _qqDatabaseService.MessageDatabase,
                        _qqDatabaseService.GroupInfoDatabase,
                        _currentQuery,
                        selectedGroup.GroupId,
                        groupBeforeRowId);
                }

                return LoadSearchPage(
                    ftsDatabase,
                    _qqDatabaseService.MessageDatabase,
                    _qqDatabaseService.GroupInfoDatabase,
                    _currentQuery,
                    _currentGroupFilter,
                    _nextDiscoveryBeforeRowId);
            });

            if (searchVersion != _searchVersion)
                return;

            if (selectedGroupKey is not null &&
                selectedGroup is not null &&
                selectedGroup.GroupId != 0 &&
                _groupNextBeforeRowIds.ContainsKey(selectedGroupKey))
            {
                AppendGroupPage(selectedGroupKey, page);
            }
            else
            {
                AppendDiscoveryPage(page);
            }

            await RefreshVisibleResultsAsync(SelectedGroup, ++_visibleResultsVersion);
            UpdateStatusText();
        }
        catch (Exception ex)
        {
            if (searchVersion == _searchVersion)
                StatusText = $"加载更多失败: {ex.Message}";
        }
        finally
        {
            if (searchVersion == _searchVersion)
                IsLoadingMore = false;

            _isLoadingMore = false;
        }
    }

    private SearchPage LoadSearchPage(
        QQGroupMessageFtsReader ftsDatabase,
        QQMessageReader? messageDatabase,
        QQGroupInfoReader? groupInfoDatabase,
        string query,
        uint? groupId,
        long? beforeRowId)
    {
        var results = ftsDatabase.Search(new GroupMessageFtsSearchRequest(
            query,
            groupId,
            SearchPageSize,
            Order: GroupMessageFtsSearchOrder.Newest,
            BeforeRowId: beforeRowId));

        var groupIds = results
            .Select(GetSearchResultGroupId)
            .Where(groupId => groupId != 0)
            .Distinct()
            .ToArray();
        var groupNames = LoadGroupNames(messageDatabase, groupInfoDatabase, groupIds);
        var viewResults = results
            .Select(result => CreateResult(result, groupNames))
            .ToList();

        return new SearchPage(
            viewResults,
            results.Count == SearchPageSize,
            results.LastOrDefault()?.RowId);
    }

    private void AppendDiscoveryPage(SearchPage page)
    {
        if (page.Results.Count == 0)
        {
            _hasMoreResults = false;
            return;
        }

        _discoveryResults.AddRange(page.Results);
        _nextDiscoveryBeforeRowId = page.NextBeforeRowId;
        _hasMoreResults = page.HasMore;

        var changedGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in page.Results)
        {
            var key = GetGroupKey(result);
            if (_groupMap.TryGetValue(key, out var group))
            {
                if (!_hasExactGroupCounts)
                    group.MatchCount++;

                changedGroups.Add(key);
                continue;
            }

            group = CreateGroup(result, _currentQuery);
            group.IsCounting = _isCountingGroups;
            _groupMap.Add(key, group);
            _groups.Add(group);
        }

        if (changedGroups.Count > 0)
        {
            var selectedKey = SelectedGroup is null ? null : GetGroupKey(SelectedGroup);
            var groups = _groupMap.Values
                .OrderByDescending(group => group.MatchCount)
                .ThenBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            _groups.Clear();
            _groups.AddRange(groups);

            if (selectedKey is not null && _groupMap.TryGetValue(selectedKey, out var selectedGroup))
                SelectedGroup = selectedGroup;
        }
    }

    private async Task CountGroupsInBackgroundAsync(
        QQGroupMessageFtsReader ftsDatabase,
        string query,
        uint? groupFilter,
        int searchVersion)
    {
        var messageDatabase = _qqDatabaseService.MessageDatabase;
        var groupInfoDatabase = _qqDatabaseService.GroupInfoDatabase;

        try
        {
            var result = await Task.Run(() =>
            {
                var counts = new Dictionary<string, GroupCountState>(StringComparer.Ordinal);
                long? beforeRowId = null;
                long scannedCount = 0;
                long lastStatusScannedCount = 0;

                while (searchVersion == _searchVersion)
                {
                    var rows = ftsDatabase.ScanMatches(new GroupMessageFtsMatchScanRequest(
                        query,
                        GroupCountScanPageSize,
                        beforeRowId));

                    if (rows.Count == 0)
                        break;

                    foreach (var row in rows)
                    {
                        var groupId = GetScanRowGroupId(row);
                        if (groupFilter is { } filteredGroupId && groupId != filteredGroupId)
                            continue;

                        var key = groupId != 0 ? groupId.ToString() : row.PeerUid ?? string.Empty;
                        if (key.Length == 0)
                            continue;

                        if (counts.TryGetValue(key, out var state))
                        {
                            state.MatchCount++;
                            continue;
                        }

                        counts[key] = new GroupCountState(groupId, row.PeerUid, 1);
                    }

                    scannedCount += rows.Count;
                    beforeRowId = rows[^1].RowId;

                    if (scannedCount - lastStatusScannedCount >= GroupCountStatusUpdateInterval)
                    {
                        lastStatusScannedCount = scannedCount;
                        _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (searchVersion == _searchVersion)
                                UpdateCountingStatus(scannedCount);
                        });
                    }

                    if (rows.Count < GroupCountScanPageSize)
                        break;
                }

                var groupIds = counts.Values
                    .Select(state => state.GroupId)
                    .Where(groupId => groupId != 0)
                    .Distinct()
                    .ToArray();
                var groupNames = LoadGroupNames(messageDatabase, groupInfoDatabase, groupIds);

                return new GroupCountResult(counts, groupNames, scannedCount);
            });

            if (searchVersion != _searchVersion)
                return;

            if (groupFilter is not null)
                _totalMatchCount = result.Counts.Values.Sum(static state => (long)state.MatchCount);

            ApplyExactGroupCounts(result);
            _isCountingGroups = false;
            _hasExactGroupCounts = true;
            UpdateStatusText();
        }
        catch (Exception ex)
        {
            if (searchVersion != _searchVersion)
                return;

            _isCountingGroups = false;
            foreach (var group in _groupMap.Values)
            {
                group.IsCounting = false;
            }

            StatusText = $"群统计失败: {ex.Message}";
        }
    }

    private void UpdateCountingStatus(long scannedCount)
    {
        if (!_isCountingGroups)
            return;

        var totalText = _totalMatchCount is { } totalCount
            ? $"共 {totalCount} 条匹配，"
            : string.Empty;
        StatusText = $"{totalText}已统计 {scannedCount} 条命中，正在统计每个群的完整数量";
    }

    private void ApplyExactGroupCounts(GroupCountResult result)
    {
        var existingKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in _groups)
        {
            var key = GetGroupKey(group);
            existingKeys.Add(key);
            if (result.Counts.TryGetValue(key, out var state))
                group.MatchCount = state.MatchCount;

            group.IsCounting = false;
        }

        var newGroups = new List<AvaGroupMessageSearchGroup>();
        foreach (var (key, state) in result.Counts)
        {
            if (existingKeys.Contains(key))
                continue;

            result.GroupNames.TryGetValue(state.GroupId, out var groupName);
            var group = new AvaGroupMessageSearchGroup
            {
                GroupId = state.GroupId,
                PeerUid = state.PeerUid,
                GroupName = groupName,
                MatchCount = state.MatchCount,
                QueryText = _currentQuery,
                IsCounting = false,
            };

            _groupMap[key] = group;
            newGroups.Add(group);
        }

        foreach (var group in newGroups
            .OrderByDescending(group => group.MatchCount)
            .ThenBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            _groups.Add(group);
        }

        if (SelectedGroup is null)
            SelectedGroup = _groups.FirstOrDefault();
    }

    private void AppendGroupPage(string groupKey, SearchPage page)
    {
        _groupNextBeforeRowIds[groupKey] = page.NextBeforeRowId;

        if (page.Results.Count == 0)
        {
            _groupsWithMoreResults.Remove(groupKey);
            return;
        }

        _allResults.AddRange(page.Results);

        if (page.HasMore)
            _groupsWithMoreResults.Add(groupKey);
        else
            _groupsWithMoreResults.Remove(groupKey);
    }

    [RelayCommand]
    public void Clear()
    {
        Interlocked.Increment(ref _searchVersion);
        IsSearching = false;
        IsLoadingMore = false;
        QueryText = string.Empty;
        GroupFilterText = string.Empty;
        _allResults = [];
        _discoveryResults.Clear();
        _groupMap.Clear();
        _groupNextBeforeRowIds.Clear();
        _groupsWithMoreResults.Clear();
        _groups.Clear();
        _results.Clear();
        SelectedGroup = null;
        _visibleResultsVersion++;
        _currentQuery = string.Empty;
        _currentGroupFilter = null;
        _nextDiscoveryBeforeRowId = null;
        _totalMatchCount = null;
        _isCountingGroups = false;
        _hasExactGroupCounts = false;
        _hasMoreResults = false;
        RefreshDatabaseState();
    }

    private async Task RefreshVisibleResultsAsync(AvaGroupMessageSearchGroup? selectedGroup, int version)
    {
        _results.Clear();
        if (selectedGroup is null)
            return;

        var groupKey = GetGroupKey(selectedGroup);
        if (!_groupNextBeforeRowIds.ContainsKey(groupKey))
        {
            var ftsDatabase = _qqDatabaseService.GroupMessageFtsDatabase;
            if (ftsDatabase is not null && selectedGroup.GroupId != 0)
            {
                SearchPage page;
                try
                {
                    IsLoadingMore = true;
                    var query = _currentQuery;
                    page = await Task.Run(() => LoadSearchPage(
                        ftsDatabase,
                        _qqDatabaseService.MessageDatabase,
                        _qqDatabaseService.GroupInfoDatabase,
                        query,
                        selectedGroup.GroupId,
                        beforeRowId: null));
                }
                catch (Exception ex)
                {
                    if (version == _visibleResultsVersion && ReferenceEquals(SelectedGroup, selectedGroup))
                        StatusText = $"加载群搜索结果失败: {ex.Message}";

                    return;
                }
                finally
                {
                    if (version == _visibleResultsVersion && ReferenceEquals(SelectedGroup, selectedGroup))
                        IsLoadingMore = false;
                }

                if (version != _visibleResultsVersion || !ReferenceEquals(SelectedGroup, selectedGroup))
                    return;

                AppendGroupPage(groupKey, page);
                UpdateStatusText();
            }
        }

        var visibleResults = _allResults
            .Where(result => IsGroupResult(result, selectedGroup))
            .ToList();

        if (visibleResults.Count == 0)
        {
            visibleResults = _discoveryResults
                .Where(result => IsGroupResult(result, selectedGroup))
                .ToList();
        }

        _results.AddRange(visibleResults);

        var messageDatabase = _qqDatabaseService.MessageDatabase;
        var senderInfos = await Task.Run(() => LoadSenderInfos(messageDatabase, visibleResults));

        if (version != _visibleResultsVersion || !ReferenceEquals(SelectedGroup, selectedGroup))
            return;

        _results.Clear();
        _results.AddRange(visibleResults.Select(result =>
        {
            senderInfos.TryGetValue(new SearchMessageKey(result.GroupId, result.MessageSeq), out var senderInfo);
            return ApplySenderInfo(result, senderInfo);
        }));
    }

    private static bool IsGroupResult(AvaGroupMessageSearchResult result, AvaGroupMessageSearchGroup group)
    {
        if (group.GroupId != 0)
            return result.GroupId == group.GroupId;

        return string.Equals(result.PeerUid, group.PeerUid, StringComparison.Ordinal);
    }

    private bool TryParseGroupFilter(out uint? groupId)
    {
        groupId = null;
        if (string.IsNullOrWhiteSpace(GroupFilterText))
            return true;

        if (uint.TryParse(GroupFilterText.Trim(), out var value) && value != 0)
        {
            groupId = value;
            return true;
        }

        StatusText = "群过滤只能填写群号";
        return false;
    }

    private Dictionary<uint, string> LoadGroupNames(
        QQMessageReader? messageDatabase,
        QQGroupInfoReader? groupInfoDatabase,
        IReadOnlyCollection<uint> groupIds)
    {
        var names = new Dictionary<uint, string>();
        if (groupIds.Count == 0)
            return names;

        if (messageDatabase is not null)
        {
            var groupIdTexts = groupIds.Select(groupId => groupId.ToString()).ToArray();
            lock (_messageDatabaseGate)
            {
                var recentGroups = messageDatabase.DbContext.RecentContacts
                    .Where(contact => contact.ChatType == ChatType.GroupMessage)
                    .Where(contact => contact.PeerUin != null && groupIdTexts.Contains(contact.PeerUin))
                    .Select(contact => new
                    {
                        contact.PeerUin,
                        contact.Source,
                    })
                    .ToList();

                foreach (var group in recentGroups)
                {
                    if (!uint.TryParse(group.PeerUin, out var recentGroupId) ||
                        string.IsNullOrWhiteSpace(group.Source) ||
                        names.ContainsKey(recentGroupId))
                    {
                        continue;
                    }

                    names[recentGroupId] = group.Source;
                }
            }
        }

        if (groupInfoDatabase is not null)
        {
            var groupList = groupIds.ToArray();
            lock (_groupInfoDatabaseGate)
            {
                var groupInfos = groupInfoDatabase.DbContext.GroupList
                    .Where(group => groupList.Contains(group.GroupId))
                    .Select(group => new
                    {
                        group.GroupId,
                        group.GroupName,
                    })
                    .ToList();

                foreach (var group in groupInfos)
                {
                    if (!string.IsNullOrWhiteSpace(group.GroupName))
                        names[group.GroupId] = group.GroupName;
                }
            }
        }

        return names;
    }

    private Dictionary<SearchMessageKey, SearchSenderInfo> LoadSenderInfos(
        QQMessageReader? messageDatabase,
        IReadOnlyCollection<AvaGroupMessageSearchResult> results)
    {
        if (messageDatabase is null || results.Count == 0)
            return new Dictionary<SearchMessageKey, SearchSenderInfo>();

        var groupIds = results
            .Select(result => result.GroupId)
            .Where(groupId => groupId != 0)
            .Distinct()
            .ToArray();
        var messageSeqs = results
            .Select(result => result.MessageSeq)
            .Where(messageSeq => messageSeq > 0)
            .Distinct()
            .ToArray();

        if (groupIds.Length == 0 || messageSeqs.Length == 0)
            return new Dictionary<SearchMessageKey, SearchSenderInfo>();

        var senderInfos = new Dictionary<SearchMessageKey, SearchSenderInfo>();
        lock (_messageDatabaseGate)
        {
            foreach (var messageSeqBatch in messageSeqs.Chunk(500))
            {
                var messages = messageDatabase.DbContext.GroupMessages
                    .Where(message => groupIds.Contains(message.GroupId))
                    .Where(message => messageSeqBatch.Contains(message.MessageSeq))
                    .Select(message => new
                    {
                        message.GroupId,
                        message.MessageSeq,
                        message.SenderId,
                        message.SendMemberName,
                        message.SendNickName,
                    })
                    .ToList();

                foreach (var message in messages)
                {
                    var key = new SearchMessageKey(message.GroupId, message.MessageSeq);
                    senderInfos.TryAdd(
                        key,
                        new SearchSenderInfo(
                            message.SenderId,
                            FirstNonEmpty(message.SendMemberName, message.SendNickName)));
                }
            }
        }

        return senderInfos;
    }

    private static uint GetSearchResultGroupId(GroupMessageFtsSearchResult result)
    {
        if (uint.TryParse(result.PeerUid, out var peerGroupId) && peerGroupId != 0)
            return peerGroupId;

        return result.GroupId;
    }

    private static uint GetScanRowGroupId(GroupMessageFtsMatchScanRow row)
    {
        if (uint.TryParse(row.PeerUid, out var peerGroupId) && peerGroupId != 0)
            return peerGroupId;

        return row.GroupId;
    }

    private void UpdateStatusText()
    {
        if (_discoveryResults.Count == 0)
        {
            StatusText = "没有找到匹配的群消息";
            return;
        }

        var suffix = _hasMoreResults ? "，继续向下滚动加载更多" : string.Empty;
        var totalText = _totalMatchCount is { } totalCount
            ? $"共 {totalCount} 条匹配，"
            : string.Empty;
        var groupText = _isCountingGroups
            ? $"已发现 {_groups.Count} 个群，正在统计每个群的完整数量"
            : $"来自 {_groups.Count} 个群";
        StatusText = $"{totalText}已加载 {_discoveryResults.Count} 条最新命中，{groupText}{suffix}";
        OnPropertyChanged(nameof(CanLoadMore));
    }

    private static AvaGroupMessageSearchGroup CreateGroup(
        AvaGroupMessageSearchResult first,
        string query)
    {
        return new AvaGroupMessageSearchGroup
        {
            GroupId = first.GroupId,
            PeerUid = first.PeerUid,
            GroupName = first.GroupName,
            MatchCount = 1,
            QueryText = query,
        };
    }

    private static string GetGroupKey(AvaGroupMessageSearchResult result)
    {
        return result.GroupId != 0 ? result.GroupId.ToString() : result.PeerUid ?? string.Empty;
    }

    private static string GetGroupKey(AvaGroupMessageSearchGroup group)
    {
        return group.GroupId != 0 ? group.GroupId.ToString() : group.PeerUid ?? string.Empty;
    }

    private static AvaGroupMessageSearchResult CreateResult(
        GroupMessageFtsSearchResult result,
        IReadOnlyDictionary<uint, string> groupNames)
    {
        var groupId = GetSearchResultGroupId(result);
        groupNames.TryGetValue(groupId, out var groupName);
        return new AvaGroupMessageSearchResult
        {
            MessageId = result.MessageId,
            MessageSeq = result.MessageSeq,
            MessageTime = result.MessageTime,
            GroupId = groupId,
            GroupName = groupName,
            PeerUid = result.PeerUid,
            SenderUid = result.SenderUid,
            PreviewText = string.IsNullOrWhiteSpace(result.PreviewText)
                ? "[空搜索文本]"
                : result.PreviewText,
        };
    }

    private static AvaGroupMessageSearchResult ApplySenderInfo(
        AvaGroupMessageSearchResult result,
        SearchSenderInfo senderInfo)
    {
        return new AvaGroupMessageSearchResult
        {
            MessageId = result.MessageId,
            MessageSeq = result.MessageSeq,
            MessageTime = result.MessageTime,
            GroupId = result.GroupId,
            GroupName = result.GroupName,
            PeerUid = result.PeerUid,
            SenderUid = result.SenderUid,
            SenderId = senderInfo.SenderId,
            SenderName = senderInfo.Name,
            PreviewText = result.PreviewText,
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }

    private readonly record struct SearchMessageKey(uint GroupId, long MessageSeq);

    private readonly record struct SearchSenderInfo(uint SenderId, string? Name);

    private sealed record SearchPage(
        List<AvaGroupMessageSearchResult> Results,
        bool HasMore,
        long? NextBeforeRowId);

    private sealed record InitialSearchResult(
        SearchPage Page,
        long? TotalCount);

    private sealed class GroupCountState(uint groupId, string? peerUid, int matchCount)
    {
        public uint GroupId { get; } = groupId;
        public string? PeerUid { get; } = peerUid;
        public int MatchCount { get; set; } = matchCount;
    }

    private sealed record GroupCountResult(
        Dictionary<string, GroupCountState> Counts,
        Dictionary<uint, string> GroupNames,
        long ScannedCount);
}
