using System;
using System.Collections.Generic;
using System.Linq;
using ObservableCollections;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.ViewModels;

internal sealed class ConversationMessageSearchResultStore
{
    private readonly ObservableList<AvaGroupMessageSearchGroup> _groups;
    private List<AvaGroupMessageSearchResult> _allResults = [];
    private readonly List<AvaGroupMessageSearchResult> _discoveryResults = [];
    private readonly Dictionary<string, AvaGroupMessageSearchGroup> _groupMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SearchCursor?> _groupNextCursors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _groupsWithMoreResults = new(StringComparer.Ordinal);

    public ConversationMessageSearchResultStore(ObservableList<AvaGroupMessageSearchGroup> groups)
    {
        _groups = groups;
    }

    public IReadOnlyList<AvaGroupMessageSearchResult> DiscoveryResults => _discoveryResults;

    public int DiscoveryResultCount => _discoveryResults.Count;

    public int GroupCount => _groups.Count;

    public SearchCursor? NextDiscoveryCursor { get; private set; }

    public bool HasMoreDiscoveryResults { get; private set; }

    public bool IsDiscoveryComplete { get; private set; }

    public bool HasConversationCursor(AvaGroupMessageSearchGroup group)
    {
        return _groupNextCursors.ContainsKey(GetGroupKey(group));
    }

    public bool HasMoreConversationResults(AvaGroupMessageSearchGroup group)
    {
        return _groupsWithMoreResults.Contains(GetGroupKey(group));
    }

    public bool TryGetConversationCursor(AvaGroupMessageSearchGroup group, out string groupKey, out SearchCursor? cursor)
    {
        groupKey = GetGroupKey(group);
        return _groupNextCursors.TryGetValue(groupKey, out cursor);
    }

    public AvaGroupMessageSearchGroup? AppendDiscoveryPage(
        SearchPage page,
        string query,
        bool isCountingGroups,
        bool hasExactGroupCounts,
        AvaGroupMessageSearchGroup? selectedGroup,
        bool sortGroups)
    {
        if (page.Results.Count == 0)
        {
            HasMoreDiscoveryResults = false;
            return selectedGroup;
        }

        _discoveryResults.AddRange(page.Results);
        NextDiscoveryCursor = page.NextCursor;
        HasMoreDiscoveryResults = page.HasMore;

        foreach (var result in page.Results)
        {
            var key = GetGroupKey(result);
            if (_groupMap.TryGetValue(key, out var group))
            {
                if (!hasExactGroupCounts)
                    group.MatchCount++;
                continue;
            }

            group = CreateGroup(result, query);
            group.IsCounting = isCountingGroups;
            _groupMap.Add(key, group);
            _groups.Add(group);
        }

        return sortGroups
            ? SortGroups(selectedGroup)
            : selectedGroup ?? _groups.FirstOrDefault();
    }

    public AvaGroupMessageSearchGroup? MarkDiscoveryComplete(AvaGroupMessageSearchGroup? selectedGroup)
    {
        IsDiscoveryComplete = true;
        HasMoreDiscoveryResults = false;
        foreach (var group in _groupMap.Values)
        {
            group.IsCounting = false;
        }

        return SortGroups(selectedGroup);
    }

    public AvaGroupMessageSearchGroup? SortGroups(AvaGroupMessageSearchGroup? selectedGroup)
    {
        var selectedKey = selectedGroup is null ? null : GetGroupKey(selectedGroup);
        var groups = _groupMap.Values
            .OrderByDescending(group => group.MatchCount)
            .ThenBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        _groups.Clear();
        _groups.AddRange(groups);

        return selectedKey is not null && _groupMap.TryGetValue(selectedKey, out var refreshedSelectedGroup)
            ? refreshedSelectedGroup
            : selectedGroup;
    }

    public void AppendGroupPage(string groupKey, SearchPage page)
    {
        _groupNextCursors[groupKey] = page.NextCursor;

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

    public IReadOnlyList<AvaGroupMessageSearchResult> GetVisibleResults(AvaGroupMessageSearchGroup group)
    {
        var visibleResults = _allResults
            .Where(result => IsGroupResult(result, group))
            .ToList();

        if (visibleResults.Count != 0)
            return visibleResults;

        return _discoveryResults
            .Where(result => IsGroupResult(result, group))
            .ToList();
    }

    public int GetVisibleResultCount(AvaGroupMessageSearchGroup group)
    {
        var allResultCount = _allResults.Count(result => IsGroupResult(result, group));
        return allResultCount != 0
            ? allResultCount
            : _discoveryResults.Count(result => IsGroupResult(result, group));
    }

    public void Clear()
    {
        _allResults = [];
        _discoveryResults.Clear();
        _groupMap.Clear();
        _groupNextCursors.Clear();
        _groupsWithMoreResults.Clear();
        _groups.Clear();
        NextDiscoveryCursor = null;
        HasMoreDiscoveryResults = false;
        IsDiscoveryComplete = false;
    }

    public static string GetGroupKey(AvaGroupMessageSearchResult result)
    {
        return result.ConversationType switch
        {
            AvaConversationType.Group when result.GroupId != 0 => SearchConversationKey.Group(result.GroupId),
            AvaConversationType.Private when result.PrivateConversationId != 0 => SearchConversationKey.Private(result.PrivateConversationId),
            AvaConversationType.PCQQGroup when result.GroupId != 0 => SearchConversationKey.PCQQGroup(result.GroupId),
            AvaConversationType.PCQQPrivate when result.PeerUin != 0 => SearchConversationKey.PCQQPrivate(result.PeerUin),
            AvaConversationType.AndroidMobileQQGroup or AvaConversationType.AndroidMobileQQPrivate =>
                SearchConversationKey.AndroidMobileQQ(result.ConversationType, result.AndroidMobileQQPeerUin ?? string.Empty),
            AvaConversationType.Icalingua when result.IcalinguaRoomId != 0 => SearchConversationKey.Icalingua(result.IcalinguaRoomId),
            _ => result.PeerUid ?? string.Empty,
        };
    }

    public static string GetGroupKey(AvaGroupMessageSearchGroup group)
    {
        return group.ConversationType switch
        {
            AvaConversationType.Group when group.GroupId != 0 => SearchConversationKey.Group(group.GroupId),
            AvaConversationType.Private when group.PrivateConversationId != 0 => SearchConversationKey.Private(group.PrivateConversationId),
            AvaConversationType.PCQQGroup when group.GroupId != 0 => SearchConversationKey.PCQQGroup(group.GroupId),
            AvaConversationType.PCQQPrivate when group.PeerUin != 0 => SearchConversationKey.PCQQPrivate(group.PeerUin),
            AvaConversationType.AndroidMobileQQGroup or AvaConversationType.AndroidMobileQQPrivate =>
                SearchConversationKey.AndroidMobileQQ(group.ConversationType, group.AndroidMobileQQPeerUin ?? string.Empty),
            AvaConversationType.Icalingua when group.IcalinguaRoomId != 0 => SearchConversationKey.Icalingua(group.IcalinguaRoomId),
            _ => group.PeerUid ?? string.Empty,
        };
    }

    private static bool IsGroupResult(AvaGroupMessageSearchResult result, AvaGroupMessageSearchGroup group)
    {
        return string.Equals(GetGroupKey(result), GetGroupKey(group), StringComparison.Ordinal);
    }

    private static AvaGroupMessageSearchGroup CreateGroup(
        AvaGroupMessageSearchResult first,
        string query)
    {
        return new AvaGroupMessageSearchGroup
        {
            ConversationType = first.ConversationType,
            GroupId = first.GroupId,
            PrivateConversationId = first.PrivateConversationId,
            PeerUin = first.PeerUin,
            IcalinguaRoomId = first.IcalinguaRoomId,
            PCQQTableName = first.PCQQTableName,
            AndroidMobileQQTableName = first.AndroidMobileQQTableName,
            AndroidMobileQQPeerUin = first.AndroidMobileQQPeerUin,
            PeerUid = first.PeerUid,
            GroupName = first.GroupName,
            MatchCount = 1,
            QueryText = query,
        };
    }

}
