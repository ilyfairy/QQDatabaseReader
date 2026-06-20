using System.Collections.Generic;
using System.Threading;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels;

internal readonly record struct SearchMessageKey(uint GroupId, long MessageSeq);

internal readonly record struct SearchSenderInfo(uint SenderId, string? Name);

internal sealed record SearchPage(
    List<AvaGroupMessageSearchResult> Results,
    bool HasMore,
    SearchCursor? NextCursor);

internal sealed record SearchCursor(
    long? QqNtBeforeRowId = null,
    IcalinguaMessageSearchCursor? IcalinguaCursor = null);

internal sealed record ConversationSearchFilter(
    uint? GroupOrPeerId,
    IReadOnlyList<long> PrivateConversationIds,
    IReadOnlyList<long> IcalinguaRoomIds)
{
    public static ConversationSearchFilter Empty { get; } = new(null, [], []);
}

internal static class SearchConversationKey
{
    public static string Group(uint groupId)
    {
        return "group:" + groupId;
    }

    public static string Private(long privateConversationId)
    {
        return "private:" + privateConversationId;
    }

    public static string Icalingua(long roomId)
    {
        return "icalingua:" + roomId;
    }
}

internal enum SearchDatabaseKind
{
    None,
    GroupMessageFts,
    Icalingua,
}

internal sealed record PrivateConversationInfo(uint PeerUin, string? PeerUid, string? Name);

internal static class SearchResultPreviewText
{
    public static string Normalize(string? previewText)
    {
        return string.IsNullOrWhiteSpace(previewText)
            ? "[空搜索文本]"
            : previewText;
    }
}

internal sealed class ConversationMessageSearchVersionTracker
{
    private int _searchVersion;
    private int _visibleResultsVersion;

    public int CurrentSearchVersion => _searchVersion;

    public int BeginSearch()
    {
        return Interlocked.Increment(ref _searchVersion);
    }

    public int CancelSearch()
    {
        return Interlocked.Increment(ref _searchVersion);
    }

    public bool IsCurrentSearch(int version)
    {
        return version == _searchVersion;
    }

    public int BeginVisibleResultsRefresh()
    {
        return ++_visibleResultsVersion;
    }

    public bool IsCurrentVisibleResultsRefresh(
        int version,
        AvaGroupMessageSearchGroup? currentSelectedGroup,
        AvaGroupMessageSearchGroup selectedGroup)
    {
        return version == _visibleResultsVersion && ReferenceEquals(currentSelectedGroup, selectedGroup);
    }
}

internal sealed class ConversationMessageSearchSession
{
    public string Query { get; private set; } = string.Empty;

    public ConversationSearchFilter Filter { get; private set; } = ConversationSearchFilter.Empty;

    public long? TotalMatchCount { get; set; }

    public bool IsDiscoveringResults { get; set; }

    public bool HasCompletedDiscovery { get; set; }

    public SearchDatabaseKind DatabaseKind { get; private set; } = SearchDatabaseKind.None;

    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);

    public void Start(
        string query,
        ConversationSearchFilter filter,
        SearchDatabaseKind databaseKind)
    {
        Query = query;
        Filter = filter;
        DatabaseKind = databaseKind;
        TotalMatchCount = null;
        IsDiscoveringResults = true;
        HasCompletedDiscovery = false;
    }

    public void Reset()
    {
        Query = string.Empty;
        Filter = ConversationSearchFilter.Empty;
        TotalMatchCount = null;
        IsDiscoveringResults = false;
        HasCompletedDiscovery = false;
        DatabaseKind = SearchDatabaseKind.None;
    }
}

internal sealed class ConversationSearchFilterParser
{
    private const string InvalidFilterMessage = "只能填写群号、QQ号或 Icalingua roomId";
    private readonly QqNtSearchMetadataLoader _metadataLoader;

    public ConversationSearchFilterParser(QqNtSearchMetadataLoader metadataLoader)
    {
        _metadataLoader = metadataLoader;
    }

    public bool TryParse(
        string? text,
        out ConversationSearchFilter filter,
        out string? errorMessage)
    {
        filter = ConversationSearchFilter.Empty;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(text))
            return true;

        var trimmedText = text.Trim();
        if (uint.TryParse(trimmedText, out var uinOrGroupId) && uinOrGroupId != 0)
        {
            filter = CreateUnsignedFilter(uinOrGroupId);
            return true;
        }

        if (long.TryParse(trimmedText, out var roomId) && roomId != 0)
        {
            filter = roomId > 0 && roomId <= uint.MaxValue
                ? CreateUnsignedFilter((uint)roomId)
                : new ConversationSearchFilter(null, [], [roomId]);
            return true;
        }

        errorMessage = InvalidFilterMessage;
        return false;
    }

    private ConversationSearchFilter CreateUnsignedFilter(uint uinOrGroupId)
    {
        return new ConversationSearchFilter(
            uinOrGroupId,
            _metadataLoader.LoadPrivateConversationIdsByPeerUin(uinOrGroupId),
            CreateIcalinguaRoomFilterCandidates(uinOrGroupId));
    }

    private static IReadOnlyList<long> CreateIcalinguaRoomFilterCandidates(uint uinOrGroupId)
    {
        var value = (long)uinOrGroupId;
        return [value, -value];
    }
}

internal static class ConversationMessageSearchStatusFormatter
{
    public static string CreateNoSearchDatabaseStatus()
    {
        return "打开消息数据库后可以搜索聊天记录";
    }

    public static string CreateSearchSummary(
        long discoveryResultCount,
        long? totalMatchCount,
        int conversationCount,
        bool isCountingConversations,
        bool hasMoreResults,
        SearchDatabaseKind databaseKind)
    {
        if (discoveryResultCount == 0)
            return "没有找到匹配的聊天记录";

        var suffix = hasMoreResults
            ? isCountingConversations
                ? "，正在加载更多"
                : "，继续向下滚动加载更多"
            : string.Empty;
        var totalText = totalMatchCount is { } totalCount
            ? $"共 {totalCount} 条匹配，"
            : string.Empty;
        var targetName = GetSearchTargetName(databaseKind);
        var groupText = isCountingConversations
            ? $"已找到 {conversationCount} 个{targetName}，正在继续搜索"
            : $"来自 {conversationCount} 个{targetName}";
        return $"{totalText}已显示 {discoveryResultCount} 条最新匹配记录，{groupText}{suffix}";
    }

    private static string GetSearchTargetName(SearchDatabaseKind databaseKind)
    {
        return databaseKind == SearchDatabaseKind.Icalingua
            ? "会话"
            : "会话";
    }
}
