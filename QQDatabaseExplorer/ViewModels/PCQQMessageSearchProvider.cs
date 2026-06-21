using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.ViewModels.MessageTab;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels;

internal sealed class PCQQMessageSearchProvider : IConversationMessageSearchProvider
{
    private const int SearchPageSize = 100;
    private readonly PCQQMessageReader _database;
    private readonly IReadOnlyList<PCQQConversation> _conversations;

    public PCQQMessageSearchProvider(PCQQMessageReader database)
    {
        _database = database;
        _conversations = database.GetConversations();
    }

    public SearchDatabaseKind Kind => SearchDatabaseKind.PCQQ;

    public SearchPage SearchDiscovery(
        string query,
        ConversationSearchFilter filter,
        SearchCursor? cursor)
    {
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
            .Where(item => string.Equals(item.TableName, conversation.PCQQTableName, System.StringComparison.Ordinal))
            .ToArray();
        return SearchPage(query, selected, cursor);
    }

    private SearchPage SearchPage(
        string query,
        IReadOnlyList<PCQQConversation> conversations,
        SearchCursor? cursor)
    {
        var results = new List<AvaGroupMessageSearchResult>(SearchPageSize);
        var conversationIndex = cursor?.ConversationIndex ?? 0;
        var messageCursor = cursor?.PCQQCursor;
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
                    new SearchCursor(PCQQCursor: page.NextCursor, ConversationIndex: conversationIndex));
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

    private IReadOnlyList<PCQQConversation> GetFilteredConversations(ConversationSearchFilter filter)
    {
        if (filter.GroupOrPeerId is not { } filterId)
            return _conversations;

        return _conversations
            .Where(conversation => conversation.PeerId == filterId || conversation.RawPeerId == filterId)
            .ToArray();
    }

    private static AvaGroupMessageSearchResult CreateResult(
        PCQQMessageRecord message,
        PCQQConversation conversation)
    {
        var conversationType = conversation.ConversationType == PCQQConversationType.Group
            ? AvaConversationType.PCQQGroup
            : AvaConversationType.PCQQPrivate;
        return new AvaGroupMessageSearchResult
        {
            ConversationType = conversationType,
            MessageId = message.MessageRandom,
            MessageSeq = message.MessageTime,
            MessageTime = MessageConversationTime.ClampUnixTime(message.MessageTime),
            GroupId = conversationType == AvaConversationType.PCQQGroup ? conversation.PeerId : 0,
            PeerUin = conversationType == AvaConversationType.PCQQPrivate ? conversation.PeerId : 0,
            PCQQTableName = conversation.TableName,
            GroupName = conversation.DisplayName,
            SenderId = message.SenderUin,
            SenderName = message.SenderNickname ?? (message.SenderUin == 0 ? null : message.SenderUin.ToString(CultureInfo.InvariantCulture)),
            PreviewText = SearchResultPreviewText.Normalize(message.PreviewText),
        };
    }
}
