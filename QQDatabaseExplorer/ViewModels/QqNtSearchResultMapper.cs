using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels;

internal static class QqNtSearchResultMapper
{
    public static AvaGroupMessageSearchResult CreateResult(
        GroupMessageFtsSearchResult result,
        IReadOnlyDictionary<uint, string> groupNames,
        IReadOnlyDictionary<long, PrivateConversationInfo> privateConversationInfos)
    {
        var groupId = QqNtSearchMetadataLoader.GetSearchResultGroupId(result);
        var privateConversationId = result.ChatType == ChatType.PrivateMessage ? result.PrivateConversationId : 0;
        groupNames.TryGetValue(groupId, out var groupName);
        privateConversationInfos.TryGetValue(privateConversationId, out var privateInfo);
        return new AvaGroupMessageSearchResult
        {
            ConversationType = privateConversationId == 0 ? AvaConversationType.Group : AvaConversationType.Private,
            MessageId = result.MessageId,
            MessageSeq = result.MessageSeq,
            MessageTime = result.MessageTime,
            GroupId = groupId,
            PrivateConversationId = privateConversationId,
            PeerUin = privateInfo?.PeerUin ?? 0,
            GroupName = FirstNonEmpty(groupName, privateInfo?.Name),
            PeerUid = FirstNonEmpty(result.PeerUid, privateInfo?.PeerUid),
            SenderUid = result.SenderUid,
            PreviewText = SearchResultPreviewText.Normalize(result.PreviewText),
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }
}
