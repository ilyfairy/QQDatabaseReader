using System.Collections.Generic;

namespace QQDatabaseExplorer.Services;

internal sealed class IcalinguaExternalMessageIdMapper
{
    private const int ExternalDatabaseShift = 56;
    private const long ExternalMessageIdMask = 0x00FF_FFFF_FFFF_FFFFL;
    private readonly Dictionary<(int DatabaseIndex, long InnerMessageId), long> _externalIds = [];
    private readonly Dictionary<long, (int DatabaseIndex, long InnerMessageId)> _innerIds = [];
    private readonly object _lock = new();
    private long _nextExternalId;

    public long GetExternalMessageId(int databaseIndex, long innerMessageId)
    {
        lock (_lock)
        {
            var key = (databaseIndex, innerMessageId);
            if (_externalIds.TryGetValue(key, out var existingId))
                return existingId;

            var externalId = ((long)(databaseIndex + 1) << ExternalDatabaseShift) |
                             (innerMessageId & ExternalMessageIdMask);
            if (_innerIds.TryGetValue(externalId, out var existingInner) && existingInner != key)
                externalId = ++_nextExternalId;

            _externalIds[key] = externalId;
            _innerIds[externalId] = key;
            return externalId;
        }
    }

    public IcalinguaDecodedMessageId Decode(long messageId)
    {
        lock (_lock)
        {
            return _innerIds.TryGetValue(messageId, out var inner)
                ? new IcalinguaDecodedMessageId(inner.DatabaseIndex, inner.InnerMessageId)
                : new IcalinguaDecodedMessageId(null, messageId);
        }
    }
}

internal readonly record struct IcalinguaDecodedMessageId(
    int? DatabaseIndex,
    long InnerMessageId);
