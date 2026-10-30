using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class MessageParticipantResolver
{
    private readonly Func<QQProfileInfoReader?> _getProfileInfoDatabase;
    private readonly QQNtDatabaseQueryGate _queryGate;
    private readonly MessageAvatarLocalPathCache _avatarLocalPathCache;
    private readonly SystemHintParticipantResolver _systemHintParticipantResolver;
    private readonly object _profileInfoCacheGate = new();
    private ProfileInfoNameCache? _profileInfoNames;

    public MessageParticipantResolver(
        Func<QQProfileInfoReader?> getProfileInfoDatabase,
        Func<QQGroupInfoReader?> getGroupInfoDatabase,
        Func<string?> getNtDataPath,
        QQNtDatabaseQueryGate queryGate)
    {
        _getProfileInfoDatabase = getProfileInfoDatabase;
        _queryGate = queryGate;
        _avatarLocalPathCache = new MessageAvatarLocalPathCache(getNtDataPath);
        _systemHintParticipantResolver = new SystemHintParticipantResolver(
            getGroupInfoDatabase,
            GetProfileInfoNameCache,
            queryGate);
    }

    public void InvalidateProfileInfoCache()
    {
        lock (_profileInfoCacheGate)
        {
            _profileInfoNames = null;
        }
    }

    public IReadOnlyDictionary<ProfileInfoLookupRequest, string> PreloadProfileInfoCache(
        QQProfileInfoReader database,
        IReadOnlyCollection<ProfileInfoLookupRequest> requests)
    {
        ProfileInfoNameCache cache;
        lock (_profileInfoCacheGate)
        {
            cache = ProfileInfoNameCache.Create(database, _queryGate);
            _profileInfoNames = cache;
        }

        var resolvedNames = new Dictionary<ProfileInfoLookupRequest, string>();
        foreach (var request in requests)
        {
            resolvedNames[request] = cache.TryGetName(request.Uin, request.NtUid, out var name)
                ? name
                : string.Empty;
        }

        return resolvedNames;
    }

    public void ClearAvatarPathCaches()
    {
        _avatarLocalPathCache.Clear();
    }

    public string ResolveProfileDisplayName(uint uin, string? ntUid, string fallback)
    {
        return GetProfileInfoNameCache().TryGetName(uin, ntUid, out var name)
            ? name
            : fallback;
    }

    public string ResolveReplyTargetSenderName(
        MessageRecord message,
        AvaQQGroup conversation,
        IDictionary<uint, string> senderNames)
    {
        var name = FirstNonEmpty(message.SendMemberName, message.SendNickName);
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        if (message.SenderId != 0 &&
            senderNames.TryGetValue(message.SenderId, out var cachedName) &&
            !string.IsNullOrWhiteSpace(cachedName))
        {
            return cachedName;
        }

        if (conversation.ConversationType == AvaConversationType.Private &&
            PrivateMessageParticipantMatcher.IsPrivatePeerMessage(message, conversation))
        {
            if (!string.IsNullOrWhiteSpace(conversation.GroupName))
                return conversation.GroupName;

            if (GetProfileInfoNameCache().TryGetName(message.SenderId, message.SenderUid, out var peerName))
                return peerName;

            return GetProfileInfoNameCache().TryGetName(conversation.PrivateUin, conversation.PrivateUid, out peerName)
                ? peerName
                : string.Empty;
        }

        return GetProfileInfoNameCache().TryGetName(message.SenderId, message.SenderUid, out var profileName)
            ? profileName
            : string.Empty;
    }

    public string ResolveMessageSenderName(
        MessageRecord item,
        AvaQQGroup conversation,
        IReadOnlyDictionary<uint, string> senderNames)
    {
        var name = FirstNonEmpty(item.SendMemberName, item.SendNickName);
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        if (item.SenderId != 0 &&
            senderNames.TryGetValue(item.SenderId, out var cachedName) &&
            !string.IsNullOrWhiteSpace(cachedName))
        {
            return cachedName;
        }

        if (conversation.ConversationType == AvaConversationType.Private)
        {
            if (PrivateMessageParticipantMatcher.IsPrivatePeerMessage(item, conversation))
                return conversation.DisplayName;

            return ResolveProfileDisplayName(item.SenderId, item.SenderUid, fallback: "我");
        }

        if (item.SenderId != 0)
            return item.SenderId.ToString();

        return !string.IsNullOrWhiteSpace(item.SenderUid)
            ? item.SenderUid
            : conversation.ConversationType == AvaConversationType.Private
                ? conversation.DisplayName
                : string.Empty;
    }

    public string ResolveSystemHintParticipantName(
        uint groupId,
        string? ntUid,
        string? uin,
        params string?[] fallbacks)
    {
        return _systemHintParticipantResolver.ResolveParticipantName(groupId, ntUid, uin, fallbacks);
    }

    public string ResolveSystemHintSourceUin(uint groupId, string? ntUid)
    {
        return _systemHintParticipantResolver.ResolveSourceUin(groupId, ntUid);
    }

    public string? ResolveMessageAvatarLocalPath(MessageRecord item, AvaQQGroup conversation)
    {
        return _avatarLocalPathCache.ResolveMessageAvatarLocalPath(item, conversation);
    }

    public string? ResolveRecentAvatarLocalPath(string? avatarPath)
    {
        return _avatarLocalPathCache.ResolveRecentAvatarLocalPath(avatarPath);
    }

    public void CacheConversationAvatarLocalPath(AvaQQGroup conversation, ConversationAvatarCacheItem item)
    {
        _avatarLocalPathCache.CacheConversationAvatarLocalPath(conversation, item);
    }

    public static bool IsPrivatePeerMessage(MessageRecord item, AvaQQGroup conversation)
    {
        return PrivateMessageParticipantMatcher.IsPrivatePeerMessage(item, conversation);
    }

    public static bool IsPrivatePeerMessage(AvaQQMessage item, AvaQQGroup conversation)
    {
        return PrivateMessageParticipantMatcher.IsPrivatePeerMessage(item, conversation);
    }

    private ProfileInfoNameCache GetProfileInfoNameCache()
    {
        var database = _getProfileInfoDatabase();
        lock (_profileInfoCacheGate)
        {
            if (_profileInfoNames is { } cache &&
                ReferenceEquals(cache.Database, database))
            {
                return cache;
            }

            _profileInfoNames = ProfileInfoNameCache.Create(database, _queryGate);
            return _profileInfoNames;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}

internal readonly record struct ConversationAvatarCacheItem(
    uint PrivateUin,
    string? PrivateUid,
    string? AvatarLocalPath);

internal readonly record struct ProfileInfoLookupRequest(uint Uin, string? NtUid);
