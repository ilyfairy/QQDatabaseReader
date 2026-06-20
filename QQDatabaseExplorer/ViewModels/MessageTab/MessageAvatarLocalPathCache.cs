using System;
using System.Collections.Generic;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class MessageAvatarLocalPathCache(Func<string?> getNtDataPath)
{
    private readonly Dictionary<uint, string> _localPathsByUin = new();
    private readonly Dictionary<string, string> _localPathsByUid = new(StringComparer.Ordinal);

    public void Clear()
    {
        _localPathsByUin.Clear();
        _localPathsByUid.Clear();
    }

    public string? ResolveMessageAvatarLocalPath(MessageRecord item, AvaQQGroup conversation)
    {
        if (conversation.ConversationType == AvaConversationType.Private &&
            PrivateMessageParticipantMatcher.IsPrivatePeerMessage(item, conversation) &&
            !string.IsNullOrWhiteSpace(conversation.AvatarLocalPath))
        {
            return conversation.AvatarLocalPath;
        }

        if (item.SenderId != 0 &&
            _localPathsByUin.TryGetValue(item.SenderId, out var uinAvatarPath))
        {
            return uinAvatarPath;
        }

        if (!string.IsNullOrWhiteSpace(item.SenderUid) &&
            _localPathsByUid.TryGetValue(item.SenderUid, out var uidAvatarPath))
        {
            return uidAvatarPath;
        }

        return null;
    }

    public string? ResolveRecentAvatarLocalPath(string? avatarPath)
    {
        return AvatarCacheResolver.ResolveStoredAvatarPath(avatarPath, getNtDataPath());
    }

    public void CacheConversationAvatarLocalPath(AvaQQGroup conversation, ConversationAvatarCacheItem item)
    {
        if (string.IsNullOrWhiteSpace(item.AvatarLocalPath))
            return;

        conversation.AvatarLocalPath = item.AvatarLocalPath;

        if (conversation.ConversationType != AvaConversationType.Private)
            return;

        if (item.PrivateUin != 0)
            _localPathsByUin[item.PrivateUin] = item.AvatarLocalPath;

        if (!string.IsNullOrWhiteSpace(item.PrivateUid))
            _localPathsByUid[item.PrivateUid] = item.AvatarLocalPath;
    }
}
