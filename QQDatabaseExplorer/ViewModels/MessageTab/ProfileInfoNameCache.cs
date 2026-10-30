using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using QQDatabaseExplorer.Services;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class ProfileInfoNameCache
{
    internal const int MaxProfileEntries = 8192;
    internal const int MaxNtUidEntries = 8192;

    private readonly QQNtDatabaseQueryGate _queryGate;
    private readonly object _cacheGate = new();
    private readonly Dictionary<uint, ProfileLookup> _profilesByUin = [];
    private readonly Dictionary<string, uint> _uinsByNtUid = new(StringComparer.Ordinal);
    private readonly Queue<uint> _profileOrder = [];
    private readonly Queue<string> _ntUidOrder = [];

    private ProfileInfoNameCache(QQProfileInfoReader? database, QQNtDatabaseQueryGate queryGate)
    {
        Database = database;
        _queryGate = queryGate;
    }

    public QQProfileInfoReader? Database { get; }

    public static ProfileInfoNameCache Create(
        QQProfileInfoReader? database,
        QQNtDatabaseQueryGate queryGate)
    {
        return new ProfileInfoNameCache(database, queryGate);
    }

    public bool TryGetName(uint uin, string? ntUid, out string name)
    {
        var resolvedUin = uin;
        if (resolvedUin == 0 && !TryGetUin(ntUid, out resolvedUin))
        {
            name = string.Empty;
            return false;
        }

        var profile = GetProfile(resolvedUin);
        if (!string.IsNullOrWhiteSpace(profile.NtUid))
            CacheNtUid(profile.NtUid, resolvedUin);

        name = profile.Name;
        return !string.IsNullOrWhiteSpace(name);
    }

    public bool TryGetUin(string? ntUid, out uint uin)
    {
        if (string.IsNullOrWhiteSpace(ntUid) || Database is null)
        {
            uin = 0;
            return false;
        }

        lock (_cacheGate)
        {
            if (_uinsByNtUid.TryGetValue(ntUid, out uin))
                return uin != 0;
        }

        uin = LoadUinByNtUid(ntUid);
        uin = CacheNtUid(ntUid, uin);
        return uin != 0;
    }

    private ProfileLookup GetProfile(uint uin)
    {
        if (uin == 0 || Database is null)
            return ProfileLookup.Empty;

        lock (_cacheGate)
        {
            if (_profilesByUin.TryGetValue(uin, out var cachedProfile))
                return cachedProfile;
        }

        var profile = LoadProfile(uin);
        return CacheProfile(uin, profile);
    }

    private ProfileLookup LoadProfile(uint uin)
    {
        return _queryGate.Run(() =>
        {
            // profile_info_v6, 旧版 QQNT 是 profile_info_v2
            if (Database!.ProfileInfoTableName is not { } table)
                return ProfileLookup.Empty;

            var connection = Database.DbContext.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT [1000], [20009], [20002] FROM \"{table}\" WHERE [1002] = $uin LIMIT 1";
            AddParameter(command, "$uin", (long)uin);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return ProfileLookup.Empty;

            return new ProfileLookup(
                ReadString(reader, 0),
                FirstNonEmpty(ReadString(reader, 1), ReadString(reader, 2)));
        });
    }

    private uint LoadUinByNtUid(string ntUid)
    {
        return _queryGate.Run(() =>
        {
            var connection = Database!.DbContext.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT [1002] FROM buddy_list WHERE [1000] = $ntUid LIMIT 1";
            AddParameter(command, "$ntUid", ntUid);
            var value = command.ExecuteScalar();
            return value is null or DBNull
                ? 0
                : unchecked((uint)Convert.ToInt64(value));
        });
    }

    private ProfileLookup CacheProfile(uint uin, ProfileLookup profile)
    {
        lock (_cacheGate)
        {
            if (_profilesByUin.TryGetValue(uin, out var cachedProfile))
                return cachedProfile;

            _profilesByUin.Add(uin, profile);
            _profileOrder.Enqueue(uin);
            while (_profilesByUin.Count > MaxProfileEntries &&
                   _profileOrder.TryDequeue(out var oldestUin))
            {
                _profilesByUin.Remove(oldestUin);
            }

            return profile;
        }
    }

    private uint CacheNtUid(string ntUid, uint uin)
    {
        lock (_cacheGate)
        {
            if (_uinsByNtUid.TryGetValue(ntUid, out var cachedUin))
            {
                if (cachedUin == 0 && uin != 0)
                    _uinsByNtUid[ntUid] = uin;

                return cachedUin == 0 ? uin : cachedUin;
            }

            _uinsByNtUid.Add(ntUid, uin);
            _ntUidOrder.Enqueue(ntUid);
            while (_uinsByNtUid.Count > MaxNtUidEntries &&
                   _ntUidOrder.TryDequeue(out var oldestNtUid))
            {
                _uinsByNtUid.Remove(oldestNtUid);
            }

            return uin;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string? ReadString(System.Data.Common.DbDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private sealed record ProfileLookup(string? NtUid, string Name)
    {
        public static ProfileLookup Empty { get; } = new(null, string.Empty);
    }
}
