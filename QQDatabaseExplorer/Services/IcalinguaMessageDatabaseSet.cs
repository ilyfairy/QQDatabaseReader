using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.Services;

public sealed class IcalinguaMessageDatabaseSet : IDisposable
{
    private readonly List<IcalinguaMessageDatabaseEntry> _databases = [];
    private readonly IcalinguaExternalMessageIdMapper _idMapper = new();
    private readonly IcalinguaMessageDatabaseSearchService _searchService;
    private readonly IcalinguaMessageTimelineDatabaseService _timelineService;
    private IReadOnlyList<IcalinguaConversation>? _conversationCache;

    public IcalinguaMessageDatabaseSet(IEnumerable<IcalinguaMessageDatabaseEntry> databases)
    {
        _databases = databases.ToList();
        _searchService = new IcalinguaMessageDatabaseSearchService(_databases, _idMapper, GetConversations);
        _timelineService = new IcalinguaMessageTimelineDatabaseService(_databases, _idMapper);
    }

    public IReadOnlyList<IcalinguaMessageDatabaseEntry> Databases => _databases;

    public int Count => _databases.Count;

    public bool IsEmpty => _databases.Count == 0;

    public IcalinguaMessageReader? PrimaryReader => _databases.FirstOrDefault()?.Reader;

    public string? PrimaryDataPath => _databases.FirstOrDefault()?.DataPath;

    public IReadOnlyList<string> DataPaths => _databases
        .Select(static database => database.DataPath)
        .Where(static path => !string.IsNullOrWhiteSpace(path))
        .Select(static path => path!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public IReadOnlyList<IcalinguaConversation> GetConversations()
    {
        if (_conversationCache is { } cachedConversations)
            return cachedConversations;

        _conversationCache = BuildConversations();
        return _conversationCache;
    }

    public IReadOnlyDictionary<long, IcalinguaConversation> GetConversationsByRoomIds(IEnumerable<long> roomIds)
    {
        var requestedRoomIds = roomIds
            .Where(static roomId => roomId != 0)
            .Distinct()
            .ToArray();
        if (requestedRoomIds.Length == 0)
            return new Dictionary<long, IcalinguaConversation>();

        if (_conversationCache is { } cachedConversations)
        {
            return cachedConversations
                .Where(conversation => requestedRoomIds.Contains(conversation.RoomId))
                .GroupBy(static conversation => conversation.RoomId)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.First());
        }

        var requestedRoomIdSet = requestedRoomIds.ToHashSet();
        return _databases
            .SelectMany((database, index) => database.Reader.GetConversationsByRoomIds(requestedRoomIdSet)
                .Select(conversation => new IcalinguaConversationEntry(index, conversation.Value)))
            .GroupBy(static item => item.Conversation.RoomId)
            .Select(CreateMergedConversation)
            .ToDictionary(static conversation => conversation.RoomId);
    }

    public IReadOnlyList<IcalinguaMessageRecord> LoadLatestMessages(long roomId, int pageSize, MessageQueryFilter? filter = null)
    {
        return _timelineService.LoadLatestMessages(roomId, pageSize, filter);
    }

    public IReadOnlyList<IcalinguaMessageRecord> LoadEarliestMessages(long roomId, int pageSize, MessageQueryFilter? filter = null)
    {
        return _timelineService.LoadEarliestMessages(roomId, pageSize, filter);
    }

    public IReadOnlyList<IcalinguaMessageRecord> LoadOlderMessages(
        long roomId,
        long messageSortTime,
        long messageId,
        int pageSize,
        MessageQueryFilter? filter = null)
    {
        return _timelineService.LoadOlderMessages(roomId, messageSortTime, messageId, pageSize, filter);
    }

    public IReadOnlyList<IcalinguaMessageRecord> LoadNewerMessages(
        long roomId,
        long messageSortTime,
        long messageId,
        int pageSize,
        MessageQueryFilter? filter = null)
    {
        return _timelineService.LoadNewerMessages(roomId, messageSortTime, messageId, pageSize, filter);
    }

    public IcalinguaMessageRecord? LoadMessage(long roomId, long messageSortTime, long messageId)
    {
        var cursor = _idMapper.Decode(messageId);
        if (cursor.DatabaseIndex is { } databaseIndex &&
            GetDatabaseByIndex(databaseIndex) is { } entry)
        {
            return WrapMessageOrNull(entry, entry.Reader.LoadMessage(roomId, messageSortTime, cursor.InnerMessageId));
        }

        foreach (var database in _databases)
        {
            if (WrapMessageOrNull(database, database.Reader.LoadMessage(roomId, messageSortTime, messageId)) is { } message)
                return message;
        }

        return null;
    }

    public IcalinguaMessageRecord? LoadMessageByRawId(long roomId, string? rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId))
            return null;

        foreach (var database in _databases)
        {
            if (WrapMessageOrNull(database, database.Reader.LoadMessageByRawId(roomId, rawId)) is { } message)
                return message;
        }

        return null;
    }

    public IReadOnlyList<IcalinguaMessageDate> LoadMessageDates(long roomId)
    {
        return _databases
            .SelectMany(database => database.Reader.LoadMessageDates(roomId))
            .GroupBy(static date => date.DayStartTime)
            .Select(static group => new IcalinguaMessageDate(group.Key, group.Sum(date => date.MessageCount)))
            .OrderBy(static date => date.DayStartTime)
            .ToArray();
    }

    public IReadOnlyList<IcalinguaSender> LoadSenders(long roomId)
    {
        return _databases
            .SelectMany(database => database.Reader.LoadSenders(roomId))
            .GroupBy(static sender => sender.SenderId)
            .Select(group => new IcalinguaSender(
                group.Key,
                FirstNonEmpty(group.Select(static sender => sender.DisplayName).ToArray())
                    ?? group.Key.ToString(CultureInfo.InvariantCulture)))
            .OrderBy(static sender => sender.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static sender => sender.SenderId)
            .ToArray();
    }

    public long CountSearchMatches(string keyword, long? roomId)
    {
        return _searchService.CountSearchMatches(keyword, roomId);
    }

    public long CountSearchMatches(string keyword, IReadOnlyList<long>? roomIds)
    {
        return _searchService.CountSearchMatches(keyword, roomIds);
    }

    public IReadOnlyList<IcalinguaMessageSearchGroupCount> CountSearchMatchesByRoom(
        string keyword,
        long? roomId,
        int? maxRooms = null)
    {
        return _searchService.CountSearchMatchesByRoom(keyword, roomId, maxRooms);
    }

    public IReadOnlyList<IcalinguaMessageSearchGroupCount> CountSearchMatchesByRoom(
        string keyword,
        IReadOnlyList<long>? roomIds,
        int? maxRooms = null)
    {
        return _searchService.CountSearchMatchesByRoom(keyword, roomIds, maxRooms);
    }

    public IReadOnlyList<IcalinguaMessageRecord> SearchMessages(
        string keyword,
        long? roomId,
        int pageSize,
        IcalinguaMessageSearchCursor? cursor = null)
    {
        return _searchService.SearchMessages(keyword, roomId, pageSize, cursor);
    }

    public IReadOnlyList<IcalinguaMessageRecord> SearchMessages(
        string keyword,
        IReadOnlyList<long>? roomIds,
        int pageSize,
        IcalinguaMessageSearchCursor? cursor = null)
    {
        return _searchService.SearchMessages(keyword, roomIds, pageSize, cursor);
    }

    public IReadOnlyList<IcalinguaMessageRecord> SearchMessagesInRoom(
        long roomId,
        string keyword,
        IcalinguaMessageSearchCursor? cursor,
        int pageSize)
    {
        return _searchService.SearchMessagesInRoom(roomId, keyword, cursor, pageSize);
    }

    public void Dispose()
    {
        foreach (var database in _databases)
        {
            database.Reader.Dispose();
        }

        _databases.Clear();
    }

    private IcalinguaMessageRecord WrapMessage(IcalinguaMessageDatabaseEntry database, IcalinguaMessageRecord message)
    {
        return message with
        {
            MessageId = _idMapper.GetExternalMessageId(database.Index, message.MessageId),
        };
    }

    private IcalinguaMessageRecord? WrapMessageOrNull(IcalinguaMessageDatabaseEntry database, IcalinguaMessageRecord? message)
    {
        return message is null ? null : WrapMessage(database, message);
    }

    private IcalinguaMessageDatabaseEntry? GetDatabaseByIndex(int databaseIndex)
    {
        return databaseIndex >= 0 && databaseIndex < _databases.Count
            ? _databases[databaseIndex]
            : null;
    }

    private IReadOnlyList<IcalinguaConversation> BuildConversations()
    {
        return _databases
            .SelectMany((database, index) => database.Reader.GetConversations()
                .Select(conversation => new IcalinguaConversationEntry(index, conversation)))
            .GroupBy(static item => item.Conversation.RoomId)
            .Select(CreateMergedConversation)
            .OrderByDescending(static conversation => conversation.LatestMessageTime)
            .ThenBy(static conversation => conversation.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IcalinguaConversation CreateMergedConversation(IGrouping<long, IcalinguaConversationEntry> group)
    {
        var ordered = group
            .OrderByDescending(static item => item.Conversation.LatestMessageTime)
            .ThenBy(static item => item.DatabaseIndex)
            .ToArray();
        var first = ordered[0].Conversation;
        return new IcalinguaConversation(
            first.RoomId,
            FirstNonEmpty(ordered.Select(static item => item.Conversation.DisplayName).ToArray())
                ?? first.RoomId.ToString(CultureInfo.InvariantCulture),
            ordered.Max(static item => item.Conversation.LatestMessageTime),
            FirstNonEmpty(ordered.Select(static item => item.Conversation.LatestMessageText).ToArray()) ?? string.Empty,
            ordered.Any(static item => item.Conversation.IsGroup),
            FirstNonEmpty(ordered.Select(static item => item.Conversation.DownloadPath).ToArray()));
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }

    private sealed record IcalinguaConversationEntry(int DatabaseIndex, IcalinguaConversation Conversation);
}

public sealed record IcalinguaMessageDatabaseEntry(
    int Index,
    IcalinguaMessageReader Reader,
    DatabaseConfig Config,
    string? DataPath);
