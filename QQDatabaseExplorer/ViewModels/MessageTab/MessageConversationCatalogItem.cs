using QQDatabaseExplorer.Models;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed record MessageConversationCatalogItem(
    AvaConversationType ConversationType,
    uint GroupId,
    long PrivateConversationId,
    uint PrivateUin,
    string? PrivateUid,
    string? DisplayName,
    byte[]? LastMessage,
    MessageType LastMessageType,
    int LastTime,
    uint SenderUin,
    string? SenderUid,
    string? SendremarkName,
    string? SendMemberName,
    string? SendNickName,
    string? AvatarPath,
    MessageRecord? LatestMessage)
{
    public string ConversationKey => ConversationType switch
    {
        AvaConversationType.Group => $"group:{GroupId}",
        AvaConversationType.Private => $"private:{PrivateConversationId}",
        _ => $"{ConversationType}:{GroupId}:{PrivateConversationId}",
    };

    public static MessageConversationCatalogItem CreateGroup(uint groupId)
    {
        return new MessageConversationCatalogItem(
            AvaConversationType.Group,
            groupId,
            0,
            0,
            null,
            null,
            null,
            MessageType.Empty,
            0,
            0,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    public static MessageConversationCatalogItem CreatePrivate(
        long conversationId,
        uint privateUin,
        string? privateUid,
        int lastTime)
    {
        return new MessageConversationCatalogItem(
            AvaConversationType.Private,
            0,
            conversationId,
            privateUin,
            privateUid,
            null,
            null,
            MessageType.Empty,
            lastTime,
            0,
            null,
            null,
            null,
            null,
            null,
            null);
    }
}
