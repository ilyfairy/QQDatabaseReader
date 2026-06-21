using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels;

internal sealed class IcalinguaMessageSearchProvider : IConversationMessageSearchProvider
{
    private const int SearchPageSize = 100;
    private readonly IcalinguaMessageDatabaseSet _database;

    public IcalinguaMessageSearchProvider(IcalinguaMessageDatabaseSet database)
    {
        _database = database;
    }

    public SearchDatabaseKind Kind => SearchDatabaseKind.Icalingua;

    public SearchPage SearchDiscovery(
        string query,
        ConversationSearchFilter filter,
        SearchCursor? cursor)
    {
        return SearchPage(
            query,
            filter.IcalinguaRoomIds,
            cursor,
            exactRoomId: false);
    }

    public SearchPage SearchConversation(
        string query,
        AvaGroupMessageSearchGroup conversation,
        SearchCursor? cursor)
    {
        return SearchPage(
            query,
            [conversation.IcalinguaRoomId],
            cursor,
            exactRoomId: true);
    }

    private SearchPage SearchPage(
        string query,
        IReadOnlyList<long> roomIds,
        SearchCursor? cursor,
        bool exactRoomId)
    {
        var selectedRoomId = roomIds.Count == 1 ? roomIds[0] : 0;
        var results = exactRoomId && selectedRoomId != 0
            ? _database.SearchMessagesInRoom(selectedRoomId, query, cursor?.IcalinguaCursor, SearchPageSize)
            : _database.SearchMessages(query, roomIds, SearchPageSize, cursor?.IcalinguaCursor);

        var conversations = _database.GetConversationsByRoomIds(results.Select(static result => result.RoomId));
        var viewResults = results
            .Select(result => CreateResult(result, conversations))
            .ToList();

        return new SearchPage(
            viewResults,
            results.Count == SearchPageSize,
            results.LastOrDefault() is { } lastResult
                ? new SearchCursor(IcalinguaCursor: new IcalinguaMessageSearchCursor(lastResult.MessageSeq, lastResult.MessageId))
                : null);
    }

    private static AvaGroupMessageSearchResult CreateResult(
        IcalinguaMessageRecord result,
        IReadOnlyDictionary<long, IcalinguaConversation> conversations)
    {
        conversations.TryGetValue(result.RoomId, out var conversation);
        return new AvaGroupMessageSearchResult
        {
            ConversationType = AvaConversationType.Icalingua,
            MessageId = result.MessageId,
            MessageSeq = result.MessageSeq,
            MessageTime = result.MessageTime,
            IcalinguaRoomId = result.RoomId,
            GroupName = conversation?.DisplayName,
            SenderId = result.SenderId,
            SenderName = result.Username,
            PreviewText = SearchResultPreviewText.Normalize(result.PreviewText),
        };
    }
}
