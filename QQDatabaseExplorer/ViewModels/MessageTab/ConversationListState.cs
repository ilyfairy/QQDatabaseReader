using System;
using System.Collections.Generic;
using System.Linq;
using ObservableCollections;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class ConversationListState
{
    private readonly ObservableList<AvaQQGroup> _groups;
    private readonly ObservableList<AvaQQGroup> _filteredGroups;
    private readonly HashSet<string> _selectedConversationKeys = new(StringComparer.Ordinal);
    private string? _activeConversationKey;
    private string? _rangeAnchorKey;

    public ConversationListState(
        ObservableList<AvaQQGroup> groups,
        ObservableList<AvaQQGroup> filteredGroups)
    {
        _groups = groups;
        _filteredGroups = filteredGroups;
    }

    public string? ActiveConversationKey => _activeConversationKey;

    public IReadOnlyList<AvaQQGroup> SelectedGroups => _groups
        .Where(group => _selectedConversationKeys.Contains(group.ConversationKey))
        .ToArray();

    public bool IsSelected(string conversationKey)
    {
        return _selectedConversationKeys.Contains(conversationKey);
    }

    public bool IsActive(string conversationKey)
    {
        return string.Equals(_activeConversationKey, conversationKey, StringComparison.Ordinal);
    }

    public void RefreshFilteredGroups(string queryText)
    {
        var query = queryText.Trim();
        var nonEmptyGroups = _groups.Where(IsValidConversation);
        var groups = string.IsNullOrWhiteSpace(query)
            ? nonEmptyGroups
            : nonEmptyGroups.Where(group =>
                group.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (group.ConversationType is AvaConversationType.Group or AvaConversationType.PCQQGroup &&
                 group.GroupId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                (group.ConversationType is AvaConversationType.AndroidMobileQQGroup or AvaConversationType.AndroidMobileQQPrivate &&
                 (group.AndroidMobileQQPeerUin?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)) ||
                (group.ConversationType == AvaConversationType.Icalingua &&
                 group.IcalinguaRoomId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                (group.ConversationType is AvaConversationType.Private or AvaConversationType.PCQQPrivate &&
                 (group.PrivateConversationId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
                  group.PrivateUin.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
                  (group.PrivateUid?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))));

        ApplyFilteredGroups(groups
            .OrderByDescending(group => group.LatestMessageTime)
            .ThenBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray());
    }

    public bool SetRangeAnchor(AvaQQGroup? group, bool updateRangeAnchor)
    {
        if (group is null || !IsValidConversation(group))
            return false;

        if (updateRangeAnchor || _rangeAnchorKey is null)
            _rangeAnchorKey = group.ConversationKey;

        return true;
    }

    public bool SetActiveGroup(AvaQQGroup? group)
    {
        if (group is not null && group.ConversationKey == _activeConversationKey)
            return false;

        if (_activeConversationKey is { } previousActiveConversationKey &&
            _groups.FirstOrDefault(item => item.ConversationKey == previousActiveConversationKey) is { } previousGroup)
        {
            previousGroup.IsActive = false;
        }

        _activeConversationKey = group?.ConversationKey;
        if (group is null)
            return false;

        group.IsActive = true;
        if (_selectedConversationKeys.Contains(group.ConversationKey))
            return false;

        _selectedConversationKeys.Clear();
        _selectedConversationKeys.Add(group.ConversationKey);
        ApplyGroupSelectionState();
        return true;
    }

    public bool SelectOnly(AvaQQGroup? group)
    {
        if (group is null || !IsValidConversation(group))
            return false;

        _selectedConversationKeys.Clear();
        _selectedConversationKeys.Add(group.ConversationKey);
        ApplyGroupSelectionState();
        return true;
    }

    public bool Toggle(AvaQQGroup? group)
    {
        if (group is null || !IsValidConversation(group))
            return false;

        _rangeAnchorKey = group.ConversationKey;
        if (!_selectedConversationKeys.Remove(group.ConversationKey))
            _selectedConversationKeys.Add(group.ConversationKey);

        if (_selectedConversationKeys.Count == 0)
            _selectedConversationKeys.Add(group.ConversationKey);

        ApplyGroupSelectionState();
        return true;
    }

    public bool SelectRange(AvaQQGroup? group, bool preserveSelection)
    {
        if (group is null || !IsValidConversation(group))
            return false;

        var visibleGroups = _filteredGroups.ToArray();
        var targetIndex = Array.IndexOf(visibleGroups, group);
        if (targetIndex < 0)
            return SelectOnly(group);

        var anchorGroup = _rangeAnchorKey is { } anchorKey
            ? visibleGroups.FirstOrDefault(item => item.ConversationKey == anchorKey)
            : null;
        var anchorIndex = anchorGroup is null ? -1 : Array.IndexOf(visibleGroups, anchorGroup);
        if (anchorIndex < 0)
            anchorIndex = 0;

        if (!preserveSelection)
            _selectedConversationKeys.Clear();

        var start = Math.Min(anchorIndex, targetIndex);
        var end = Math.Max(anchorIndex, targetIndex);
        for (var i = start; i <= end; i++)
        {
            if (IsValidConversation(visibleGroups[i]))
                _selectedConversationKeys.Add(visibleGroups[i].ConversationKey);
        }

        ApplyGroupSelectionState();
        return true;
    }

    public void Clear()
    {
        _selectedConversationKeys.Clear();
        _activeConversationKey = null;
        _rangeAnchorKey = null;
        foreach (var group in _groups)
        {
            group.IsSelected = false;
            group.IsActive = false;
        }
    }

    public void ApplyGroupSelectionState()
    {
        foreach (var group in _groups)
        {
            group.IsSelected = _selectedConversationKeys.Contains(group.ConversationKey);
            group.IsActive = IsActive(group.ConversationKey);
        }
    }

    public static bool IsValidConversation(AvaQQGroup group)
    {
        return group.ConversationType switch
        {
            AvaConversationType.Group => group.GroupId != 0,
            AvaConversationType.Private => group.PrivateConversationId != 0,
            AvaConversationType.PCQQGroup => group.GroupId != 0 && !string.IsNullOrWhiteSpace(group.PCQQTableName),
            AvaConversationType.PCQQPrivate => group.PrivateUin != 0 && !string.IsNullOrWhiteSpace(group.PCQQTableName),
            AvaConversationType.AndroidMobileQQGroup or AvaConversationType.AndroidMobileQQPrivate =>
                !string.IsNullOrWhiteSpace(group.AndroidMobileQQPeerUin) &&
                !string.IsNullOrWhiteSpace(group.AndroidMobileQQTableName),
            AvaConversationType.Icalingua => group.IcalinguaRoomId != 0,
            _ => false,
        };
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
                _filteredGroups.Move(currentIndex, targetIndex);
            else
                _filteredGroups.Insert(targetIndex, targetGroup);
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
}
