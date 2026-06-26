using System;

namespace QQDatabaseExplorer.Models;

public sealed record AvaRawMessageData(
    string Source,
    int MessageType,
    int SubMessageType,
    int SendType,
    string SenderUid,
    string PeerUid,
    uint PeerUin,
    uint GroupId,
    long PrivateConversationId,
    long ReplyToMessageSeq,
    string? ContentBase64,
    string? SubContentBase64,
    string? MessageReactionsBase64)
{
    public static AvaRawMessageData Create(
        string source,
        int messageType,
        int subMessageType,
        int sendType,
        string senderUid,
        string peerUid,
        uint peerUin,
        uint groupId,
        long privateConversationId,
        long replyToMessageSeq,
        byte[]? content,
        byte[]? subContent,
        byte[]? messageReactions)
    {
        return new AvaRawMessageData(
            source,
            messageType,
            subMessageType,
            sendType,
            senderUid,
            peerUid,
            peerUin,
            groupId,
            privateConversationId,
            replyToMessageSeq,
            ToBase64(content),
            ToBase64(subContent),
            ToBase64(messageReactions));
    }

    private static string? ToBase64(byte[]? bytes)
    {
        return bytes is { Length: > 0 } ? Convert.ToBase64String(bytes) : null;
    }
}
