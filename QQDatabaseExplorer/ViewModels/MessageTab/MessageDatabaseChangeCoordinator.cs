using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class MessageDatabaseChangeCoordinator
{
    private readonly MessageConversationDatabaseSynchronizer _conversationDatabaseSynchronizer;
    private readonly MessageParticipantResolver _participantResolver;
    private readonly MessageProfileInfoApplier _profileInfoApplier;
    private readonly MessageSenderCache _senderCache;
    private readonly Func<bool> _hasGroupInfoDatabase;
    private readonly Func<bool> _hasQQNtMessageDatabase;
    private readonly Func<IEnumerable<AvaQQGroup>> _getConversations;
    private readonly Func<IEnumerable<AvaQQMessage>> _getVisibleMessages;
    private readonly Func<AvaQQGroup?> _getSelectedConversation;
    private readonly Action _clearMessageSelection;
    private readonly Action _clearMessages;
    private readonly Action _clearGroupSelection;
    private readonly Action _refreshFilteredGroups;

    public MessageDatabaseChangeCoordinator(
        MessageConversationDatabaseSynchronizer conversationDatabaseSynchronizer,
        MessageParticipantResolver participantResolver,
        MessageProfileInfoApplier profileInfoApplier,
        MessageSenderCache senderCache,
        Func<bool> hasGroupInfoDatabase,
        Func<bool> hasQQNtMessageDatabase,
        Func<IEnumerable<AvaQQGroup>> getConversations,
        Func<IEnumerable<AvaQQMessage>> getVisibleMessages,
        Func<AvaQQGroup?> getSelectedConversation,
        Action clearMessageSelection,
        Action clearMessages,
        Action clearGroupSelection,
        Action refreshFilteredGroups)
    {
        _conversationDatabaseSynchronizer = conversationDatabaseSynchronizer;
        _participantResolver = participantResolver;
        _profileInfoApplier = profileInfoApplier;
        _senderCache = senderCache;
        _hasGroupInfoDatabase = hasGroupInfoDatabase;
        _hasQQNtMessageDatabase = hasQQNtMessageDatabase;
        _getConversations = getConversations;
        _getVisibleMessages = getVisibleMessages;
        _getSelectedConversation = getSelectedConversation;
        _clearMessageSelection = clearMessageSelection;
        _clearMessages = clearMessages;
        _clearGroupSelection = clearGroupSelection;
        _refreshFilteredGroups = refreshFilteredGroups;
    }

    public void HandleDatabaseAdded(IQQDatabase database)
    {
        if (database is QQProfileInfoReader)
        {
            _participantResolver.InvalidateProfileInfoCache();
        }
    }

    public async Task HandleDatabaseAddedAsync(IQQDatabase database)
    {
        try
        {
            if (await _conversationDatabaseSynchronizer.TryApplyAsync(database))
            {
                _refreshFilteredGroups();
            }
            else if (database is QQProfileInfoReader profileInfoDatabase)
            {
                await Task.Run(() => _participantResolver.PreloadProfileInfoCache(profileInfoDatabase));
                ApplyProfileInfoNames();
            }
        }
        catch
        {
        }
    }

    public void HandleDatabaseRemoved(IQQDatabase database)
    {
        var removal = _conversationDatabaseSynchronizer.TryRemove(
            database,
            _hasGroupInfoDatabase(),
            _hasQQNtMessageDatabase());
        if (removal.Handled)
        {
            ApplyConversationDatabaseRemoval(removal);
        }
        else if (database is QQProfileInfoReader)
        {
            _participantResolver.InvalidateProfileInfoCache();
            ApplyProfileInfoNames();
        }
    }

    private void ApplyProfileInfoNames()
    {
        if (_profileInfoApplier.Apply(_getConversations(), _getVisibleMessages(), _getSelectedConversation()))
        {
            _refreshFilteredGroups();
        }
    }

    private void ApplyConversationDatabaseRemoval(ConversationDatabaseRemovalResult removal)
    {
        if (removal.ClearMessageSelection)
            _clearMessageSelection();

        if (removal.ClearMessages)
            _clearMessages();

        if (removal.ClearSenderCache)
            _senderCache.Clear();

        if (removal.ClearAvatarPathCaches)
            _participantResolver.ClearAvatarPathCaches();

        if (removal.ClearGroupSelection)
            _clearGroupSelection();

        if (removal.RefreshFilteredGroups)
            _refreshFilteredGroups();
    }
}
