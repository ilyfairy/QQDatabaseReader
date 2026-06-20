using System;
using System.Collections.Generic;
using System.Linq;
using ObservableCollections;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class MessageConversationListApplier
{
    private readonly ObservableList<AvaQQGroup> _groups;
    private readonly ConversationListState _conversationList;
    private readonly MessageParticipantResolver _participantResolver;

    public MessageConversationListApplier(
        ObservableList<AvaQQGroup> groups,
        ConversationListState conversationList,
        MessageParticipantResolver participantResolver)
    {
        _groups = groups;
        _conversationList = conversationList;
        _participantResolver = participantResolver;
    }

    public AvaQQGroup GetOrCreateGroup(uint groupId)
    {
        var group = _groups.FirstOrDefault(v =>
            v.ConversationType == AvaConversationType.Group &&
            v.GroupId == groupId);
        if (group is not null)
            return group;

        group = new AvaQQGroup
        {
            ConversationType = AvaConversationType.Group,
            GroupId = groupId,
        };
        _groups.Add(group);
        return group;
    }

    public AvaQQGroup GetOrCreatePrivateConversation(long conversationId)
    {
        var conversation = _groups.FirstOrDefault(v =>
            v.ConversationType == AvaConversationType.Private &&
            v.PrivateConversationId == conversationId);
        if (conversation is not null)
            return conversation;

        conversation = new AvaQQGroup
        {
            ConversationType = AvaConversationType.Private,
            PrivateConversationId = conversationId,
        };
        _groups.Add(conversation);
        return conversation;
    }

    public AvaQQGroup GetOrCreateIcalinguaConversation(long roomId)
    {
        var conversation = _groups.FirstOrDefault(v =>
            v.ConversationType == AvaConversationType.Icalingua &&
            v.IcalinguaRoomId == roomId);
        if (conversation is not null)
            return conversation;

        conversation = new AvaQQGroup
        {
            ConversationType = AvaConversationType.Icalingua,
            IcalinguaRoomId = roomId,
        };
        _groups.Add(conversation);
        return conversation;
    }

    public AvaQQGroup GetOrCreatePCQQGroup(uint groupId)
    {
        var group = _groups.FirstOrDefault(v =>
            v.ConversationType == AvaConversationType.PCQQGroup &&
            v.GroupId == groupId);
        if (group is not null)
            return group;

        group = new AvaQQGroup
        {
            ConversationType = AvaConversationType.PCQQGroup,
            GroupId = groupId,
        };
        _groups.Add(group);
        return group;
    }

    public AvaQQGroup GetOrCreatePCQQPrivateConversation(uint privateUin)
    {
        var conversation = _groups.FirstOrDefault(v =>
            v.ConversationType == AvaConversationType.PCQQPrivate &&
            v.PrivateUin == privateUin);
        if (conversation is not null)
            return conversation;

        conversation = new AvaQQGroup
        {
            ConversationType = AvaConversationType.PCQQPrivate,
            PrivateUin = privateUin,
        };
        _groups.Add(conversation);
        return conversation;
    }

    public void ApplyGroupInfoItems(IReadOnlyList<GroupInfoLoadItem> rawGroups)
    {
        foreach (var item in _groups
                     .Where(group => group.ConversationType == AvaConversationType.Group)
                     .Join(rawGroups,
                         v => v.GroupId,
                         v => v.GroupId,
                         (group, rawGroup) => (group, rawGroup)))
        {
            item.group.GroupName = item.rawGroup.GroupName;
        }

        var existingGroupIds = _groups
            .Where(group => group.ConversationType == AvaConversationType.Group)
            .Select(group => group.GroupId);
        var newGroups = rawGroups.ExceptBy(existingGroupIds, v => v.GroupId)
            .Select(v => new AvaQQGroup
            {
                ConversationType = AvaConversationType.Group,
                GroupId = v.GroupId,
                GroupName = v.GroupName,
                IsSelected = _conversationList.IsSelected($"group:{v.GroupId}"),
                IsActive = _conversationList.IsActive($"group:{v.GroupId}"),
            });
        _groups.AddRange(newGroups);
    }

    public void ApplyMessageConversations(IEnumerable<ConversationLoadItem> items)
    {
        foreach (var item in items)
        {
            var conversation = item.ConversationType switch
            {
                AvaConversationType.Group when item.GroupId != 0 => GetOrCreateGroup(item.GroupId),
                AvaConversationType.Private when item.PrivateConversationId != 0 => GetOrCreatePrivateConversation(item.PrivateConversationId),
                _ => null,
            };
            if (conversation is null)
                continue;

            ApplyQQNtConversation(conversation, item);
            ApplySelectionState(conversation);
        }
    }

    public void ApplyPCQQMessageConversations(IEnumerable<PCQQConversation> items)
    {
        foreach (var item in items)
        {
            var conversation = item.ConversationType switch
            {
                PCQQConversationType.Group when item.PeerId != 0 => GetOrCreatePCQQGroup(item.PeerId),
                PCQQConversationType.Private when item.PeerId != 0 => GetOrCreatePCQQPrivateConversation(item.PeerId),
                _ => null,
            };
            if (conversation is null)
                continue;

            ApplyPCQQConversation(conversation, item);
            ApplySelectionState(conversation);
        }
    }

    public void ApplyIcalinguaMessageConversations(IEnumerable<IcalinguaConversation> items)
    {
        foreach (var item in items)
        {
            if (item.RoomId == 0)
                continue;

            var conversation = GetOrCreateIcalinguaConversation(item.RoomId);
            ApplyIcalinguaConversation(conversation, item);
            ApplySelectionState(conversation);
        }
    }

    public void ClearConversations()
    {
        _groups.Clear();
    }

    public void RemoveConversations(Predicate<AvaQQGroup> predicate)
    {
        foreach (var conversation in _groups.Where(group => predicate(group)).ToArray())
        {
            _groups.Remove(conversation);
        }
    }

    public void ClearQQNtGroupNames()
    {
        foreach (var conversation in _groups.Where(static group => group.ConversationType == AvaConversationType.Group))
        {
            conversation.GroupName = null;
        }
    }

    private void ApplySelectionState(AvaQQGroup conversation)
    {
        conversation.IsSelected = _conversationList.IsSelected(conversation.ConversationKey);
        conversation.IsActive = _conversationList.IsActive(conversation.ConversationKey);
    }

    private void ApplyQQNtConversation(AvaQQGroup conversation, ConversationLoadItem item)
    {
        conversation.PrivateUin = item.PrivateUin;
        conversation.PrivateUid = item.PrivateUid;
        var profileName = item.ConversationType == AvaConversationType.Private
            ? _participantResolver.ResolveProfileDisplayName(item.PrivateUin, item.PrivateUid, fallback: string.Empty)
            : string.Empty;
        conversation.GroupName = FirstNonEmpty(profileName, item.DisplayName, conversation.GroupName);
        if (!string.IsNullOrWhiteSpace(item.LatestMessageText))
            conversation.LatestMessageText = item.LatestMessageText;
        _participantResolver.CacheConversationAvatarLocalPath(
            conversation,
            new ConversationAvatarCacheItem(item.PrivateUin, item.PrivateUid, item.AvatarLocalPath));
        if (item.LastTime != 0)
            conversation.LatestMessageTime = item.LastTime;
    }

    private static void ApplyPCQQConversation(AvaQQGroup conversation, PCQQConversation item)
    {
        conversation.PCQQTableName = item.TableName;
        if (item.ConversationType == PCQQConversationType.Group &&
            !string.IsNullOrWhiteSpace(item.DisplayNameOverride))
        {
            conversation.GroupName = item.DisplayNameOverride;
            conversation.PCQQHasInfo = item.InfoGroupId != 0 || item.InfoGroupCode != 0;
        }
        else if (item.ConversationType == PCQQConversationType.Private &&
                 !string.IsNullOrWhiteSpace(item.DisplayNameOverride))
        {
            conversation.GroupName = item.DisplayNameOverride;
        }

        conversation.LatestMessageText = LatestMessagePreviewFactory.CreatePCQQ(
            item.ConversationType,
            item.LatestMessageText,
            item.LatestMessageSenderUin,
            item.LatestMessageSenderNickname);
        conversation.LatestMessageTime = MessageConversationTime.ClampUnixTime(item.LatestMessageTime);
    }

    private static void ApplyIcalinguaConversation(AvaQQGroup conversation, IcalinguaConversation item)
    {
        conversation.GroupName = FirstNonEmpty(item.DisplayName, item.RoomId.ToString());
        conversation.IcalinguaDownloadPath = item.DownloadPath;
        conversation.LatestMessageText = item.LatestMessageText;
        conversation.LatestMessageTime = MessageConversationTime.ClampUnixTime(item.LatestMessageTime);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }
}
