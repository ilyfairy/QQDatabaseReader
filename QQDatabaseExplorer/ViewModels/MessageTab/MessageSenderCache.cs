using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class MessageSenderCache
{
    private readonly Dictionary<string, Dictionary<uint, string>> _senderNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<long, MessageSenderInfo>> _messageSenderInfos = new(StringComparer.Ordinal);
    private readonly Func<MessageRecord, AvaQQGroup, IDictionary<uint, string>, MessageSenderInfo> _createSenderInfo;

    public MessageSenderCache(Func<MessageRecord, AvaQQGroup, IDictionary<uint, string>, MessageSenderInfo> createSenderInfo)
    {
        _createSenderInfo = createSenderInfo;
    }

    public void Cache(
        AvaQQGroup conversation,
        IEnumerable<MessageRecord> messages,
        IReadOnlyList<MessageRecord>? referencedMessages = null)
    {
        var messageList = messages as IReadOnlyList<MessageRecord> ?? messages.ToArray();
        var senderNames = GetSenderNameCache(conversation.ConversationKey);
        foreach (var message in messageList)
        {
            CacheSenderName(senderNames, message.SenderId, message.SendMemberName, message.SendNickName);
        }

        var messageSenderInfos = GetMessageSenderInfoCache(conversation.ConversationKey);
        foreach (var message in messageList)
        {
            CacheMessageSenderInfo(messageSenderInfos, message, conversation, senderNames);
        }

        foreach (var message in referencedMessages ?? [])
        {
            CacheSenderName(senderNames, message.SenderId, message.SendMemberName, message.SendNickName);
            CacheMessageSenderInfo(messageSenderInfos, message, conversation, senderNames);
        }
    }

    public Dictionary<uint, string> GetSenderNameCache(string conversationKey)
    {
        if (!_senderNames.TryGetValue(conversationKey, out var senderNames))
        {
            senderNames = new Dictionary<uint, string>();
            _senderNames[conversationKey] = senderNames;
        }

        return senderNames;
    }

    public Dictionary<long, MessageSenderInfo> GetMessageSenderInfoCache(string conversationKey)
    {
        if (!_messageSenderInfos.TryGetValue(conversationKey, out var senderInfos))
        {
            senderInfos = new Dictionary<long, MessageSenderInfo>();
            _messageSenderInfos[conversationKey] = senderInfos;
        }

        return senderInfos;
    }

    public IReadOnlyList<MessageSenderFilterOption> GetSenderFilterCandidates(AvaQQGroup? conversation)
    {
        if (conversation is null)
            return [];

        var cachedNames = _senderNames.GetValueOrDefault(conversation.ConversationKey);
        if (cachedNames is null || cachedNames.Count == 0)
            return [];

        return cachedNames
            .Where(static item => item.Key != 0)
            .Select(static item => new MessageSenderFilterOption(item.Key, FirstNonEmpty(item.Value, item.Key.ToString())))
            .OrderBy(static item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static item => item.SenderId)
            .ToArray();
    }

    public void Clear()
    {
        _senderNames.Clear();
        _messageSenderInfos.Clear();
    }

    private void CacheMessageSenderInfo(
        IDictionary<long, MessageSenderInfo> senderInfos,
        MessageRecord message,
        AvaQQGroup conversation,
        IDictionary<uint, string> senderNames)
    {
        if (message.MessageSeq <= 0)
            return;

        senderInfos[message.MessageSeq] = _createSenderInfo(message, conversation, senderNames);
    }

    private static void CacheSenderName(
        IDictionary<uint, string> senderNames,
        uint senderId,
        string? sendMemberName,
        string? sendNickName)
    {
        if (senderId == 0)
            return;

        var name = FirstNonEmpty(sendMemberName, sendNickName);
        if (!string.IsNullOrWhiteSpace(name))
        {
            senderNames[senderId] = name;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}
