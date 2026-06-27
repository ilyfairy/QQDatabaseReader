using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.ViewModels.MessageTab;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels;

internal sealed class QqNtSearchMetadataLoader
{
    private readonly QQDatabaseService _qqDatabaseService;
    private readonly object _messageDatabaseGate = new();
    private readonly object _groupInfoDatabaseGate = new();
    private ProfileInfoNameCache? _profileInfoNames;

    public QqNtSearchMetadataLoader(QQDatabaseService qqDatabaseService)
    {
        _qqDatabaseService = qqDatabaseService;
    }

    public IReadOnlyList<long> LoadPrivateConversationIdsByPeerUin(uint peerUin)
    {
        var messageDatabase = _qqDatabaseService.MessageDatabase;
        var androidMessageDatabase = _qqDatabaseService.AndroidMessageDatabase;
        if (peerUin == 0 || messageDatabase is null && androidMessageDatabase is null)
            return [];

        var result = new HashSet<long>();
        lock (_messageDatabaseGate)
        {
            if (messageDatabase is not null)
                AddPrivateConversationIdsByPeerUin(messageDatabase.DbContext.PrivateMessages, peerUin, result);

            if (androidMessageDatabase is not null)
                AddPrivateConversationIdsByPeerUin(androidMessageDatabase.DbContext.PrivateMessages, peerUin, result);
        }

        return result.ToArray();
    }

    public Dictionary<uint, string> LoadGroupNames(IReadOnlyCollection<uint> groupIds)
    {
        var messageDatabase = _qqDatabaseService.MessageDatabase;
        var androidMessageDatabase = _qqDatabaseService.AndroidMessageDatabase;
        var groupInfoDatabase = _qqDatabaseService.GroupInfoDatabase;
        var names = new Dictionary<uint, string>();
        if (groupIds.Count == 0)
            return names;

        if (messageDatabase is not null)
            AddRecentGroupNames(messageDatabase.DbContext.RecentContacts, groupIds, names);

        if (androidMessageDatabase is not null)
            AddAndroidRecentGroupNames(androidMessageDatabase.DbContext.RecentContacts, groupIds, names);

        if (groupInfoDatabase is not null)
        {
            var groupList = groupIds.ToArray();
            lock (_groupInfoDatabaseGate)
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
            }
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

        var result = new Dictionary<long, PrivateConversationInfo>();
        lock (_messageDatabaseGate)
        {
            if (messageDatabase is not null)
                AddPrivateConversationInfos(messageDatabase.DbContext.PrivateMessages, conversationIds, result);

            if (androidMessageDatabase is not null)
                AddPrivateConversationInfos(androidMessageDatabase.DbContext.PrivateMessages, conversationIds, result);
        }

        return result;
    }

    public Dictionary<SearchMessageKey, SearchSenderInfo> LoadSenderInfos(
        IReadOnlyCollection<AvaGroupMessageSearchResult> results)
    {
        var messageDatabase = _qqDatabaseService.MessageDatabase;
        if (messageDatabase is null || results.Count == 0)
            return new Dictionary<SearchMessageKey, SearchSenderInfo>();

        var senderInfos = new Dictionary<SearchMessageKey, SearchSenderInfo>();
        lock (_messageDatabaseGate)
        {
            AddGroupSenderInfos(messageDatabase, results, senderInfos);
            AddPrivateSenderInfos(messageDatabase, results, senderInfos);
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
        lock (_messageDatabaseGate)
        {
            var recentGroups = recentContacts
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
    }

    private void AddAndroidRecentGroupNames(
        IQueryable<AndroidRecentContact> recentContacts,
        IReadOnlyCollection<uint> groupIds,
        Dictionary<uint, string> names)
    {
        var groupIdTexts = groupIds.Select(groupId => groupId.ToString()).ToArray();
        lock (_messageDatabaseGate)
        {
            var recentGroups = recentContacts
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
                            message.SendNickName,
                            ResolveProfileDisplayName(message.SenderId, message.SenderUid, string.Empty))));
            }
        }
    }

    private static void AddPrivateConversationIdsByPeerUin(
        IQueryable<PrivateMessage> messages,
        uint peerUin,
        HashSet<long> result)
    {
        var conversationIds = messages
            .Where(message => message.PeerUin == peerUin)
            .Where(message => message.ConversationId != 0)
            .Select(message => message.ConversationId)
            .Distinct()
            .ToArray();

        foreach (var conversationId in conversationIds)
        {
            result.Add(conversationId);
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
                var name = ResolveProfileDisplayName(row.PeerUin, row.PeerUid, string.Empty);
                result[row.ConversationId] = new PrivateConversationInfo(
                    row.PeerUin,
                    row.PeerUid,
                    name);
            }
        }
    }

    private string ResolveProfileDisplayName(uint uin, string? ntUid, string fallback)
    {
        var cache = GetProfileInfoNameCache();
        return cache.TryGetName(uin, ntUid, out var name)
            ? name
            : fallback;
    }

    private ProfileInfoNameCache GetProfileInfoNameCache()
    {
        var database = _qqDatabaseService.ProfileInfoDatabase;
        if (_profileInfoNames is { } cache &&
            ReferenceEquals(cache.Database, database))
        {
            return cache;
        }

        _profileInfoNames = ProfileInfoNameCache.Create(database);
        return _profileInfoNames;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }
}
