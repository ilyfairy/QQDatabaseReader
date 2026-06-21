using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Services;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class MessageConversationDirectoryLoader
{
    private readonly LatestMessagePreviewFactory _latestMessagePreviewFactory;
    private readonly MessageParticipantResolver _participantResolver;
    private readonly Func<IcalinguaMessageDatabaseSet?> _getIcalinguaMessageDatabases;

    public MessageConversationDirectoryLoader(
        LatestMessagePreviewFactory latestMessagePreviewFactory,
        MessageParticipantResolver participantResolver,
        Func<IcalinguaMessageDatabaseSet?> getIcalinguaMessageDatabases)
    {
        _latestMessagePreviewFactory = latestMessagePreviewFactory;
        _participantResolver = participantResolver;
        _getIcalinguaMessageDatabases = getIcalinguaMessageDatabases;
    }

    public IReadOnlyList<GroupInfoLoadItem> LoadGroupInfoItems(QQGroupInfoReader groupDatabase)
    {
        return groupDatabase.DbContext.GroupList
            .Where(v => v.GroupId != 0)
            .Select(group => new GroupInfoLoadItem(group.GroupId, group.GroupName))
            .ToList();
    }

    public List<ConversationLoadItem> LoadQQNtMessageConversations(QQMessageReader messageDatabase)
    {
        var items = QQNtRecentConversationCatalogReader.Read(messageDatabase).ToList();
        items.AddRange(MessageTableConversationCatalogReader.ReadQQNtFallbackConversations(messageDatabase));
        return CreateConversationLoadItems(items);
    }

    public List<ConversationLoadItem> LoadAndroidQQNtMessageConversations(QQAndroidMessageReader messageDatabase)
    {
        var items = AndroidQQNtRecentConversationCatalogReader.Read(messageDatabase).ToList();
        items.AddRange(MessageTableConversationCatalogReader.ReadAndroidQQNtFallbackConversations(messageDatabase));
        return CreateConversationLoadItems(items);
    }

    public IReadOnlyList<PCQQConversation> LoadPCQQMessageConversations(PCQQMessageReader messageDatabase)
    {
        return messageDatabase.GetConversations();
    }

    public IReadOnlyList<AndroidMobileQQConversation> LoadAndroidMobileQQMessageConversations(AndroidMobileQQMessageReader messageDatabase)
    {
        return messageDatabase.GetConversations();
    }

    public IReadOnlyList<IcalinguaConversation> LoadIcalinguaMessageConversations()
    {
        return _getIcalinguaMessageDatabases()?.GetConversations() ?? [];
    }

    private List<ConversationLoadItem> CreateConversationLoadItems(
        IReadOnlyList<MessageConversationCatalogItem> catalogItems)
    {
        return catalogItems
            .Select(CreateConversationLoadItem)
            .ToList();
    }

    private ConversationLoadItem CreateConversationLoadItem(MessageConversationCatalogItem item)
    {
        return new ConversationLoadItem(
            item.ConversationType,
            item.GroupId,
            item.PrivateConversationId,
            item.PrivateUin,
            item.PrivateUid,
            item.DisplayName,
            _latestMessagePreviewFactory.Create(item),
            _participantResolver.ResolveRecentAvatarLocalPath(item.AvatarPath),
            item.LastTime);
    }
}
