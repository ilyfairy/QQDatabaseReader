using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels;

internal sealed class QqNtSearchMetadataLoader
{
    private readonly QQDatabaseService _qqDatabaseService;
    private readonly Func<uint, string?, string, string> _resolveProfileDisplayName;

    public QqNtSearchMetadataLoader(
        QQDatabaseService qqDatabaseService,
        Func<uint, string?, string, string> resolveProfileDisplayName)
    {
        _qqDatabaseService = qqDatabaseService;
        _resolveProfileDisplayName = resolveProfileDisplayName;
    }

    public PrivateConversationSearchIdentity LoadPrivateConversationIdentityByPeerUin(uint peerUin)
    {
        var messageDatabase = _qqDatabaseService.MessageDatabase;
        var androidMessageDatabase = _qqDatabaseService.AndroidMessageDatabase;
        if (peerUin == 0)
            return new PrivateConversationSearchIdentity(null, []);

        return _qqDatabaseService.QQNtDatabaseQueryGate.Run(() =>
        {
            var peerIdentity = LoadPeerIdentity(peerUin);

            var result = new HashSet<long>();
            if (messageDatabase is not null)
            {
                AddPrivateConversationIdsByRecentContact(
                    messageDatabase.DbContext.RecentContacts,
                    messageDatabase.DbContext.PrivateMessages,
                    peerIdentity.CandidateUids,
                    result);
            }

            if (androidMessageDatabase is not null)
            {
                AddPrivateConversationIdsByRecentContact(
                    androidMessageDatabase.DbContext.RecentContacts,
                    androidMessageDatabase.DbContext.PrivateMessages,
                    peerIdentity.CandidateUids,
                    result);
            }

            return new PrivateConversationSearchIdentity(peerIdentity.PeerUid, result.ToArray());
        });
    }

    public Dictionary<uint, string> LoadGroupNames(IReadOnlyCollection<uint> groupIds)
    {
        var messageDatabase = _qqDatabaseService.MessageDatabase;
        var androidMessageDatabase = _qqDatabaseService.AndroidMessageDatabase;
        var groupInfoDatabase = _qqDatabaseService.GroupInfoDatabase;
        var names = new Dictionary<uint, string>();
        if (groupIds.Count == 0)
            return names;

        if (messageDatabase is not null || androidMessageDatabase is not null)
        {
            _qqDatabaseService.QQNtDatabaseQueryGate.Run(() =>
            {
                if (messageDatabase is not null)
                    AddRecentGroupNames(messageDatabase.DbContext.RecentContacts, groupIds, names);

                if (androidMessageDatabase is not null)
                    AddAndroidRecentGroupNames(androidMessageDatabase.DbContext.RecentContacts, groupIds, names);
            });
        }

        if (groupInfoDatabase is not null)
        {
            var groupList = groupIds.ToArray();
            _qqDatabaseService.QQNtDatabaseQueryGate.Run(() =>
            {
                var groupInfos = groupInfoDatabase.DbContext.GroupList
                    .Where(group => groupList.Contains(group.GroupId))
                    .Select(group => new
                    {
                        group.GroupId,
                        group.GroupName,
                    })
                    .ToList();

                foreach (var group in groupInfos)
                {
                    if (!string.IsNullOrWhiteSpace(group.GroupName))
                        names[group.GroupId] = group.GroupName;
                }
            });
        }

        return names;
    }

    public Dictionary<long, PrivateConversationInfo> LoadPrivateConversationInfos(
        IReadOnlyCollection<GroupMessageFtsSearchResult> results)
    {
        var conversationIds = results
            .Where(static result => result.ChatType == ChatType.PrivateMessage)
            .Select(static result => result.PrivateConversationId)
            .Where(static conversationId => conversationId != 0)
            .Distinct()
            .ToArray();
        return LoadPrivateConversationInfos(conversationIds);
    }

    public Dictionary<long, PrivateConversationInfo> LoadPrivateConversationInfos(
        IReadOnlyCollection<long> conversationIds)
    {
        var messageDatabase = _qqDatabaseService.MessageDatabase;
        var androidMessageDatabase = _qqDatabaseService.AndroidMessageDatabase;
        if (conversationIds.Count == 0 || messageDatabase is null && androidMessageDatabase is null)
            return new Dictionary<long, PrivateConversationInfo>();

        var result = _qqDatabaseService.QQNtDatabaseQueryGate.Run(() =>
        {
            var loaded = new Dictionary<long, PrivateConversationInfo>();
            if (messageDatabase is not null)
                AddPrivateConversationInfos(messageDatabase.DbContext.PrivateMessages, conversationIds, loaded);

            if (androidMessageDatabase is not null)
                AddPrivateConversationInfos(androidMessageDatabase.DbContext.PrivateMessages, conversationIds, loaded);

            return loaded;
        });

        foreach (var (conversationId, info) in result.ToArray())
        {
            result[conversationId] = info with
            {
                Name = ResolveProfileDisplayName(info.PeerUin, info.PeerUid, string.Empty),
            };
        }

        return result;
    }

    public Dictionary<SearchMessageKey, SearchSenderInfo> LoadSenderInfos(
        IReadOnlyCollection<AvaGroupMessageSearchResult> results)
    {
        var messageDatabase = _qqDatabaseService.MessageDatabase;
        if (messageDatabase is null || results.Count == 0)
            return new Dictionary<SearchMessageKey, SearchSenderInfo>();

        var senderInfos = _qqDatabaseService.QQNtDatabaseQueryGate.Run(() =>
        {
            var loaded = new Dictionary<SearchMessageKey, SearchSenderInfo>();
            AddGroupSenderInfos(messageDatabase, results, loaded);
            AddPrivateSenderInfos(messageDatabase, results, loaded);

            return loaded;
        });

        foreach (var result in results.Where(static item => item.ConversationType == AvaConversationType.Private))
        {
            var key = new SearchMessageKey(
                AvaConversationType.Private,
                0,
                result.PrivateConversationId,
                result.MessageSeq);
            if (!senderInfos.TryGetValue(key, out var senderInfo) ||
                !string.IsNullOrWhiteSpace(senderInfo.Name))
            {
                continue;
            }

            senderInfos[key] = senderInfo with
            {
                Name = ResolveProfileDisplayName(senderInfo.SenderId, result.SenderUid, string.Empty),
            };
        }

        return senderInfos;
    }

    public static uint GetSearchResultGroupId(GroupMessageFtsSearchResult result)
    {
        if (result.ChatType != ChatType.GroupMessage)
            return 0;

        if (uint.TryParse(result.PeerUid, out var peerGroupId) && peerGroupId != 0)
            return peerGroupId;

        return result.GroupId;
    }

    private void AddRecentGroupNames(
        IQueryable<RecentContact> recentContacts,
        IReadOnlyCollection<uint> groupIds,
        Dictionary<uint, string> names)
    {
        var groupIdTexts = groupIds.Select(groupId => groupId.ToString()).ToArray();
        var recentGroups = recentContacts
            .Where(contact => contact.RecentCategory == (int)ChatType.GroupMessage)
            .Where(contact => contact.ChatType == ChatType.GroupMessage)
            .Where(contact => contact.PeerUin != null && groupIdTexts.Contains(contact.PeerUin))
            .Select(contact => new
            {
                contact.PeerUin,
                contact.Source,
            })
            .ToList();

        AddRecentGroupNames(recentGroups.Select(group => (group.PeerUin, group.Source)), names);
    }

    private void AddAndroidRecentGroupNames(
        IQueryable<AndroidRecentContact> recentContacts,
        IReadOnlyCollection<uint> groupIds,
        Dictionary<uint, string> names)
    {
        var groupIdTexts = groupIds.Select(groupId => groupId.ToString()).ToArray();
        var recentGroups = recentContacts
            .Where(contact => contact.RecentCategory == (int)ChatType.GroupMessage)
            .Where(contact => contact.ChatType == ChatType.GroupMessage)
            .Where(contact => contact.PeerUin != null && groupIdTexts.Contains(contact.PeerUin))
            .Select(contact => new
            {
                contact.PeerUin,
                contact.Source,
            })
            .ToList();

        AddRecentGroupNames(recentGroups.Select(group => (group.PeerUin, group.Source)), names);
    }

    private static void AddRecentGroupNames(
        IEnumerable<(string? PeerUin, string? Source)> recentGroups,
        Dictionary<uint, string> names)
    {
        foreach (var group in recentGroups)
        {
            if (!uint.TryParse(group.PeerUin, out var recentGroupId) ||
                string.IsNullOrWhiteSpace(group.Source) ||
                names.ContainsKey(recentGroupId))
            {
                continue;
            }

            names[recentGroupId] = group.Source;
        }
    }

    private PrivatePeerIdentity LoadPeerIdentity(uint peerUin)
    {
        var peerUids = new HashSet<string>(StringComparer.Ordinal)
        {
            peerUin.ToString(),
        };
        // profile_info_v6, 旧版 QQNT 是 profile_info_v2
        if (_qqDatabaseService.ProfileInfoDatabase is not { ProfileInfoTableName: { } profileTable } profileInfoDatabase)
            return new PrivatePeerIdentity(null, peerUids.ToArray());

        var connection = profileInfoDatabase.DbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT [1000] FROM \"{profileTable}\" WHERE [1002] = $uin";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$uin";
        parameter.Value = (long)peerUin;
        command.Parameters.Add(parameter);
        var peerUid = command.ExecuteScalar() as string;
        if (!string.IsNullOrWhiteSpace(peerUid))
            peerUids.Add(peerUid);

        return new PrivatePeerIdentity(peerUid, peerUids.ToArray());
    }

    private static void AddPrivateConversationIdsByRecentContact(
        IQueryable<RecentContact> recentContacts,
        IQueryable<PrivateMessage> messages,
        IReadOnlyCollection<string> peerUids,
        HashSet<long> result)
    {
        var lastMessageIds = recentContacts
            .Where(contact => contact.RecentCategory == (int)ChatType.PrivateMessage)
            .Where(contact => contact.ChatType == ChatType.PrivateMessage)
            .Where(contact => contact.PeerUin != null && peerUids.Contains(contact.PeerUin))
            .Where(contact => contact.LastMessageId != 0)
            .Select(contact => contact.LastMessageId)
            .Distinct()
            .ToArray();
        AddPrivateConversationIdsByMessageId(messages, lastMessageIds, peerUids, result);
    }

    private static void AddPrivateConversationIdsByRecentContact(
        IQueryable<AndroidRecentContact> recentContacts,
        IQueryable<PrivateMessage> messages,
        IReadOnlyCollection<string> peerUids,
        HashSet<long> result)
    {
        var lastMessageIds = recentContacts
            .Where(contact => contact.RecentCategory == (int)ChatType.PrivateMessage)
            .Where(contact => contact.ChatType == ChatType.PrivateMessage)
            .Where(contact => contact.PeerUin != null && peerUids.Contains(contact.PeerUin))
            .Where(contact => contact.LastMessageId != 0)
            .Select(contact => contact.LastMessageId)
            .Distinct()
            .ToArray();
        AddPrivateConversationIdsByMessageId(messages, lastMessageIds, peerUids, result);
    }

    private static void AddPrivateConversationIdsByMessageId(
        IQueryable<PrivateMessage> messages,
        IReadOnlyCollection<long> messageIds,
        IReadOnlyCollection<string> peerUids,
        HashSet<long> result)
    {
        foreach (var messageIdBatch in messageIds.Chunk(500))
        {
            var batch = messageIdBatch.ToArray();
            var conversationIds = messages
                .Where(message => batch.Contains(message.MessageId))
                .Where(message => peerUids.Contains(message.PeerUid))
                .Where(message => message.ConversationId != 0)
                .Select(message => message.ConversationId)
                .Distinct()
                .ToArray();
            foreach (var conversationId in conversationIds)
            {
                result.Add(conversationId);
            }
        }
    }

    private static void AddGroupSenderInfos(
        QQMessageReader messageDatabase,
        IReadOnlyCollection<AvaGroupMessageSearchResult> results,
        Dictionary<SearchMessageKey, SearchSenderInfo> senderInfos)
    {
        var groupIds = results
            .Where(static result => result.ConversationType == AvaConversationType.Group)
            .Select(static result => result.GroupId)
            .Where(static groupId => groupId != 0)
            .Distinct()
            .ToArray();
        var messageSeqs = results
            .Where(static result => result.ConversationType == AvaConversationType.Group)
            .Select(static result => result.MessageSeq)
            .Where(static messageSeq => messageSeq > 0)
            .Distinct()
            .ToArray();

        if (groupIds.Length == 0 || messageSeqs.Length == 0)
            return;

        foreach (var messageSeqBatch in messageSeqs.Chunk(500))
        {
            var messages = messageDatabase.DbContext.GroupMessages
                .Where(message => groupIds.Contains(message.GroupId))
                .Where(message => messageSeqBatch.Contains(message.MessageSeq))
                .Select(message => new
                {
                    message.GroupId,
                    message.MessageSeq,
                    message.SenderId,
                    message.SendMemberName,
                    message.SendNickName,
                })
                .ToList();

            foreach (var message in messages)
            {
                var key = new SearchMessageKey(AvaConversationType.Group, message.GroupId, 0, message.MessageSeq);
                senderInfos.TryAdd(
                    key,
                    new SearchSenderInfo(
                        message.SenderId,
                        FirstNonEmpty(message.SendMemberName, message.SendNickName)));
            }
        }
    }

    private void AddPrivateSenderInfos(
        QQMessageReader messageDatabase,
        IReadOnlyCollection<AvaGroupMessageSearchResult> results,
        Dictionary<SearchMessageKey, SearchSenderInfo> senderInfos)
    {
        var conversationIds = results
            .Where(static result => result.ConversationType == AvaConversationType.Private)
            .Select(static result => result.PrivateConversationId)
            .Where(static conversationId => conversationId != 0)
            .Distinct()
            .ToArray();
        var messageSeqs = results
            .Where(static result => result.ConversationType == AvaConversationType.Private)
            .Select(static result => result.MessageSeq)
            .Where(static messageSeq => messageSeq > 0)
            .Distinct()
            .ToArray();

        if (conversationIds.Length == 0 || messageSeqs.Length == 0)
            return;

        foreach (var messageSeqBatch in messageSeqs.Chunk(500))
        {
            var messages = messageDatabase.DbContext.PrivateMessages
                .Where(message => conversationIds.Contains(message.ConversationId))
                .Where(message => messageSeqBatch.Contains(message.MessageSeq))
                .Select(message => new
                {
                    message.ConversationId,
                    message.MessageSeq,
                    message.SenderId,
                    message.SenderUid,
                    message.SendMemberName,
                    message.SendNickName,
                })
                .ToList();

            foreach (var message in messages)
            {
                var key = new SearchMessageKey(AvaConversationType.Private, 0, message.ConversationId, message.MessageSeq);
                senderInfos.TryAdd(
                    key,
                    new SearchSenderInfo(
                        message.SenderId,
                        FirstNonEmpty(
                            message.SendMemberName,
                            message.SendNickName)));
            }
        }
    }

    private void AddPrivateConversationInfos(
        IQueryable<PrivateMessage> messages,
        IReadOnlyCollection<long> conversationIds,
        Dictionary<long, PrivateConversationInfo> result)
    {
        foreach (var conversationIdBatch in conversationIds.Chunk(500))
        {
            var batch = conversationIdBatch.ToArray();
            var rows = messages
                .Where(message => batch.Contains(message.ConversationId))
                .GroupBy(message => message.ConversationId)
                .Select(group => new
                {
                    ConversationId = group.Key,
                    PeerUin = group.Max(message => message.PeerUin),
                    PeerUid = group.Max(message => message.PeerUid),
                    Name = string.Empty,
                })
                .ToList();

            foreach (var row in rows)
            {
                result[row.ConversationId] = new PrivateConversationInfo(
                    row.PeerUin,
                    row.PeerUid,
                    string.Empty);
            }
        }
    }

    private string ResolveProfileDisplayName(uint uin, string? ntUid, string fallback)
    {
        return _resolveProfileDisplayName(uin, ntUid, fallback);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }

    private sealed record PrivatePeerIdentity(
        string? PeerUid,
        IReadOnlyList<string> CandidateUids);
}
