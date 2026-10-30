using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using QQDatabaseReader;

namespace QQDatabaseExplorer.Services;

internal sealed class IcalinguaMessageDatabaseSearchService
{
    private const int MaxSearchScanRowsPerPage = 5000;
    private readonly IReadOnlyList<IcalinguaMessageDatabaseEntry> _databases;
    private readonly IcalinguaExternalMessageIdMapper _idMapper;
    private readonly Func<IReadOnlyList<IcalinguaConversation>> _getConversations;

    public IcalinguaMessageDatabaseSearchService(
        IReadOnlyList<IcalinguaMessageDatabaseEntry> databases,
        IcalinguaExternalMessageIdMapper idMapper,
        Func<IReadOnlyList<IcalinguaConversation>> getConversations)
    {
        _databases = databases;
        _idMapper = idMapper;
        _getConversations = getConversations;
    }

    public long CountSearchMatches(string keyword, long? roomId)
    {
        return _databases.Sum(database => database.Reader.CountSearchMatches(keyword, roomId));
    }

    public long CountSearchMatches(string keyword, IReadOnlyList<long>? roomIds)
    {
        if (roomIds is not { Count: > 0 })
            return CountSearchMatches(keyword, roomId: null);

        return roomIds
            .Distinct()
            .Sum(roomId => _databases.Sum(database => database.Reader.CountSearchMatchesInRoom(roomId, keyword)));
    }

    public IReadOnlyList<IcalinguaMessageSearchGroupCount> CountSearchMatchesByRoom(
        string keyword,
        long? roomId,
        int? maxRooms = null)
    {
        return _databases
            .SelectMany(database => database.Reader.CountSearchMatchesByRoom(keyword, roomId, maxRooms: null))
            .GroupBy(static count => count.RoomId)
            .Select(group => new IcalinguaMessageSearchGroupCount(
                group.Key,
                group.Sum(static count => count.MatchCount),
                FirstNonEmpty(group.Select(static count => count.DisplayName).ToArray())
                    ?? group.Key.ToString(CultureInfo.InvariantCulture)))
            .Where(static count => count.MatchCount > 0)
            .OrderByDescending(static count => count.MatchCount)
            .ThenBy(static count => count.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(maxRooms.GetValueOrDefault(int.MaxValue))
            .ToArray();
    }

    public IReadOnlyList<IcalinguaMessageSearchGroupCount> CountSearchMatchesByRoom(
        string keyword,
        IReadOnlyList<long>? roomIds,
        int? maxRooms = null)
    {
        if (roomIds is not { Count: > 0 })
            return CountSearchMatchesByRoom(keyword, roomId: null, maxRooms);

        var conversations = _getConversations()
            .ToDictionary(static conversation => conversation.RoomId);

        return roomIds
            .Distinct()
            .Select(roomId => new IcalinguaMessageSearchGroupCount(
                roomId,
                _databases.Sum(database => database.Reader.CountSearchMatchesInRoom(roomId, keyword)),
                conversations.TryGetValue(roomId, out var conversation)
                    ? conversation.DisplayName
                    : GetIcalinguaRoomDisplayId(roomId)))
            .Where(static count => count.MatchCount > 0)
            .OrderByDescending(static count => count.MatchCount)
            .ThenBy(static count => count.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(maxRooms.GetValueOrDefault(int.MaxValue))
            .ToArray();
    }

    public IReadOnlyList<IcalinguaMessageRecord> SearchMessages(
        string keyword,
        long? roomId,
        int pageSize,
        IcalinguaMessageSearchCursor? cursor = null)
    {
        if (roomId is null)
            return SearchDatabasesLatestFirst(keyword, pageSize, cursor);

        return IcalinguaMessageMerger.Merge(
            QueryEach(database => database.Reader.SearchMessages(keyword, roomId, pageSize, CreateCursorFor(database.Index, cursor)))
                .Where(message => cursor is null || IcalinguaMessageMerger.IsBeforeCursor(message, cursor.MessageSortTime, cursor.MessageId)),
            descending: true,
            pageSize);
    }

    public IReadOnlyList<IcalinguaMessageRecord> SearchMessages(
        string keyword,
        IReadOnlyList<long>? roomIds,
        int pageSize,
        IcalinguaMessageSearchCursor? cursor = null)
    {
        if (roomIds is not { Count: > 0 })
            return SearchMessages(keyword, roomId: null, pageSize, cursor);

        var exactRoomIds = roomIds.Distinct().ToArray();
        return IcalinguaMessageMerger.Merge(
            QueryEach(database => exactRoomIds
                    .SelectMany(roomId => database.Reader.SearchMessagesInRoom(
                        roomId,
                        keyword,
                        CreateCursorFor(database.Index, cursor),
                        pageSize))
                    .ToArray())
                .Where(message => cursor is null || IcalinguaMessageMerger.IsBeforeCursor(message, cursor.MessageSortTime, cursor.MessageId)),
            descending: true,
            pageSize);
    }

    public IReadOnlyList<IcalinguaMessageRecord> SearchMessagesInRoom(
        long roomId,
        string keyword,
        IcalinguaMessageSearchCursor? cursor,
        int pageSize)
    {
        return IcalinguaMessageMerger.Merge(
            QueryEach(database => database.Reader.SearchMessagesInRoom(roomId, keyword, CreateCursorFor(database.Index, cursor), pageSize))
                .Where(message => cursor is null || IcalinguaMessageMerger.IsBeforeCursor(message, cursor.MessageSortTime, cursor.MessageId)),
            descending: true,
            pageSize);
    }

    public IcalinguaMessageSearchPage SearchMessagesPage(
        string keyword,
        IReadOnlyList<long>? roomIds,
        int pageSize,
        IcalinguaMessageSearchCursor? cursor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(keyword) || pageSize <= 0 || _databases.Count == 0)
            return new IcalinguaMessageSearchPage([], null, false, 0);

        var databases = _databases
            .OrderByDescending(static database => database.Index)
            .ToArray();
        var databasePosition = FindSearchDatabasePosition(databases, cursor?.ScanDatabaseIndex);
        if (databasePosition >= databases.Length)
            return new IcalinguaMessageSearchPage([], null, false, 0);

        var results = new List<IcalinguaMessageRecord>(pageSize);
        var scannedRows = 0;
        var activeCursor = cursor;
        while (databasePosition < databases.Length &&
               results.Count < pageSize &&
               scannedRows < MaxSearchScanRowsPerPage)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var database = databases[databasePosition];
            var remainingScanRows = MaxSearchScanRowsPerPage - scannedRows;
            var remainingResults = pageSize - results.Count;
            var readerCursor = activeCursor is null
                ? null
                : activeCursor with { ScanDatabaseIndex = null };
            var page = database.Reader.SearchMessagesPage(
                keyword,
                roomIds,
                remainingResults,
                readerCursor,
                remainingScanRows,
                cancellationToken);
            scannedRows += page.ScannedRows;
            results.AddRange(page.Messages.Select(message => WrapMessage(database, message)));

            if (page.HasMore && page.NextCursor is { } readerNextCursor)
            {
                var nextCursor = CreateExternalSearchCursor(
                    database.Index,
                    readerNextCursor,
                    results,
                    cursor);
                return CreateBoundedSearchPage(results, nextCursor, scannedRows, pageSize);
            }

            databasePosition++;
            activeCursor = null;
            if (databasePosition >= databases.Length)
                break;

            if (results.Count >= pageSize || scannedRows >= MaxSearchScanRowsPerPage)
            {
                var nextCursor = CreateNextDatabaseCursor(
                    databases[databasePosition].Index,
                    results,
                    cursor);
                return CreateBoundedSearchPage(results, nextCursor, scannedRows, pageSize);
            }
        }

        return new IcalinguaMessageSearchPage(
            IcalinguaMessageMerger.Merge(results, descending: true, pageSize),
            null,
            false,
            scannedRows);
    }

    public IcalinguaMessageSearchPage SearchMessagesInRoomPage(
        long roomId,
        string keyword,
        int pageSize,
        IcalinguaMessageSearchCursor? cursor,
        CancellationToken cancellationToken)
    {
        return SearchMessagesPage(
            keyword,
            [roomId],
            pageSize,
            cursor,
            cancellationToken);
    }

    private static int FindSearchDatabasePosition(
        IReadOnlyList<IcalinguaMessageDatabaseEntry> databases,
        int? databaseIndex)
    {
        if (databaseIndex is null)
            return 0;

        for (var index = 0; index < databases.Count; index++)
        {
            if (databases[index].Index == databaseIndex.Value)
                return index;
        }

        return databases.Count;
    }

    private IcalinguaMessageSearchPage CreateBoundedSearchPage(
        IReadOnlyList<IcalinguaMessageRecord> results,
        IcalinguaMessageSearchCursor nextCursor,
        int scannedRows,
        int pageSize)
    {
        return new IcalinguaMessageSearchPage(
            IcalinguaMessageMerger.Merge(results, descending: true, pageSize),
            nextCursor,
            true,
            scannedRows);
    }

    private static IcalinguaMessageSearchCursor CreateExternalSearchCursor(
        int databaseIndex,
        IcalinguaMessageSearchCursor readerCursor,
        IReadOnlyList<IcalinguaMessageRecord> results,
        IcalinguaMessageSearchCursor? previousCursor)
    {
        var anchor = results
            .OrderByDescending(static message => message.MessageSeq)
            .ThenByDescending(static message => message.MessageId)
            .LastOrDefault();
        return readerCursor with
        {
            MessageSortTime = anchor?.MessageSeq ?? previousCursor?.MessageSortTime ?? 0,
            MessageId = anchor?.MessageId ?? previousCursor?.MessageId ?? 0,
            ScanDatabaseIndex = databaseIndex,
        };
    }

    private static IcalinguaMessageSearchCursor CreateNextDatabaseCursor(
        int databaseIndex,
        IReadOnlyList<IcalinguaMessageRecord> results,
        IcalinguaMessageSearchCursor? previousCursor)
    {
        var anchor = results
            .OrderByDescending(static message => message.MessageSeq)
            .ThenByDescending(static message => message.MessageId)
            .LastOrDefault();
        return new IcalinguaMessageSearchCursor(
            anchor?.MessageSeq ?? previousCursor?.MessageSortTime ?? 0,
            anchor?.MessageId ?? previousCursor?.MessageId ?? 0,
            databaseIndex,
            ScanSourceIndex: 0,
            ScanBeforeRowId: null);
    }

    private IReadOnlyList<IcalinguaMessageRecord> SearchDatabasesLatestFirst(
        string keyword,
        int pageSize,
        IcalinguaMessageSearchCursor? cursor)
    {
        var result = new List<IcalinguaMessageRecord>();
        var limit = Math.Max(1, pageSize);
        foreach (var database in GetDatabaseSearchOrder(cursor))
        {
            var remaining = limit - result.Count;
            if (remaining <= 0)
                break;

            var messages = database.Reader.SearchMessages(
                keyword,
                roomId: null,
                pageSize: remaining,
                CreateCursorFor(database.Index, cursor));
            result.AddRange(messages.Select(message => WrapMessage(database, message)));

            if (messages.Count >= remaining)
                break;
        }

        return IcalinguaMessageMerger.Merge(result, descending: true, limit);
    }

    private IEnumerable<IcalinguaMessageDatabaseEntry> GetDatabaseSearchOrder(IcalinguaMessageSearchCursor? cursor)
    {
        var decoded = cursor is null
            ? default
            : _idMapper.Decode(cursor.MessageId);
        var startIndex = decoded.DatabaseIndex ?? _databases.Max(static database => database.Index);
        return _databases
            .Where(database => database.Index <= startIndex)
            .OrderByDescending(static database => database.Index);
    }

    private IEnumerable<IcalinguaMessageRecord> QueryEach(Func<IcalinguaMessageDatabaseEntry, IReadOnlyList<IcalinguaMessageRecord>> query)
    {
        return _databases
            .AsParallel()
            .AsOrdered()
            .SelectMany(database => query(database).Select(message => WrapMessage(database, message)));
    }

    private IcalinguaMessageRecord WrapMessage(IcalinguaMessageDatabaseEntry database, IcalinguaMessageRecord message)
    {
        return message with
        {
            MessageId = _idMapper.GetExternalMessageId(database.Index, message.MessageId),
        };
    }

    private IcalinguaMessageSearchCursor? CreateCursorFor(
        int databaseIndex,
        IcalinguaMessageSearchCursor? cursor)
    {
        if (cursor is null)
            return null;

        var decoded = _idMapper.Decode(cursor.MessageId);
        if (decoded.DatabaseIndex != databaseIndex)
        {
            return new IcalinguaMessageSearchCursor(
                cursor.MessageSortTime + 1,
                0);
        }

        return new IcalinguaMessageSearchCursor(
            cursor.MessageSortTime,
            decoded.InnerMessageId);
    }

    private static string GetIcalinguaRoomDisplayId(long roomId)
    {
        return roomId < 0
            ? (-roomId).ToString(CultureInfo.InvariantCulture)
            : roomId.ToString(CultureInfo.InvariantCulture);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }
}
