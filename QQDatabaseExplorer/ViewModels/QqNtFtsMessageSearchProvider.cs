using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels;

internal sealed class QqNtFtsMessageSearchProvider : IConversationMessageSearchProvider
{
    private const int SearchPageSize = 100;
    private readonly QQGroupMessageFtsReader? _groupFtsDatabase;
    private readonly QQGroupMessageFtsReader? _buddyFtsDatabase;
    private readonly QqNtSearchMetadataLoader _metadataLoader;

    public QqNtFtsMessageSearchProvider(
        QQGroupMessageFtsReader? groupFtsDatabase,
        QQGroupMessageFtsReader? buddyFtsDatabase,
        QqNtSearchMetadataLoader metadataLoader)
    {
        _groupFtsDatabase = groupFtsDatabase;
        _buddyFtsDatabase = buddyFtsDatabase;
        _metadataLoader = metadataLoader;
    }

    public SearchDatabaseKind Kind => SearchDatabaseKind.GroupMessageFts;

    public SearchPage SearchDiscovery(
        string query,
        ConversationSearchFilter filter,
        SearchCursor? cursor)
    {
        return SearchPage(
            query,
            filter.GroupOrPeerId,
            filter.GroupOrPeerId,
            filter.PrivateConversationIds,
            searchGroupMessages: true,
            searchBuddyMessages: true,
            cursor);
    }

    public SearchPage SearchConversation(
        string query,
        AvaGroupMessageSearchGroup conversation,
        SearchCursor? cursor)
    {
        return SearchPage(
            query,
            GetSelectedGroupFilter(conversation),
            conversation.PeerUin == 0 ? null : conversation.PeerUin,
            conversation.PrivateConversationId == 0 ? [] : [conversation.PrivateConversationId],
            searchGroupMessages: conversation.ConversationType == AvaConversationType.Group,
            searchBuddyMessages: conversation.ConversationType == AvaConversationType.Private,
            cursor);
    }

    private SearchPage SearchPage(
        string query,
        uint? groupId,
        uint? peerUin,
        IReadOnlyList<long> privateConversationIds,
        bool searchGroupMessages,
        bool searchBuddyMessages,
        SearchCursor? cursor)
    {
        var searchPage = SearchFtsDatabases(
            query,
            groupId,
            peerUin,
            privateConversationIds,
            searchGroupMessages,
            searchBuddyMessages,
            cursor);
        var results = searchPage.Results;

        var groupIds = results
            .Select(QqNtSearchMetadataLoader.GetSearchResultGroupId)
            .Where(groupId => groupId != 0)
            .Distinct()
            .ToArray();
        var groupNames = _metadataLoader.LoadGroupNames(groupIds);
        var privateConversationInfos = _metadataLoader.LoadPrivateConversationInfos(results);
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
        IReadOnlyList<long> privateConversationIds,
        bool searchGroupMessages,
        bool searchBuddyMessages,
        SearchCursor? cursor)
    {
        var groupResults = searchGroupMessages
            ? SearchGroupFts(query, groupId, cursor?.QqNtBeforeRowId)
            : [];
        var buddyResults = searchBuddyMessages
            ? SearchBuddyFts(query, peerUin, privateConversationIds, cursor?.QqNtBuddyBeforeRowId)
            : [];
        if (groupResults.Count == 0 && buddyResults.Count == 0)
            return new QqNtFtsPage([], false, null);

        var mergedResults = groupResults
            .Concat(buddyResults)
            .OrderByDescending(static result => result.MessageTime)
            .ThenByDescending(static result => result.MessageId)
            .ThenByDescending(static result => result.RowId)
            .ToList();

        var lastGroupRowId = groupResults.Count == 0 ? null : (long?)groupResults[^1].RowId;
        var lastBuddyRowId = buddyResults.Count == 0 ? null : (long?)buddyResults[^1].RowId;
        var nextGroupCursor = lastGroupRowId ?? cursor?.QqNtBeforeRowId;
        var nextBuddyCursor = lastBuddyRowId ?? cursor?.QqNtBuddyBeforeRowId;
        var hasMore =
            groupResults.Count == SearchPageSize ||
            buddyResults.Count == SearchPageSize;

        return new QqNtFtsPage(
            mergedResults,
            hasMore,
            hasMore ? new SearchCursor(QqNtBeforeRowId: nextGroupCursor, QqNtBuddyBeforeRowId: nextBuddyCursor) : null);
    }

    private IReadOnlyList<GroupMessageFtsSearchResult> SearchGroupFts(
        string query,
        uint? groupId,
        long? beforeRowId)
    {
        if (_groupFtsDatabase is null)
            return [];

        return _groupFtsDatabase.Search(new GroupMessageFtsSearchRequest(
            query,
            groupId,
            SearchPageSize,
            Order: GroupMessageFtsSearchOrder.Newest,
            BeforeRowId: beforeRowId));
    }

    private IReadOnlyList<GroupMessageFtsSearchResult> SearchBuddyFts(
        string query,
        uint? peerUin,
        IReadOnlyList<long> privateConversationIds,
        long? beforeRowId)
    {
        if (_buddyFtsDatabase is null)
            return [];

        return _buddyFtsDatabase.Search(new GroupMessageFtsSearchRequest(
            query,
            GroupId: null,
            Limit: SearchPageSize,
            Order: GroupMessageFtsSearchOrder.Newest,
            BeforeRowId: beforeRowId,
            PrivateConversationIds: privateConversationIds,
            PeerUin: peerUin));
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
