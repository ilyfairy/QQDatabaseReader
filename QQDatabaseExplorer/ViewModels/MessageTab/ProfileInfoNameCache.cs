using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class ProfileInfoNameCache
{
    private static readonly IReadOnlyDictionary<uint, string> EmptyUinNames =
        new Dictionary<uint, string>();
    private static readonly IReadOnlyDictionary<string, string> EmptyNtUidNames =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, uint> EmptyUinsByNtUid =
        new Dictionary<string, uint>(StringComparer.Ordinal);
    private static readonly ProfileInfoNameCache Empty = new(null, EmptyUinNames, EmptyNtUidNames, EmptyUinsByNtUid);

    private readonly IReadOnlyDictionary<uint, string> _namesByUin;
    private readonly IReadOnlyDictionary<string, string> _namesByNtUid;
    private readonly IReadOnlyDictionary<string, uint> _uinsByNtUid;

    private ProfileInfoNameCache(
        QQProfileInfoReader? database,
        IReadOnlyDictionary<uint, string> namesByUin,
        IReadOnlyDictionary<string, string> namesByNtUid,
        IReadOnlyDictionary<string, uint> uinsByNtUid)
    {
        Database = database;
        _namesByUin = namesByUin;
        _namesByNtUid = namesByNtUid;
        _uinsByNtUid = uinsByNtUid;
    }

    public QQProfileInfoReader? Database { get; }

    public static ProfileInfoNameCache Create(QQProfileInfoReader? database)
    {
        if (database is null)
            return Empty;

        var namesByUin = new Dictionary<uint, string>();
        var namesByNtUid = new Dictionary<string, string>(StringComparer.Ordinal);
        var uinsByNtUid = new Dictionary<string, uint>(StringComparer.Ordinal);

        try
        {
            foreach (var profile in database.DbContext.ProfileInfo
                         .Select(profile => new
                         {
                             profile.Uin,
                             profile.NtUid,
                             profile.NickName,
                             profile.RemarkName,
                         })
                         .ToList())
            {
                CacheProfileUin(uinsByNtUid, profile.Uin, profile.NtUid);
                CacheProfileName(namesByUin, namesByNtUid, profile.Uin, profile.NtUid, profile.RemarkName, profile.NickName);
            }

            foreach (var buddy in database.DbContext.BuddyList
                         .Select(buddy => new
                         {
                             buddy.Uin,
                             buddy.NtUid,
                         })
                         .ToList())
            {
                if (buddy.Uin != 0 &&
                    !string.IsNullOrWhiteSpace(buddy.NtUid))
                {
                    CacheProfileUin(uinsByNtUid, buddy.Uin, buddy.NtUid);
                    if (namesByUin.TryGetValue(buddy.Uin, out var name) &&
                        !namesByNtUid.ContainsKey(buddy.NtUid))
                    {
                        namesByNtUid[buddy.NtUid] = name;
                    }
                }
            }
        }
        catch
        {
            return new ProfileInfoNameCache(database, EmptyUinNames, EmptyNtUidNames, EmptyUinsByNtUid);
        }

        return new ProfileInfoNameCache(database, namesByUin, namesByNtUid, uinsByNtUid);
    }

    public bool TryGetName(uint uin, string? ntUid, out string name)
    {
        if (uin != 0 &&
            _namesByUin.TryGetValue(uin, out name!) &&
            !string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(ntUid) &&
            _namesByNtUid.TryGetValue(ntUid, out name!) &&
            !string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        name = string.Empty;
        return false;
    }

    public bool TryGetUin(string? ntUid, out uint uin)
    {
        if (!string.IsNullOrWhiteSpace(ntUid) &&
            _uinsByNtUid.TryGetValue(ntUid, out uin) &&
            uin != 0)
        {
            return true;
        }

        uin = 0;
        return false;
    }

    private static void CacheProfileUin(
        IDictionary<string, uint> uinsByNtUid,
        uint uin,
        string? ntUid)
    {
        if (uin != 0 && !string.IsNullOrWhiteSpace(ntUid))
        {
            uinsByNtUid[ntUid] = uin;
        }
    }

    private static void CacheProfileName(
        IDictionary<uint, string> namesByUin,
        IDictionary<string, string> namesByNtUid,
        uint uin,
        string? ntUid,
        string? remarkName,
        string? nickName)
    {
        var name = FirstNonEmpty(remarkName, nickName);
        if (string.IsNullOrWhiteSpace(name))
            return;

        if (uin != 0)
        {
            namesByUin[uin] = name;
        }

        if (!string.IsNullOrWhiteSpace(ntUid))
        {
            namesByNtUid[ntUid] = name;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}
