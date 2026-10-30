using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class MessageSenderCache
{
    internal const int MaxMessageSenderInfoEntries = 8192;
    internal const int MaxSenderNameEntries = 8192;

    private static readonly IReadOnlyDictionary<uint, string> EmptySenderNames =
        new ReadOnlyDictionary<uint, string>(new Dictionary<uint, string>());
    private static readonly IReadOnlyDictionary<long, MessageSenderInfo> EmptyMessageSenderInfos =
        new ReadOnlyDictionary<long, MessageSenderInfo>(new Dictionary<long, MessageSenderInfo>());

    private readonly Dictionary<string, Dictionary<uint, string>> _senderNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<long, MessageSenderInfo>> _messageSenderInfos = new(StringComparer.Ordinal);
    private readonly Queue<MessageSenderCacheKey> _messageSenderInfoOrder = new();
    private readonly Queue<MessageSenderNameCacheKey> _senderNameOrder = new();
    private readonly Func<MessageRecord, AvaQQGroup, IDictionary<uint, string>, MessageSenderInfo> _createSenderInfo;
    private int _messageSenderInfoCount;
    private int _senderNameCount;

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
        var conversationKey = conversation.ConversationKey;
        var senderNames = GetOrCreateSenderNameCache(conversationKey);
        foreach (var message in messageList)
        {
            CacheSenderName(
                conversationKey,
                senderNames,
                message.SenderId,
                message.SendMemberName,
                message.SendNickName);
        }

        var messageSenderInfos = GetOrCreateMessageSenderInfoCache(conversationKey);
        foreach (var message in messageList)
        {
            CacheMessageSenderInfo(
                conversationKey,
                messageSenderInfos,
                message,
                conversation,
                senderNames);
        }

        foreach (var message in referencedMessages ?? [])
        {
            CacheSenderName(
                conversationKey,
                senderNames,
                message.SenderId,
                message.SendMemberName,
                message.SendNickName);
            CacheMessageSenderInfo(
                conversationKey,
                messageSenderInfos,
                message,
                conversation,
                senderNames);
        }

        TrimMessageSenderInfos();
        TrimSenderNames();
        RemoveEmptyConversationCaches(conversationKey);
    }

    public IReadOnlyDictionary<uint, string> GetSenderNameCache(string conversationKey)
    {
        return _senderNames.GetValueOrDefault(conversationKey) ?? EmptySenderNames;
    }

    public IReadOnlyDictionary<long, MessageSenderInfo> GetMessageSenderInfoCache(string conversationKey)
    {
        return _messageSenderInfos.GetValueOrDefault(conversationKey) ?? EmptyMessageSenderInfos;
    }

    public void SetSenderName(string conversationKey, uint senderId, string? name)
    {
        var senderNames = GetOrCreateSenderNameCache(conversationKey);
        CacheSenderName(conversationKey, senderNames, senderId, name, null);
        TrimSenderNames();
        RemoveEmptyConversationCaches(conversationKey);
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
        _messageSenderInfoOrder.Clear();
        _senderNameOrder.Clear();
        _messageSenderInfoCount = 0;
        _senderNameCount = 0;
    }

    private void CacheMessageSenderInfo(
        string conversationKey,
        IDictionary<long, MessageSenderInfo> senderInfos,
        MessageRecord message,
        AvaQQGroup conversation,
        IDictionary<uint, string> senderNames)
    {
        if (message.MessageSeq <= 0)
            return;

        if (!senderInfos.ContainsKey(message.MessageSeq))
        {
            _messageSenderInfoOrder.Enqueue(new MessageSenderCacheKey(conversationKey, message.MessageSeq));
            _messageSenderInfoCount++;
        }

        senderInfos[message.MessageSeq] = _createSenderInfo(message, conversation, senderNames);
    }

    private void TrimMessageSenderInfos()
    {
        while (_messageSenderInfoCount > MaxMessageSenderInfoEntries &&
               _messageSenderInfoOrder.TryDequeue(out var oldest))
        {
            if (!_messageSenderInfos.TryGetValue(oldest.ConversationKey, out var senderInfos) ||
                !senderInfos.Remove(oldest.MessageSeq))
            {
                continue;
            }

            _messageSenderInfoCount--;
            if (senderInfos.Count == 0)
                _messageSenderInfos.Remove(oldest.ConversationKey);
        }
    }

    private void CacheSenderName(
        string conversationKey,
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
            if (!senderNames.ContainsKey(senderId))
            {
                _senderNameOrder.Enqueue(new MessageSenderNameCacheKey(conversationKey, senderId));
                _senderNameCount++;
            }
            senderNames[senderId] = name;
        }
    }

    private void TrimSenderNames()
    {
        while (_senderNameCount > MaxSenderNameEntries &&
               _senderNameOrder.TryDequeue(out var oldest))
        {
            if (!_senderNames.TryGetValue(oldest.ConversationKey, out var senderNames) ||
                !senderNames.Remove(oldest.SenderId))
            {
                continue;
            }

            _senderNameCount--;
            if (senderNames.Count == 0)
                _senderNames.Remove(oldest.ConversationKey);
        }
    }

    private Dictionary<uint, string> GetOrCreateSenderNameCache(string conversationKey)
    {
        if (!_senderNames.TryGetValue(conversationKey, out var senderNames))
        {
            senderNames = new Dictionary<uint, string>();
            _senderNames[conversationKey] = senderNames;
        }

        return senderNames;
    }

    private Dictionary<long, MessageSenderInfo> GetOrCreateMessageSenderInfoCache(string conversationKey)
    {
        if (!_messageSenderInfos.TryGetValue(conversationKey, out var senderInfos))
        {
            senderInfos = new Dictionary<long, MessageSenderInfo>();
            _messageSenderInfos[conversationKey] = senderInfos;
        }

        return senderInfos;
    }

    private void RemoveEmptyConversationCaches(string conversationKey)
    {
        if (_senderNames.TryGetValue(conversationKey, out var senderNames) && senderNames.Count == 0)
            _senderNames.Remove(conversationKey);
        if (_messageSenderInfos.TryGetValue(conversationKey, out var senderInfos) && senderInfos.Count == 0)
            _messageSenderInfos.Remove(conversationKey);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private readonly record struct MessageSenderCacheKey(string ConversationKey, long MessageSeq);
    private readonly record struct MessageSenderNameCacheKey(string ConversationKey, uint SenderId);
}
