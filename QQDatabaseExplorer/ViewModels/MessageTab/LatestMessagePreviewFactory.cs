using System;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class LatestMessagePreviewFactory
{
    private readonly QqNtSystemHintDisplayFactory _systemHintFactory;
    private readonly Func<uint, string?, string, string> _resolveProfileDisplayName;

    public LatestMessagePreviewFactory(
        QqNtSystemHintDisplayFactory systemHintFactory,
        Func<uint, string?, string, string> resolveProfileDisplayName)
    {
        _systemHintFactory = systemHintFactory;
        _resolveProfileDisplayName = resolveProfileDisplayName;
    }

    public string Create(MessageConversationCatalogItem contact)
    {
        if (contact.LatestMessage is { } latestMessage)
        {
            return Create(contact.ConversationType, latestMessage);
        }

        var messageText = RecentMessagePreviewParser.Parse(
            contact.LastMessage,
            contact.LatestMessage?.MessageType ?? contact.LastMessageType,
            contact.LatestMessage?.SubMessageType);
        if (string.IsNullOrWhiteSpace(messageText))
            return string.Empty;

        if (contact.ConversationType == AvaConversationType.Private)
            return messageText;

        var senderName = FirstNonEmpty(contact.SendMemberName, contact.SendNickName, contact.SendremarkName);
        if (string.IsNullOrWhiteSpace(senderName))
        {
            senderName = _resolveProfileDisplayName(contact.SenderUin, contact.SenderUid, string.Empty);
        }

        return AddSenderName(messageText, senderName);
    }

    public string Create(AvaQQGroup conversation, MessageRecord message)
    {
        return Create(conversation.ConversationType, message);
    }

    public string Create(AvaConversationType conversationType, MessageRecord message)
    {
        var senderName = FirstNonEmpty(message.SendMemberName, message.SendNickName);
        if (string.IsNullOrWhiteSpace(senderName) && message.SenderId != 0)
        {
            senderName = _resolveProfileDisplayName(message.SenderId, message.SenderUid, message.SenderId.ToString());
        }

        var messageText = CreatePreviewText(message, senderName);
        if (string.IsNullOrWhiteSpace(messageText))
            return string.Empty;

        if (conversationType is AvaConversationType.Private or AvaConversationType.PCQQPrivate)
            return messageText;

        return AddSenderName(messageText, senderName);
    }

    public static string CreatePCQQ(
        PCQQConversationType conversationType,
        string messageText,
        uint senderUin,
        string? senderNickname)
    {
        if (string.IsNullOrWhiteSpace(messageText) ||
            conversationType == PCQQConversationType.Private)
        {
            return messageText;
        }

        var senderName = FirstNonEmpty(senderNickname, senderUin == 0 ? null : senderUin.ToString());
        return AddSenderName(messageText, senderName);
    }

    public static string CreateAndroidMobileQQ(
        AndroidMobileQQConversationType conversationType,
        string messageText,
        string senderUin,
        string? senderName)
    {
        if (string.IsNullOrWhiteSpace(messageText) ||
            conversationType == AndroidMobileQQConversationType.Private)
        {
            return messageText;
        }

        return AddSenderName(messageText, FirstNonEmpty(senderName, senderUin));
    }

    public string CreatePreviewText(MessageRecord message)
    {
        return CreateRawPreviewText(message);
    }

    private string CreatePreviewText(MessageRecord message, string? senderName)
    {
        return CreateSystemHintText(message, senderName) ?? CreateRawPreviewText(message);
    }

    private string? CreateSystemHintText(MessageRecord message, string? senderName)
    {
        if (QqNtMessageContentParser.TryParse(message.Content) is { } content &&
            _systemHintFactory.Create(
                content,
                message.GroupId,
                message.SenderId,
                message.SenderUid,
                senderName) is { } systemHint)
        {
            return systemHint.DisplayText;
        }

        return null;
    }

    private static string CreateRawPreviewText(MessageRecord message)
    {
        if (IcalinguaMessagePayload.FromContent(message.Content) is { } icalinguaPayload &&
            !string.IsNullOrWhiteSpace(icalinguaPayload.PreviewText))
        {
            return icalinguaPayload.PreviewText;
        }

        if (message.MessageType == MessageType.Text &&
            message.SubMessageType == SubMessageType.Text &&
            message.Content is { Length: > 0 } pcqqContent &&
            pcqqContent.AsSpan().StartsWith("MSG"u8))
        {
            return PCQQMessageContentParser.GetDisplayText(pcqqContent);
        }

        if (message.MessageType != MessageType.Voice &&
            QQMessageDisplayText.TryGetPriorityText(message.MessageType, message.SubMessageType, out var priorityText))
        {
            return priorityText;
        }

        if (message.Content is { Length: > 0 })
        {
            var messageText = RecentMessagePreviewParser.Parse(
                message.Content,
                message.MessageType,
                message.SubMessageType);
            if (!string.IsNullOrWhiteSpace(messageText))
                return messageText;
        }

        return QqNtMessageFallbackTextFactory.CreateMissingMessageText(message);
    }

    private static string AddSenderName(string messageText, string? senderName)
    {
        return string.IsNullOrWhiteSpace(senderName)
            ? messageText
            : $"{senderName}: {messageText}";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}
