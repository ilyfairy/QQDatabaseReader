using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class QQNtConversationCatalogSource(
    MessageConversationDirectoryLoader directoryLoader,
    MessageConversationListApplier conversationApplier) : IConversationCatalogSource
{
    public bool CanApply(IQQDatabase database)
    {
        return database is QQMessageReader;
    }

    public async Task ApplyAsync(IQQDatabase database)
    {
        if (database is not QQMessageReader messageDatabase)
            return;

        var conversations = await Task.Run(() =>
            directoryLoader.LoadQQNtMessageConversations(messageDatabase));
        conversationApplier.ApplyMessageConversations(conversations);
    }
}

internal sealed class AndroidQQNtConversationCatalogSource(
    MessageConversationDirectoryLoader directoryLoader,
    MessageConversationListApplier conversationApplier) : IConversationCatalogSource
{
    public bool CanApply(IQQDatabase database)
    {
        return database is QQAndroidMessageReader;
    }

    public async Task ApplyAsync(IQQDatabase database)
    {
        if (database is not QQAndroidMessageReader messageDatabase)
            return;

        var conversations = await Task.Run(() =>
            directoryLoader.LoadAndroidQQNtMessageConversations(messageDatabase));
        conversationApplier.ApplyMessageConversations(conversations);
    }
}

internal sealed class QQNtGroupInfoConversationCatalogSource(
    MessageConversationDirectoryLoader directoryLoader,
    MessageConversationListApplier conversationApplier) : IConversationCatalogSource
{
    public bool CanApply(IQQDatabase database)
    {
        return database is QQGroupInfoReader;
    }

    public async Task ApplyAsync(IQQDatabase database)
    {
        if (database is not QQGroupInfoReader groupDatabase)
            return;

        var groups = await Task.Run(() => directoryLoader.LoadGroupInfoItems(groupDatabase));
        conversationApplier.ApplyGroupInfoItems(groups);
    }
}

internal sealed class PCQQConversationCatalogSource(
    MessageConversationDirectoryLoader directoryLoader,
    MessageConversationListApplier conversationApplier) : IConversationCatalogSource
{
    public bool CanApply(IQQDatabase database)
    {
        return database is PCQQMessageReader;
    }

    public async Task ApplyAsync(IQQDatabase database)
    {
        if (database is not PCQQMessageReader pcqqMessageDatabase)
            return;

        var conversations = await Task.Run(() =>
            directoryLoader.LoadPCQQMessageConversations(pcqqMessageDatabase));
        conversationApplier.ApplyPCQQMessageConversations(conversations);
    }
}

internal sealed class IcalinguaConversationCatalogSource(
    MessageConversationDirectoryLoader directoryLoader,
    MessageConversationListApplier conversationApplier) : IConversationCatalogSource
{
    public bool CanApply(IQQDatabase database)
    {
        return database is IcalinguaMessageReader;
    }

    public async Task ApplyAsync(IQQDatabase database)
    {
        var conversations = await Task.Run(directoryLoader.LoadIcalinguaMessageConversations);
        conversationApplier.ApplyIcalinguaMessageConversations(conversations);
    }
}

internal sealed class AndroidMobileQQConversationCatalogSource(
    MessageConversationDirectoryLoader directoryLoader,
    MessageConversationListApplier conversationApplier) : IConversationCatalogSource
{
    public bool CanApply(IQQDatabase database)
    {
        return database is AndroidMobileQQMessageReader;
    }

    public async Task ApplyAsync(IQQDatabase database)
    {
        if (database is not AndroidMobileQQMessageReader messageDatabase)
            return;

        var conversations = await Task.Run(() =>
            directoryLoader.LoadAndroidMobileQQMessageConversations(messageDatabase));
        conversationApplier.ApplyAndroidMobileQQMessageConversations(conversations);
    }
}

internal sealed class QQNtMessageConversationRemovalHandler(
    MessageConversationListApplier conversationApplier) : IConversationDatabaseRemovalHandler
{
    public bool CanRemove(IQQDatabase database)
    {
        return database is QQMessageReader or QQAndroidMessageReader;
    }

    public ConversationDatabaseRemovalResult Remove(
        IQQDatabase database,
        ConversationDatabaseRemovalContext context)
    {
        if (!context.HasGroupInfoDatabase)
            conversationApplier.ClearConversations();

        return new ConversationDatabaseRemovalResult(
            Handled: true,
            ClearMessages: true,
            ClearMessageSelection: true,
            ClearSenderCache: true,
            ClearAvatarPathCaches: true,
            ClearGroupSelection: !context.HasGroupInfoDatabase,
            RefreshFilteredGroups: !context.HasGroupInfoDatabase);
    }
}

internal sealed class PCQQConversationRemovalHandler(
    MessageConversationListApplier conversationApplier) : IConversationDatabaseRemovalHandler
{
    public bool CanRemove(IQQDatabase database)
    {
        return database is PCQQMessageReader;
    }

    public ConversationDatabaseRemovalResult Remove(
        IQQDatabase database,
        ConversationDatabaseRemovalContext context)
    {
        conversationApplier.RemoveConversations(static conversation =>
            conversation.ConversationType is AvaConversationType.PCQQGroup or AvaConversationType.PCQQPrivate);
        return ConversationDatabaseRemovalResults.ClearMessageDatabase;
    }
}

internal sealed class IcalinguaConversationRemovalHandler(
    MessageConversationListApplier conversationApplier) : IConversationDatabaseRemovalHandler
{
    public bool CanRemove(IQQDatabase database)
    {
        return database is IcalinguaMessageReader;
    }

    public ConversationDatabaseRemovalResult Remove(
        IQQDatabase database,
        ConversationDatabaseRemovalContext context)
    {
        conversationApplier.RemoveConversations(static conversation =>
            conversation.ConversationType == AvaConversationType.Icalingua);
        return ConversationDatabaseRemovalResults.ClearMessageDatabase with { ClearAvatarPathCaches = false };
    }
}

internal sealed class AndroidMobileQQConversationRemovalHandler(
    MessageConversationListApplier conversationApplier) : IConversationDatabaseRemovalHandler
{
    public bool CanRemove(IQQDatabase database)
    {
        return database is AndroidMobileQQMessageReader;
    }

    public ConversationDatabaseRemovalResult Remove(
        IQQDatabase database,
        ConversationDatabaseRemovalContext context)
    {
        conversationApplier.RemoveConversations(static conversation =>
            conversation.ConversationType is AvaConversationType.AndroidMobileQQGroup or AvaConversationType.AndroidMobileQQPrivate);
        return ConversationDatabaseRemovalResults.ClearMessageDatabase;
    }
}

internal sealed class QQNtGroupInfoConversationRemovalHandler(
    MessageConversationListApplier conversationApplier) : IConversationDatabaseRemovalHandler
{
    public bool CanRemove(IQQDatabase database)
    {
        return database is QQGroupInfoReader;
    }

    public ConversationDatabaseRemovalResult Remove(
        IQQDatabase database,
        ConversationDatabaseRemovalContext context)
    {
        if (!context.HasNtMessageDatabase)
        {
            conversationApplier.ClearConversations();
            return new ConversationDatabaseRemovalResult(
                Handled: true,
                ClearMessages: false,
                ClearMessageSelection: false,
                ClearSenderCache: false,
                ClearAvatarPathCaches: false,
                ClearGroupSelection: true,
                RefreshFilteredGroups: true);
        }

        conversationApplier.ClearQQNtGroupNames();
        return new ConversationDatabaseRemovalResult(
            Handled: true,
            ClearMessages: false,
            ClearMessageSelection: false,
            ClearSenderCache: false,
            ClearAvatarPathCaches: false,
            ClearGroupSelection: false,
            RefreshFilteredGroups: true);
    }
}

internal sealed class MessageConversationDatabaseSynchronizer
{
    private readonly MessageConversationDirectoryLoader _directoryLoader;
    private readonly MessageConversationListApplier _conversationApplier;
    private readonly IReadOnlyList<IConversationCatalogSource> _catalogSources;
    private readonly IReadOnlyList<IConversationDatabaseRemovalHandler> _removalHandlers;

    public MessageConversationDatabaseSynchronizer(
        MessageConversationDirectoryLoader directoryLoader,
        MessageConversationListApplier conversationApplier)
    {
        _directoryLoader = directoryLoader;
        _conversationApplier = conversationApplier;
        _catalogSources =
        [
            new QQNtConversationCatalogSource(_directoryLoader, _conversationApplier),
            new AndroidQQNtConversationCatalogSource(_directoryLoader, _conversationApplier),
            new QQNtGroupInfoConversationCatalogSource(_directoryLoader, _conversationApplier),
            new PCQQConversationCatalogSource(_directoryLoader, _conversationApplier),
            new AndroidMobileQQConversationCatalogSource(_directoryLoader, _conversationApplier),
            new IcalinguaConversationCatalogSource(_directoryLoader, _conversationApplier),
        ];
        _removalHandlers =
        [
            new QQNtMessageConversationRemovalHandler(_conversationApplier),
            new PCQQConversationRemovalHandler(_conversationApplier),
            new AndroidMobileQQConversationRemovalHandler(_conversationApplier),
            new IcalinguaConversationRemovalHandler(_conversationApplier),
            new QQNtGroupInfoConversationRemovalHandler(_conversationApplier),
        ];
    }

    public async Task<bool> TryApplyAsync(IQQDatabase database)
    {
        if (_catalogSources.FirstOrDefault(source => source.CanApply(database)) is { } catalogSource)
        {
            await catalogSource.ApplyAsync(database);
            return true;
        }

        return false;
    }

    public ConversationDatabaseRemovalResult TryRemove(
        IQQDatabase database,
        bool hasGroupInfoDatabase,
        bool hasNtMessageDatabase)
    {
        if (_removalHandlers.FirstOrDefault(handler => handler.CanRemove(database)) is not { } removalHandler)
            return default;

        return removalHandler.Remove(
            database,
            new ConversationDatabaseRemovalContext(hasGroupInfoDatabase, hasNtMessageDatabase));
    }
}
