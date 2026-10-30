using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
        SearchCursor? cursor,
        CancellationToken cancellationToken)
    {
        return SearchPage(
            query,
            filter.IcalinguaRoomIds,
            cursor,
            exactRoomId: false,
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
            [conversation.IcalinguaRoomId],
            cursor,
            exactRoomId: true,
            cancellationToken);
    }

    private SearchPage SearchPage(
        string query,
        IReadOnlyList<long> roomIds,
        SearchCursor? cursor,
        bool exactRoomId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selectedRoomId = roomIds.Count == 1 ? roomIds[0] : 0;
        var page = exactRoomId && selectedRoomId != 0
            ? _database.SearchMessagesInRoomPage(
                selectedRoomId,
                query,
                SearchPageSize,
                cursor?.IcalinguaCursor,
                cancellationToken)
            : _database.SearchMessagesPage(
                query,
                roomIds,
                SearchPageSize,
                cursor?.IcalinguaCursor,
                cancellationToken);

        var conversations = _database.GetConversationsByRoomIds(page.Messages.Select(static result => result.RoomId));
        var viewResults = page.Messages
            .Select(result => CreateResult(result, conversations))
            .ToList();
        cancellationToken.ThrowIfCancellationRequested();

        return new SearchPage(
            viewResults,
            page.HasMore,
            page.NextCursor is { } nextCursor
                ? new SearchCursor(IcalinguaCursor: nextCursor)
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
