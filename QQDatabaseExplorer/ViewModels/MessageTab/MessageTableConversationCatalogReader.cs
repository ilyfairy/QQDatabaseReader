using System.Collections.Generic;
using System.Linq;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal static class MessageTableConversationCatalogReader
{
    public static IReadOnlyList<MessageConversationCatalogItem> ReadQQNtFallbackConversations(
        QQMessageReader messageDatabase)
    {
        return ReadFallbackConversations(
            messageDatabase.DbContext.GroupMessages
                .Select(message => message.GroupId)
                .Where(groupId => groupId != 0)
                .Distinct()
                .ToList(),
            messageDatabase.DbContext.PrivateMessages
                .Where(message => message.ConversationId != 0)
                .Where(message => message.MessageType != MessageType.Empty)
                .GroupBy(message => message.ConversationId)
                .Select(group => new PrivateConversationFallbackInfo(
                    group.Key,
                    group.Max(message => message.PeerUin),
                    group.Max(message => message.PeerUid),
                    group.Max(message => message.MessageTime)))
                .ToList());
    }

    public static IReadOnlyList<MessageConversationCatalogItem> ReadAndroidQQNtFallbackConversations(
        QQAndroidMessageReader messageDatabase)
    {
        return ReadFallbackConversations(
            messageDatabase.DbContext.GroupMessages
                .Select(message => message.GroupId)
                .Where(groupId => groupId != 0)
                .Distinct()
                .ToList(),
            messageDatabase.DbContext.PrivateMessages
                .Where(message => message.ConversationId != 0)
                .Where(message => message.MessageType != MessageType.Empty)
                .GroupBy(message => message.ConversationId)
                .Select(group => new PrivateConversationFallbackInfo(
                    group.Key,
                    group.Max(message => message.PeerUin),
                    group.Max(message => message.PeerUid),
                    group.Max(message => message.MessageTime)))
                .ToList());
    }

    private static IReadOnlyList<MessageConversationCatalogItem> ReadFallbackConversations(
        IEnumerable<uint> groupIds,
        IEnumerable<PrivateConversationFallbackInfo> privateConversations)
    {
        var items = groupIds
            .Select(MessageConversationCatalogItem.CreateGroup)
            .ToList();

        items.AddRange(privateConversations
            .Select(conversationInfo => MessageConversationCatalogItem.CreatePrivate(
                conversationInfo.ConversationId,
                conversationInfo.PrivateUin,
                conversationInfo.PrivateUid,
                conversationInfo.LastTime)));

        return items;
    }

    private sealed record PrivateConversationFallbackInfo(
        long ConversationId,
        uint PrivateUin,
        string? PrivateUid,
        int LastTime);
}
