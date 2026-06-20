using System;
using System.Linq;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class SystemHintParticipantResolver(
    Func<QQGroupInfoReader?> getGroupInfoDatabase,
    Func<ProfileInfoNameCache> getProfileInfoNameCache)
{
    public string ResolveParticipantName(
        uint groupId,
        string? ntUid,
        string? uin,
        params string?[] fallbacks)
    {
        var fallback = FirstNonEmpty(fallbacks.Select(QqNtSystemHintDisplayFactory.CleanText).ToArray());
        var resolvedUin = QqNtSystemHintDisplayFactory.ParseUin(uin);

        if (TryResolveGroupMemberName(groupId, ntUid, resolvedUin, out var groupMemberName))
            return groupMemberName;

        return getProfileInfoNameCache().TryGetName(resolvedUin, ntUid, out var profileName)
            ? profileName
            : fallback;
    }

    public string ResolveSourceUin(uint groupId, string? ntUid)
    {
        if (string.IsNullOrWhiteSpace(ntUid))
            return string.Empty;

        if (TryResolveGroupMemberUin(groupId, ntUid, out var groupMemberUin))
            return groupMemberUin.ToString();

        return getProfileInfoNameCache().TryGetUin(ntUid, out var profileUin)
            ? profileUin.ToString()
            : string.Empty;
    }

    private bool TryResolveGroupMemberName(
        uint groupId,
        string? ntUid,
        uint resolvedUin,
        out string name)
    {
        name = string.Empty;
        if (getGroupInfoDatabase() is not { } groupInfoDatabase)
            return false;

        try
        {
            var query = groupInfoDatabase.DbContext.GroupMembers
                .Where(member => member.Uin != 0 || !string.IsNullOrEmpty(member.NtUid));

            if (!string.IsNullOrWhiteSpace(ntUid))
            {
                query = query.Where(member => member.NtUid == ntUid);
            }
            else if (resolvedUin != 0)
            {
                query = query.Where(member => member.Uin == resolvedUin);
            }
            else
            {
                return false;
            }

            if (groupId != 0)
            {
                var groupName = query
                    .Where(member => member.GroupId == groupId)
                    .Select(member => new { member.MemberName, member.NickName })
                    .FirstOrDefault();
                name = FirstNonEmpty(groupName?.MemberName, groupName?.NickName);
                if (!string.IsNullOrWhiteSpace(name))
                    return true;
            }

            var match = query
                .Select(member => new { member.MemberName, member.NickName })
                .FirstOrDefault();
            name = FirstNonEmpty(match?.MemberName, match?.NickName);
            return !string.IsNullOrWhiteSpace(name);
        }
        catch
        {
            return false;
        }
    }

    private bool TryResolveGroupMemberUin(uint groupId, string ntUid, out uint uin)
    {
        uin = 0;
        if (getGroupInfoDatabase() is not { } groupInfoDatabase)
            return false;

        try
        {
            var query = groupInfoDatabase.DbContext.GroupMembers
                .Where(member => member.NtUid == ntUid)
                .Where(member => member.Uin != 0);

            if (groupId != 0)
            {
                uin = query
                    .Where(member => member.GroupId == groupId)
                    .Select(member => member.Uin)
                    .FirstOrDefault();
                if (uin != 0)
                    return true;
            }

            var matches = query
                .Select(member => member.Uin)
                .Distinct()
                .Take(2)
                .ToArray();
            if (matches.Length != 1)
                return false;

            uin = matches[0];
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}
