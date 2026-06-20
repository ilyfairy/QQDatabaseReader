using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.ViewModels;

internal interface IConversationMessageSearchProvider
{
    SearchDatabaseKind Kind { get; }

    SearchPage SearchDiscovery(
        string query,
        ConversationSearchFilter filter,
        SearchCursor? cursor);

    SearchPage SearchConversation(
        string query,
        AvaGroupMessageSearchGroup conversation,
        SearchCursor? cursor);
}
