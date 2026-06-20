using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ObservableCollections;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels;

internal sealed record ConversationMessageSearchComposition(
    ConversationSearchFilterParser FilterParser,
    ConversationMessageSearchProviderFactory ProviderFactory,
    ConversationMessageSearchWorkflow Workflow);

internal sealed class ConversationMessageSearchProviderFactory
{
    private readonly QQDatabaseService _databaseService;
    private readonly QqNtSearchMetadataLoader _qqNtSearchMetadataLoader;

    public ConversationMessageSearchProviderFactory(
        QQDatabaseService databaseService,
        QqNtSearchMetadataLoader qqNtSearchMetadataLoader)
    {
        _databaseService = databaseService;
        _qqNtSearchMetadataLoader = qqNtSearchMetadataLoader;
    }

    public bool HasSearchDatabase =>
        _databaseService.GroupMessageFtsDatabase is not null ||
        _databaseService.IcalinguaMessageDatabases is not null;

    public string ReadyStatusText => _databaseService.GroupMessageFtsDatabase is not null
        ? "输入关键词，支持中文、拼音、首字母、URL"
        : "输入关键词，搜索 Icalingua 聊天记录";

    public bool ShouldRefreshFor(IQQDatabase database)
    {
        return database.DatabaseType is QQDatabaseType.GroupMessageFts or QQDatabaseType.IcalinguaMessage;
    }

    public SearchDatabaseKind GetPreferredKind()
    {
        if (_databaseService.GroupMessageFtsDatabase is not null)
            return SearchDatabaseKind.GroupMessageFts;

        if (_databaseService.IcalinguaMessageDatabases is not null)
            return SearchDatabaseKind.Icalingua;

        return SearchDatabaseKind.None;
    }

    public bool IsAvailable(SearchDatabaseKind kind)
    {
        return kind switch
        {
            SearchDatabaseKind.GroupMessageFts => _databaseService.GroupMessageFtsDatabase is not null,
            SearchDatabaseKind.Icalingua => _databaseService.IcalinguaMessageDatabases is not null,
            SearchDatabaseKind.None => false,
            _ => false,
        };
    }

    public IConversationMessageSearchProvider CreatePreferred()
    {
        return Create(GetPreferredKind());
    }

    public IConversationMessageSearchProvider Create(SearchDatabaseKind kind)
    {
        return kind switch
        {
            SearchDatabaseKind.GroupMessageFts when _databaseService.GroupMessageFtsDatabase is { } ftsDatabase =>
                new QqNtFtsMessageSearchProvider(ftsDatabase, _qqNtSearchMetadataLoader),
            SearchDatabaseKind.Icalingua when _databaseService.IcalinguaMessageDatabases is { } icalinguaDatabase =>
                new IcalinguaMessageSearchProvider(icalinguaDatabase),
            _ => throw new InvalidOperationException("当前没有可用的搜索数据库"),
        };
    }
}

internal static class ConversationMessageSearchCompositionFactory
{
    public static ConversationMessageSearchComposition Create(
        QQDatabaseService databaseService,
        ObservableList<AvaGroupMessageSearchGroup> groups,
        ConversationMessageSearchResultStore resultStore,
        ConversationMessageSearchSession searchSession)
    {
        var metadataLoader = new QqNtSearchMetadataLoader(databaseService);
        var filterParser = new ConversationSearchFilterParser(metadataLoader);
        var providerFactory = new ConversationMessageSearchProviderFactory(
            databaseService,
            metadataLoader);
        var workflow = new ConversationMessageSearchWorkflow(
            groups,
            providerFactory,
            resultStore,
            metadataLoader,
            searchSession);

        return new ConversationMessageSearchComposition(
            filterParser,
            providerFactory,
            workflow);
    }
}

internal sealed class ConversationMessageSearchWorkflow
{
    private readonly ObservableList<AvaGroupMessageSearchGroup> _groups;
    private readonly ConversationMessageSearchProviderFactory _providerFactory;
    private readonly ConversationMessageSearchResultStore _resultStore;
    private readonly QqNtSearchMetadataLoader _metadataLoader;
    private readonly ConversationMessageSearchSession _searchSession;

    public ConversationMessageSearchWorkflow(
        ObservableList<AvaGroupMessageSearchGroup> groups,
        ConversationMessageSearchProviderFactory providerFactory,
        ConversationMessageSearchResultStore resultStore,
        QqNtSearchMetadataLoader metadataLoader,
        ConversationMessageSearchSession searchSession)
    {
        _groups = groups;
        _providerFactory = providerFactory;
        _resultStore = resultStore;
        _metadataLoader = metadataLoader;
        _searchSession = searchSession;
    }

    public bool IsLoadMoreAvailable => _providerFactory.IsAvailable(_searchSession.DatabaseKind);

    // 搜索页的流程协调放在这里，具体数据库差异仍然留给 IConversationMessageSearchProvider。
    public async Task<ConversationInitialSearchResult> SearchAsync(
        string query,
        ConversationSearchFilter filter,
        AvaGroupMessageSearchGroup? selectedGroup)
    {
        var databaseKind = _providerFactory.GetPreferredKind();
        _searchSession.Start(query, filter, databaseKind);

        var searchProvider = _providerFactory.Create(databaseKind);
        var firstPage = await LoadDiscoveryPageAsync(searchProvider, query, filter, cursor: null);

        var refreshedSelection = ApplyDiscoveryPage(firstPage, selectedGroup, sortGroups: false);

        return new ConversationInitialSearchResult(searchProvider, filter, refreshedSelection);
    }

    public async Task<AvaGroupMessageSearchGroup?> LoadMoreDiscoveryAsync(AvaGroupMessageSearchGroup? selectedGroup)
    {
        var searchProvider = _providerFactory.Create(_searchSession.DatabaseKind);
        var page = await LoadDiscoveryPageAsync(
            searchProvider,
            _searchSession.Query,
            _searchSession.Filter,
            _resultStore.NextDiscoveryCursor);

        return ApplyDiscoveryPage(page, selectedGroup, sortGroups: false);
    }

    public AvaGroupMessageSearchGroup? CompleteDiscovery(AvaGroupMessageSearchGroup? selectedGroup)
    {
        _searchSession.TotalMatchCount = _resultStore.DiscoveryResultCount;
        _searchSession.IsDiscoveringResults = false;
        _searchSession.HasCompletedDiscovery = true;
        return _resultStore.MarkDiscoveryComplete(selectedGroup);
    }

    public async Task<AvaGroupMessageSearchGroup?> LoadMoreAsync(AvaGroupMessageSearchGroup? selectedGroup)
    {
        var searchProvider = _providerFactory.Create(_searchSession.DatabaseKind);
        var loadMoreResult = await LoadMorePageAsync(
            searchProvider,
            _searchSession.Query,
            _searchSession.Filter,
            selectedGroup);

        if (loadMoreResult.SelectedGroupKey is not null &&
            selectedGroup is not null &&
            loadMoreResult.IsConversationPage)
        {
            _resultStore.AppendGroupPage(loadMoreResult.SelectedGroupKey, loadMoreResult.Page);
            return selectedGroup;
        }

        return loadMoreResult.Page.Results.Count == 0
            ? selectedGroup
            : ApplyDiscoveryPage(loadMoreResult.Page, selectedGroup, sortGroups: true);
    }

    public bool ShouldLoadConversationPage(AvaGroupMessageSearchGroup conversation, SearchDatabaseKind databaseKind)
    {
        return !_searchSession.IsDiscoveringResults &&
            !_resultStore.IsDiscoveryComplete &&
            !_resultStore.HasConversationCursor(conversation) &&
            databaseKind != SearchDatabaseKind.None &&
            HasConversationIdentity(conversation) &&
            _providerFactory.IsAvailable(databaseKind);
    }

    public async Task<ConversationSearchPageLoadResult> LoadConversationPageAsync(
        AvaGroupMessageSearchGroup conversation,
        string query,
        SearchDatabaseKind databaseKind)
    {
        var groupKey = ConversationMessageSearchResultStore.GetGroupKey(conversation);
        var provider = _providerFactory.Create(databaseKind);
        var page = await Task.Run(() => provider.SearchConversation(query, conversation, cursor: null));
        return new ConversationSearchPageLoadResult(groupKey, page);
    }

    public async Task<IReadOnlyList<AvaGroupMessageSearchResult>> GetEnrichedVisibleResultsAsync(
        AvaGroupMessageSearchGroup conversation)
    {
        var visibleResults = _resultStore.GetVisibleResults(conversation);
        if (visibleResults.Count == 0)
            return visibleResults;

        var senderInfos = await Task.Run(() => _metadataLoader.LoadSenderInfos(visibleResults));
        return visibleResults
            .Select(result =>
            {
                return result.GroupId != 0 &&
                    senderInfos.TryGetValue(new SearchMessageKey(result.GroupId, result.MessageSeq), out var senderInfo)
                    ? ApplySenderInfo(result, senderInfo)
                    : result;
            })
            .ToArray();
    }

    private AvaGroupMessageSearchGroup? ApplyDiscoveryPage(
        SearchPage page,
        AvaGroupMessageSearchGroup? selectedGroup,
        bool sortGroups)
    {
        var refreshedSelection = _resultStore.AppendDiscoveryPage(
            page,
            _searchSession.Query,
            _searchSession.IsDiscoveringResults,
            _searchSession.HasCompletedDiscovery,
            selectedGroup,
            sortGroups);
        return refreshedSelection ?? _groups.FirstOrDefault();
    }

    private static Task<SearchPage> LoadDiscoveryPageAsync(
        IConversationMessageSearchProvider provider,
        string query,
        ConversationSearchFilter filter,
        SearchCursor? cursor)
    {
        return Task.Run(() => provider.SearchDiscovery(query, filter, cursor));
    }

    private Task<ConversationSearchLoadMoreResult> LoadMorePageAsync(
        IConversationMessageSearchProvider provider,
        string query,
        ConversationSearchFilter filter,
        AvaGroupMessageSearchGroup? selectedGroup)
    {
        var selectedGroupKey = selectedGroup is null
            ? null
            : ConversationMessageSearchResultStore.GetGroupKey(selectedGroup);
        SearchCursor? groupCursor = null;
        var isConversationPage = false;
        if (selectedGroup is not null &&
            _resultStore.TryGetConversationCursor(selectedGroup, out _, out var cursor))
        {
            groupCursor = cursor;
            isConversationPage = true;
        }

        var discoveryCursor = _resultStore.NextDiscoveryCursor;

        return Task.Run(() =>
        {
            var page = isConversationPage
                ? provider.SearchConversation(query, selectedGroup!, groupCursor)
                : provider.SearchDiscovery(query, filter, discoveryCursor);
            return new ConversationSearchLoadMoreResult(page, selectedGroupKey, isConversationPage);
        });
    }

    private static bool HasConversationIdentity(AvaGroupMessageSearchGroup conversation)
    {
        return conversation.GroupId != 0 ||
            conversation.PrivateConversationId != 0 ||
            conversation.IcalinguaRoomId != 0;
    }

    private static AvaGroupMessageSearchResult ApplySenderInfo(
        AvaGroupMessageSearchResult result,
        SearchSenderInfo senderInfo)
    {
        return new AvaGroupMessageSearchResult
        {
            MessageId = result.MessageId,
            MessageSeq = result.MessageSeq,
            MessageTime = result.MessageTime,
            GroupId = result.GroupId,
            PrivateConversationId = result.PrivateConversationId,
            PeerUin = result.PeerUin,
            IcalinguaRoomId = result.IcalinguaRoomId,
            GroupName = result.GroupName,
            PeerUid = result.PeerUid,
            SenderUid = result.SenderUid,
            SenderId = senderInfo.SenderId,
            SenderName = senderInfo.Name,
            PreviewText = result.PreviewText,
        };
    }
}

internal sealed record ConversationInitialSearchResult(
    IConversationMessageSearchProvider Provider,
    ConversationSearchFilter Filter,
    AvaGroupMessageSearchGroup? SelectedGroup);

internal sealed record ConversationSearchLoadMoreResult(
    SearchPage Page,
    string? SelectedGroupKey,
    bool IsConversationPage);

internal sealed record ConversationSearchPageLoadResult(
    string GroupKey,
    SearchPage Page);
