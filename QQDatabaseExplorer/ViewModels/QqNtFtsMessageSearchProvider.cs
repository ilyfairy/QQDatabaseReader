using System.Collections.Generic;
using System.Linq;
using System.Threading;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels;

internal sealed class QqNtFtsMessageSearchProvider : IConversationMessageSearchProvider
{
    private const int SearchPageSize = 100;
    private const int MaxFtsScanRowsPerPage = 5000;
    private readonly QQGroupMessageFtsReader? _groupFtsDatabase;
    private readonly QQGroupMessageFtsReader? _buddyFtsDatabase;
    private readonly QqNtSearchMetadataLoader _metadataLoader;
    private readonly QQNtDatabaseQueryGate _queryGate;

    public QqNtFtsMessageSearchProvider(
        QQGroupMessageFtsReader? groupFtsDatabase,
        QQGroupMessageFtsReader? buddyFtsDatabase,
        QqNtSearchMetadataLoader metadataLoader,
        QQNtDatabaseQueryGate queryGate)
    {
        _groupFtsDatabase = groupFtsDatabase;
        _buddyFtsDatabase = buddyFtsDatabase;
        _metadataLoader = metadataLoader;
        _queryGate = queryGate;
    }

    public SearchDatabaseKind Kind => SearchDatabaseKind.GroupMessageFts;

    public SearchPage SearchDiscovery(
        string query,
        ConversationSearchFilter filter,
        SearchCursor? cursor,
        CancellationToken cancellationToken)
    {
        return SearchPage(
            query,
            filter.GroupOrPeerId,
            filter.GroupOrPeerId,
            filter.PrivatePeerUid,
            filter.PrivateConversationIds,
            searchGroupMessages: true,
            searchBuddyMessages: true,
            cursor,
            cancellationToken);
    }

    public SearchPage SearchConversation(
        string query,
        AvaGroupMessageSearchGroup conversation,
        SearchCursor? cursor,
        CancellationToken cancellationToken)
    {
        return SearchPage(
            query,
            GetSelectedGroupFilter(conversation),
            conversation.PeerUin == 0 ? null : conversation.PeerUin,
            conversation.PeerUid,
            conversation.PrivateConversationId == 0 ? [] : [conversation.PrivateConversationId],
            searchGroupMessages: conversation.ConversationType == AvaConversationType.Group,
            searchBuddyMessages: conversation.ConversationType == AvaConversationType.Private,
            cursor,
            cancellationToken);
    }

    private SearchPage SearchPage(
        string query,
        uint? groupId,
        uint? peerUin,
        string? peerUid,
        IReadOnlyList<long> privateConversationIds,
        bool searchGroupMessages,
        bool searchBuddyMessages,
        SearchCursor? cursor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var searchPage = SearchFtsDatabases(
            query,
            groupId,
            peerUin,
            peerUid,
            privateConversationIds,
            searchGroupMessages,
            searchBuddyMessages,
            cursor,
            cancellationToken);
        var results = searchPage.Results;

        var groupIds = results
            .Select(QqNtSearchMetadataLoader.GetSearchResultGroupId)
            .Where(groupId => groupId != 0)
            .Distinct()
            .ToArray();
        var groupNames = _metadataLoader.LoadGroupNames(groupIds);
        var privateConversationInfos = _metadataLoader.LoadPrivateConversationInfos(results);
        cancellationToken.ThrowIfCancellationRequested();
        var viewResults = results
            .Select(result => QqNtSearchResultMapper.CreateResult(
                result,
                groupNames,
                privateConversationInfos))
            .ToList();

        return new SearchPage(
            viewResults,
            searchPage.HasMore,
            searchPage.NextCursor);
    }

    private QqNtFtsPage SearchFtsDatabases(
        string query,
        uint? groupId,
        uint? peerUin,
        string? peerUid,
        IReadOnlyList<long> privateConversationIds,
        bool searchGroupMessages,
        bool searchBuddyMessages,
        SearchCursor? cursor,
        CancellationToken cancellationToken)
    {
        var groupPage = searchGroupMessages
            ? SearchGroupFts(query, groupId, cursor?.QqNtBeforeRowId, cancellationToken)
            : new GroupMessageFtsSearchPage([], null, false);
        var buddyPage = searchBuddyMessages
            ? SearchBuddyFts(
                query,
                peerUin,
                peerUid,
                privateConversationIds,
                cursor?.QqNtBuddyBeforeRowId,
                cancellationToken)
            : new GroupMessageFtsSearchPage([], null, false);
        var groupResults = groupPage.Results;
        var buddyResults = buddyPage.Results;
        if (groupResults.Count == 0 && buddyResults.Count == 0)
        {
            var hasEmptyPageContinuation = groupPage.HasMore || buddyPage.HasMore;
            return new QqNtFtsPage(
                [],
                hasEmptyPageContinuation,
                hasEmptyPageContinuation
                    ? new SearchCursor(
                        QqNtBeforeRowId: groupPage.NextBeforeRowId ?? cursor?.QqNtBeforeRowId,
                        QqNtBuddyBeforeRowId: buddyPage.NextBeforeRowId ?? cursor?.QqNtBuddyBeforeRowId)
                    : null);
        }

        var mergedResults = groupResults
            .Concat(buddyResults)
            .OrderByDescending(static result => result.MessageTime)
            .ThenByDescending(static result => result.MessageId)
            .ThenByDescending(static result => result.RowId)
            .ToList();

        var nextGroupCursor = groupPage.NextBeforeRowId ?? cursor?.QqNtBeforeRowId;
        var nextBuddyCursor = buddyPage.NextBeforeRowId ?? cursor?.QqNtBuddyBeforeRowId;
        var hasMore = groupPage.HasMore || buddyPage.HasMore;

        return new QqNtFtsPage(
            mergedResults,
            hasMore,
            hasMore ? new SearchCursor(QqNtBeforeRowId: nextGroupCursor, QqNtBuddyBeforeRowId: nextBuddyCursor) : null);
    }

    private GroupMessageFtsSearchPage SearchGroupFts(
        string query,
        uint? groupId,
        long? beforeRowId,
        CancellationToken cancellationToken)
    {
        if (_groupFtsDatabase is null)
            return new GroupMessageFtsSearchPage([], null, false);

        return _queryGate.Run(() =>
            _groupFtsDatabase.SearchPage(
                new GroupMessageFtsSearchRequest(
                    query,
                    groupId,
                    SearchPageSize,
                    Order: GroupMessageFtsSearchOrder.Newest,
                    BeforeRowId: beforeRowId),
                MaxFtsScanRowsPerPage,
                cancellationToken),
            cancellationToken);
    }

    private GroupMessageFtsSearchPage SearchBuddyFts(
        string query,
        uint? peerUin,
        string? peerUid,
        IReadOnlyList<long> privateConversationIds,
        long? beforeRowId,
        CancellationToken cancellationToken)
    {
        if (_buddyFtsDatabase is null)
            return new GroupMessageFtsSearchPage([], null, false);

        return _queryGate.Run(() =>
            _buddyFtsDatabase.SearchPage(
                new GroupMessageFtsSearchRequest(
                    query,
                    GroupId: null,
                    Limit: SearchPageSize,
                    Order: GroupMessageFtsSearchOrder.Newest,
                    BeforeRowId: beforeRowId,
                    PrivateConversationIds: privateConversationIds,
                    PeerUin: peerUin,
                    PeerUid: peerUid),
                MaxFtsScanRowsPerPage,
                cancellationToken),
            cancellationToken);
    }

    private static uint? GetSelectedGroupFilter(AvaGroupMessageSearchGroup group)
    {
        return group.GroupId == 0 ? null : group.GroupId;
    }
}

internal sealed record QqNtFtsPage(
    List<GroupMessageFtsSearchResult> Results,
    bool HasMore,
    SearchCursor? NextCursor);
