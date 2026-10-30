using System;
using System.Linq;
using QQDatabaseExplorer.Services;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class SystemHintParticipantResolver(
    Func<QQGroupInfoReader?> getGroupInfoDatabase,
    Func<ProfileInfoNameCache> getProfileInfoNameCache,
    QQNtDatabaseQueryGate queryGate)
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
        if (groupId == 0 || getGroupInfoDatabase() is not { } groupInfoDatabase)
            return false;

        try
        {
            name = queryGate.Run(() =>
            {
                var query = groupInfoDatabase.DbContext.GroupMembers
                    .Where(member => member.GroupId == groupId)
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
                    return string.Empty;
                }

                var match = query
                    .Select(member => new { member.MemberName, member.NickName })
                    .FirstOrDefault();
                return FirstNonEmpty(match?.MemberName, match?.NickName);
            });
        }
        catch
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(name);
    }

    private bool TryResolveGroupMemberUin(uint groupId, string ntUid, out uint uin)
    {
        uin = 0;
        if (groupId == 0 || getGroupInfoDatabase() is not { } groupInfoDatabase)
            return false;

        try
        {
            uin = queryGate.Run(() => groupInfoDatabase.DbContext.GroupMembers
                .Where(member => member.GroupId == groupId)
                .Where(member => member.NtUid == ntUid)
                .Where(member => member.Uin != 0)
                .Select(member => member.Uin)
                .FirstOrDefault());
        }
        catch
        {
            return false;
        }

        return uin != 0;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}
