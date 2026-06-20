using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels;

internal sealed class QqNtFtsMessageSearchProvider : IConversationMessageSearchProvider
{
    private const int SearchPageSize = 100;
    private readonly QQGroupMessageFtsReader _ftsDatabase;
    private readonly QqNtSearchMetadataLoader _metadataLoader;

    public QqNtFtsMessageSearchProvider(
        QQGroupMessageFtsReader ftsDatabase,
        QqNtSearchMetadataLoader metadataLoader)
    {
        _ftsDatabase = ftsDatabase;
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
            null,
            conversation.PrivateConversationId == 0 ? [] : [conversation.PrivateConversationId],
            cursor);
    }

    private SearchPage SearchPage(
        string query,
        uint? groupId,
        uint? peerUin,
        IReadOnlyList<long> privateConversationIds,
        SearchCursor? cursor)
    {
        var results = _ftsDatabase.Search(new GroupMessageFtsSearchRequest(
            query,
            groupId,
            SearchPageSize,
            Order: GroupMessageFtsSearchOrder.Newest,
            BeforeRowId: cursor?.QqNtBeforeRowId,
            PrivateConversationIds: privateConversationIds,
            PeerUin: peerUin));

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
            results.Count == SearchPageSize,
            results.LastOrDefault() is { } lastResult ? new SearchCursor(QqNtBeforeRowId: lastResult.RowId) : null);
    }

    private static uint? GetSelectedGroupFilter(AvaGroupMessageSearchGroup group)
    {
        return group.GroupId == 0 ? null : group.GroupId;
    }
}
