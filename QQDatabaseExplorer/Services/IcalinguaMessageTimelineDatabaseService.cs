using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.Services;

internal sealed class IcalinguaMessageTimelineDatabaseService
{
    private readonly IReadOnlyList<IcalinguaMessageDatabaseEntry> _databases;
    private readonly IcalinguaExternalMessageIdMapper _idMapper;

    public IcalinguaMessageTimelineDatabaseService(
        IReadOnlyList<IcalinguaMessageDatabaseEntry> databases,
        IcalinguaExternalMessageIdMapper idMapper)
    {
        _databases = databases;
        _idMapper = idMapper;
    }

    public IReadOnlyList<IcalinguaMessageRecord> LoadLatestMessages(
        long roomId,
        int pageSize,
        MessageQueryFilter? filter = null)
    {
        return IcalinguaMessageMerger.MergeLatest(
            QueryEach(database => database.Reader.LoadLatestMessages(roomId, pageSize, filter)),
            pageSize);
    }

    public IReadOnlyList<IcalinguaMessageRecord> LoadEarliestMessages(
        long roomId,
        int pageSize,
        MessageQueryFilter? filter = null)
    {
        return IcalinguaMessageMerger.Merge(
            QueryEach(database => database.Reader.LoadEarliestMessages(roomId, pageSize, filter)),
            descending: false,
            pageSize);
    }

    public IReadOnlyList<IcalinguaMessageRecord> LoadOlderMessages(
        long roomId,
        long messageSortTime,
        long messageId,
        int pageSize,
        MessageQueryFilter? filter = null)
    {
        var cursor = _idMapper.Decode(messageId);
        var queryPageSize = GetPerDatabasePageSize(pageSize);
        return IcalinguaMessageMerger.MergeOlder(
            QueryEach(database => database.Reader.LoadOlderMessages(
                    roomId,
                    cursor.DatabaseIndex == database.Index ? messageSortTime : messageSortTime + 1,
                    cursor.DatabaseIndex == database.Index ? cursor.InnerMessageId : 0,
                    queryPageSize,
                    filter))
                .Where(message => IcalinguaMessageMerger.IsBeforeCursor(message, messageSortTime, messageId)),
            pageSize);
    }

    public IReadOnlyList<IcalinguaMessageRecord> LoadNewerMessages(
        long roomId,
        long messageSortTime,
        long messageId,
        int pageSize,
        MessageQueryFilter? filter = null)
    {
        var cursor = _idMapper.Decode(messageId);
        var queryPageSize = GetPerDatabasePageSize(pageSize);
        return IcalinguaMessageMerger.Merge(
            QueryEach(database => database.Reader.LoadNewerMessages(
                    roomId,
                    cursor.DatabaseIndex == database.Index ? messageSortTime : Math.Max(0, messageSortTime - 1),
                    cursor.DatabaseIndex == database.Index ? cursor.InnerMessageId : 0,
                    queryPageSize,
                    filter))
                .Where(message => IcalinguaMessageMerger.IsAfterCursor(message, messageSortTime, messageId)),
            descending: false,
            pageSize);
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

    private int GetPerDatabasePageSize(int pageSize)
    {
        return Math.Max(1, pageSize * Math.Max(1, _databases.Count));
    }
}
