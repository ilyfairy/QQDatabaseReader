using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.ViewModels.MessageTab;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels;

internal sealed class AndroidMobileQQMessageSearchProvider : IConversationMessageSearchProvider
{
    private const int SearchPageSize = 100;
    private readonly AndroidMobileQQMessageReader _database;
    private readonly IReadOnlyList<AndroidMobileQQConversation> _conversations;

    public AndroidMobileQQMessageSearchProvider(AndroidMobileQQMessageReader database)
    {
        _database = database;
        _conversations = database.GetConversations();
    }

    public SearchDatabaseKind Kind => SearchDatabaseKind.AndroidMobileQQ;

    public SearchPage SearchDiscovery(
        string query,
        ConversationSearchFilter filter,
        SearchCursor? cursor)
    {
        if (filter.GroupOrPeerId is null && _database.HasMessageSearchIndex)
            return SearchAllIndexedMessages(query, cursor);

        return SearchPage(
            query,
            GetFilteredConversations(filter),
            cursor);
    }

    public SearchPage SearchConversation(
        string query,
        AvaGroupMessageSearchGroup conversation,
        SearchCursor? cursor)
    {
        var selected = _conversations
            .Where(item => string.Equals(item.TableName, conversation.AndroidMobileQQTableName, System.StringComparison.Ordinal))
            .ToArray();
        return SearchPage(query, selected, cursor);
    }

    private SearchPage SearchPage(
        string query,
        IReadOnlyList<AndroidMobileQQConversation> conversations,
        SearchCursor? cursor)
    {
        var results = new List<AvaGroupMessageSearchResult>(SearchPageSize);
        var conversationIndex = cursor?.ConversationIndex ?? 0;
        var messageCursor = cursor?.AndroidMobileQQCursor;
        while (results.Count < SearchPageSize && conversationIndex < conversations.Count)
        {
            var conversation = conversations[conversationIndex];
            var page = _database.SearchMessages(
                conversation.TableName,
                query,
                SearchPageSize - results.Count,
                messageCursor);
            results.AddRange(page.Messages.Select(message => CreateResult(message, conversation)));
            if (page.HasMore)
            {
                return new SearchPage(
                    results,
                    true,
                    new SearchCursor(AndroidMobileQQCursor: page.NextCursor, ConversationIndex: conversationIndex));
            }

            conversationIndex++;
            messageCursor = null;
        }

        var hasMore = conversationIndex < conversations.Count;
        return new SearchPage(
            results,
            hasMore,
            hasMore ? new SearchCursor(ConversationIndex: conversationIndex) : null);
    }

    private SearchPage SearchAllIndexedMessages(string query, SearchCursor? cursor)
    {
        var page = _database.SearchAllMessages(
            query,
            SearchPageSize,
            cursor?.AndroidMobileQQCursor);
        var conversationByTableName = _conversations
            .GroupBy(static conversation => conversation.TableName, System.StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), System.StringComparer.Ordinal);
        var results = page.Messages
            .Where(message => conversationByTableName.ContainsKey(message.TableName))
            .Select(message => CreateResult(message, conversationByTableName[message.TableName]))
            .ToList();

        return new SearchPage(
            results,
            page.HasMore,
            page.HasMore ? new SearchCursor(AndroidMobileQQCursor: page.NextCursor) : null);
    }

    private IReadOnlyList<AndroidMobileQQConversation> GetFilteredConversations(ConversationSearchFilter filter)
    {
        if (filter.GroupOrPeerId is not { } filterId)
            return _conversations;

        var filterText = filterId.ToString(CultureInfo.InvariantCulture);
        return _conversations
            .Where(conversation => string.Equals(conversation.PeerUin, filterText, System.StringComparison.Ordinal))
            .ToArray();
    }

    private static AvaGroupMessageSearchResult CreateResult(
        AndroidMobileQQMessageRecord message,
        AndroidMobileQQConversation conversation)
    {
        var conversationType = conversation.ConversationType == AndroidMobileQQConversationType.Group
            ? AvaConversationType.AndroidMobileQQGroup
            : AvaConversationType.AndroidMobileQQPrivate;
        var peerUin = TryParseUin(conversation.PeerUin);
        return new AvaGroupMessageSearchResult
        {
            ConversationType = conversationType,
            MessageId = message.RowId,
            MessageSeq = message.MessageTime,
            MessageTime = MessageConversationTime.ClampUnixTime(message.MessageTime),
            GroupId = conversationType == AvaConversationType.AndroidMobileQQGroup ? peerUin : 0,
            PeerUin = conversationType == AvaConversationType.AndroidMobileQQPrivate ? peerUin : 0,
            AndroidMobileQQTableName = conversation.TableName,
            AndroidMobileQQPeerUin = conversation.PeerUin,
            GroupName = conversation.DisplayName,
            SenderId = TryParseUin(message.SenderUin),
            SenderName = message.SenderName,
            SenderUid = message.SenderUin,
            PreviewText = SearchResultPreviewText.Normalize(message.PreviewText),
        };
    }

    private static uint TryParseUin(string? value)
    {
        return uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var uin)
            ? uin
            : 0;
    }
}
