using System;
using System.Linq;
using System.Threading.Tasks;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.ViewModels.MessageTab;

namespace QQDatabaseExplorer.ViewModels;

public partial class MessageTabViewModel
{
    public async Task<MessageCopyPayload> CreateSearchResultCopyPayloadAsync(AvaGroupMessageSearchResult result)
    {
        if (!result.CanLocate)
            return MessageCopyPayload.Empty;

        var conversation = CreateSearchResultConversation(result);
        if (conversation is null || !_databaseAvailability.HasMessageDatabase(conversation))
            return MessageCopyPayload.Empty;

        var page = await _messageDatabaseQueryRunner.RunAsync(() =>
        {
            var targetMessage = LoadSearchResultTargetMessage(conversation, result);
            return targetMessage is null
                ? null
                : _messagePageLoader.CreatePage(conversation, [targetMessage], 1);
        });
        if (page is null)
            return MessageCopyPayload.Empty;

        var messages = await _messagePageDisplayBuilder.CreateAsync(conversation, page);
        var message = messages.FirstOrDefault(message => IsSearchResultTargetMessage(message, result)) ??
                      messages.FirstOrDefault();
        return message is null
            ? MessageCopyPayload.Empty
            : MessageCopyPayload.FromMessage(message);
    }

    public async Task JumpToReplyMessageAsync(AvaQQMessage sourceMessage)
    {
        if (sourceMessage.Reply is not { } reply)
            return;

        var targetConversation = _replyTargetConversationResolver.Resolve(sourceMessage, reply);
        if (targetConversation is not null &&
            targetConversation.ConversationKey != sourceMessage.ConversationKey)
        {
            await JumpToReplyMessageInConversationAsync(targetConversation, reply);
            return;
        }

        if (LoadedReplyTargetFinder.TryFind(
                _messages,
                _conversationList.ActiveConversationKey,
                sourceMessage.ConversationKey,
                sourceMessage.ConversationType,
                reply,
                out var loadedMessage))
        {
            View?.ScrollToMessageIfNeeded(loadedMessage.MessageId);
            return;
        }

        if (IsActiveConversationKey(sourceMessage.ConversationKey) &&
            SelectedGroup?.ConversationKey == sourceMessage.ConversationKey)
        {
            await JumpToMessageInActiveConversationAsync(sourceMessage, reply);
            return;
        }

        if (targetConversation is not null)
        {
            await JumpToReplyMessageInConversationAsync(targetConversation, reply);
            return;
        }

        if (sourceMessage.ConversationType == AvaConversationType.Group)
        {
            await JumpToMessageAsync(
                sourceMessage.GroupId,
                reply.MessageId,
                reply.MessageSeq,
                SelectedGroup?.GroupName);
        }
    }

    public async Task JumpToSystemHintTargetMessageAsync(AvaQQMessage sourceMessage)
    {
        if (sourceMessage.SystemHintTargetMessageSeq <= 0 ||
            !IsActiveConversationKey(sourceMessage.ConversationKey) ||
            SelectedGroup?.ConversationKey != sourceMessage.ConversationKey ||
            !_databaseAvailability.HasQQNtMessageDatabase)
        {
            return;
        }

        var loadedMatches = _messages
            .Where(message => message.MessageSeq == sourceMessage.SystemHintTargetMessageSeq)
            .Take(2)
            .ToArray();
        if (loadedMatches.Length == 1)
        {
            View?.ScrollToMessageIfNeeded(loadedMatches[0].MessageId);
            return;
        }

        MessageRecord? targetMessage = sourceMessage.ConversationType switch
        {
            AvaConversationType.Group when sourceMessage.GroupId != 0 => _jumpTargetResolver.ResolveGroupTarget(
                sourceMessage.GroupId,
                [],
                [],
                [sourceMessage.SystemHintTargetMessageSeq]),
            AvaConversationType.Private when sourceMessage.PrivateConversationId != 0 => _jumpTargetResolver.ResolvePrivateTarget(
                sourceMessage.PrivateConversationId,
                [],
                [],
                [sourceMessage.SystemHintTargetMessageSeq]),
            _ => null,
        };

        if (targetMessage is null)
            return;

        var loadVersion = _messageLoadVersion.BeginNext();
        await LoadMessagesAroundAsync(
            SelectedGroup,
            targetMessage.MessageId,
            targetMessage.MessageSeq,
            loadVersion);
    }

    private async Task JumpToReplyMessageInConversationAsync(AvaQQGroup conversation, AvaReplyMessage reply)
    {
        if (ConversationTypeClassifier.IsIcalingua(conversation))
        {
            await JumpToIcalinguaMessageInConversationAsync(conversation, reply);
            return;
        }

        if (!_databaseAvailability.HasQQNtMessageDatabase)
        {
            return;
        }

        long messageId;
        long messageSeq;
        switch (conversation.ConversationType)
        {
            case AvaConversationType.Group:
            {
                var targetMessage = _jumpTargetResolver.ResolveGroupTarget(
                    conversation.GroupId,
                    ReplyTargetMatcher.GetReplyMessageIdCandidates(reply),
                    ReplyTargetMatcher.GetReplyMessageRandomCandidates(reply),
                    ReplyTargetMatcher.GetReplyMessageSeqCandidates(reply, conversation.ConversationType),
                    reply);
                if (targetMessage is null)
                    return;

                messageId = targetMessage.MessageId;
                messageSeq = targetMessage.MessageSeq;
                break;
            }
            case AvaConversationType.Private:
            {
                var targetMessage = _jumpTargetResolver.ResolvePrivateTarget(
                    conversation.PrivateConversationId,
                    ReplyTargetMatcher.GetReplyMessageIdCandidates(reply),
                    ReplyTargetMatcher.GetReplyMessageRandomCandidates(reply),
                    ReplyTargetMatcher.GetReplyMessageSeqCandidates(reply, conversation.ConversationType),
                    reply);
                if (targetMessage is null)
                    return;

                messageId = targetMessage.MessageId;
                messageSeq = targetMessage.MessageSeq;
                break;
            }
            default:
                return;
        }

        await JumpToConversationMessageAsync(conversation, messageId, messageSeq, clearMessageFilter: false);
    }

    private async Task JumpToMessageInActiveConversationAsync(AvaQQMessage sourceMessage, AvaReplyMessage reply)
    {
        if (sourceMessage.ConversationType == AvaConversationType.Icalingua)
        {
            if (SelectedGroup is not { } selectedGroup)
                return;

            await JumpToIcalinguaMessageInConversationAsync(selectedGroup, reply);
            return;
        }

        if (ConversationTypeClassifier.IsPCQQ(sourceMessage.ConversationType))
        {
            await JumpToPCQQMessageInActiveConversationAsync(sourceMessage, reply);
            return;
        }

        if (!_databaseAvailability.HasQQNtMessageDatabase)
            return;

        switch (sourceMessage.ConversationType)
        {
            case AvaConversationType.Group:
            {
                if (sourceMessage.GroupId == 0)
                    return;

                var targetMessage = _jumpTargetResolver.ResolveGroupTarget(
                    sourceMessage.GroupId,
                    ReplyTargetMatcher.GetReplyMessageIdCandidates(reply),
                    ReplyTargetMatcher.GetReplyMessageRandomCandidates(reply),
                    ReplyTargetMatcher.GetReplyMessageSeqCandidates(reply, sourceMessage.ConversationType),
                    reply);
                if (targetMessage is null)
                    return;

                var loadVersion = _messageLoadVersion.BeginNext();
                await LoadMessagesAroundAsync(SelectedGroup, targetMessage.MessageId, targetMessage.MessageSeq, loadVersion);
                break;
            }
            case AvaConversationType.Private:
            {
                if (sourceMessage.PrivateConversationId == 0)
                    return;

                var targetMessage = _jumpTargetResolver.ResolvePrivateTarget(
                    sourceMessage.PrivateConversationId,
                    ReplyTargetMatcher.GetReplyMessageIdCandidates(reply),
                    ReplyTargetMatcher.GetReplyMessageRandomCandidates(reply),
                    ReplyTargetMatcher.GetReplyMessageSeqCandidates(reply, sourceMessage.ConversationType),
                    reply);
                if (targetMessage is null)
                    return;

                var loadVersion = _messageLoadVersion.BeginNext();
                await LoadMessagesAroundAsync(SelectedGroup, targetMessage.MessageId, targetMessage.MessageSeq, loadVersion);
                break;
            }
        }
    }

    private async Task JumpToIcalinguaMessageInConversationAsync(AvaQQGroup conversation, AvaReplyMessage reply)
    {
        var targetMessage = _jumpTargetResolver.ResolveIcalinguaReplyTarget(conversation, reply);
        if (targetMessage is null)
            return;

        await JumpToConversationMessageAsync(
            conversation,
            targetMessage.MessageId,
            targetMessage.MessageSeq,
            clearMessageFilter: false);
    }

    private async Task JumpToPCQQMessageInActiveConversationAsync(AvaQQMessage sourceMessage, AvaReplyMessage reply)
    {
        if (SelectedGroup is not { } selectedGroup)
        {
            return;
        }

        var targetMessage = _jumpTargetResolver.ResolvePCQQReplyTarget(
            selectedGroup,
            reply,
            sourceMessage.ConversationType);
        if (targetMessage is null)
            return;

        var loadVersion = _messageLoadVersion.BeginNext();
        await LoadMessagesAroundAsync(
            selectedGroup,
            targetMessage.MessageId,
            targetMessage.MessageSeq,
            loadVersion);
    }

    public async Task JumpToMessageAsync(uint groupId, long messageId, long messageSeq, string? groupName = null)
    {
        await JumpToMessageAsync(groupId, messageId, messageSeq, groupName, clearMessageFilter: false);
    }

    public async Task JumpToMessageAndClearFilterAsync(uint groupId, long messageId, long messageSeq, string? groupName = null)
    {
        await JumpToMessageAsync(groupId, messageId, messageSeq, groupName, clearMessageFilter: true);
    }

    public async Task JumpToPrivateMessageAsync(
        long conversationId,
        long messageId,
        long messageSeq,
        string? conversationName = null,
        uint peerUin = 0,
        string? peerUid = null)
    {
        await JumpToPrivateMessageAsync(
            conversationId,
            messageId,
            messageSeq,
            conversationName,
            peerUin,
            peerUid,
            clearMessageFilter: false);
    }

    public async Task JumpToPrivateMessageAndClearFilterAsync(
        long conversationId,
        long messageId,
        long messageSeq,
        string? conversationName = null,
        uint peerUin = 0,
        string? peerUid = null)
    {
        await JumpToPrivateMessageAsync(
            conversationId,
            messageId,
            messageSeq,
            conversationName,
            peerUin,
            peerUid,
            clearMessageFilter: true);
    }

    public async Task JumpToIcalinguaMessageAsync(long roomId, long messageId, long messageSeq, string? groupName = null)
    {
        await JumpToIcalinguaMessageAsync(roomId, messageId, messageSeq, groupName, clearMessageFilter: false);
    }

    public async Task JumpToIcalinguaMessageAndClearFilterAsync(long roomId, long messageId, long messageSeq, string? groupName = null)
    {
        await JumpToIcalinguaMessageAsync(roomId, messageId, messageSeq, groupName, clearMessageFilter: true);
    }

    public async Task JumpToPCQQMessageAsync(
        AvaConversationType conversationType,
        uint groupId,
        uint privateUin,
        string? tableName,
        long messageId,
        long messageSeq,
        string? conversationName,
        bool clearMessageFilter)
    {
        if (string.IsNullOrWhiteSpace(tableName) || messageId == 0 || messageSeq <= 0)
            return;

        AvaQQGroup conversation;
        if (conversationType == AvaConversationType.PCQQGroup)
        {
            if (groupId == 0)
                return;

            conversation = _conversationApplier.GetOrCreatePCQQGroup(groupId);
        }
        else if (conversationType == AvaConversationType.PCQQPrivate)
        {
            if (privateUin == 0)
                return;

            conversation = _conversationApplier.GetOrCreatePCQQPrivateConversation(privateUin);
        }
        else
        {
            return;
        }

        conversation.PCQQTableName = tableName;
        if (!string.IsNullOrWhiteSpace(conversationName) && string.IsNullOrWhiteSpace(conversation.GroupName))
            conversation.GroupName = conversationName;

        await JumpToConversationMessageAsync(conversation, messageId, messageSeq, clearMessageFilter);
    }

    public async Task JumpToAndroidMobileQQMessageAsync(
        AvaConversationType conversationType,
        string? peerUin,
        string? tableName,
        long messageId,
        long messageSeq,
        string? conversationName,
        bool clearMessageFilter)
    {
        if (string.IsNullOrWhiteSpace(peerUin) ||
            string.IsNullOrWhiteSpace(tableName) ||
            messageId == 0 ||
            messageSeq <= 0 ||
            conversationType is not (AvaConversationType.AndroidMobileQQGroup or AvaConversationType.AndroidMobileQQPrivate))
        {
            return;
        }

        var conversation = _conversationApplier.GetOrCreateAndroidMobileQQConversation(conversationType, peerUin);
        conversation.AndroidMobileQQTableName = tableName;
        conversation.AndroidMobileQQPeerUin = peerUin;
        if (!string.IsNullOrWhiteSpace(conversationName) && string.IsNullOrWhiteSpace(conversation.GroupName))
            conversation.GroupName = conversationName;

        await JumpToConversationMessageAsync(conversation, messageId, messageSeq, clearMessageFilter);
    }

    private async Task JumpToIcalinguaMessageAsync(
        long roomId,
        long messageId,
        long messageSeq,
        string? groupName,
        bool clearMessageFilter)
    {
        if (roomId == 0 || messageId == 0 || messageSeq <= 0)
            return;

        var group = _conversationApplier.GetOrCreateIcalinguaConversation(roomId);
        if (!string.IsNullOrWhiteSpace(groupName) && string.IsNullOrWhiteSpace(group.GroupName))
            group.GroupName = groupName;

        await JumpToConversationMessageAsync(group, messageId, messageSeq, clearMessageFilter);
    }

    private async Task JumpToPrivateMessageAsync(
        long conversationId,
        long messageId,
        long messageSeq,
        string? conversationName,
        uint peerUin,
        string? peerUid,
        bool clearMessageFilter)
    {
        if (conversationId == 0 || messageId == 0 || messageSeq <= 0)
            return;

        if (_databaseAvailability.HasQQNtMessageDatabase)
        {
            var targetMessage = _jumpTargetResolver.ResolvePrivateTarget(conversationId, [messageId], [], [messageSeq]);
            if (targetMessage is null)
                return;

            messageId = targetMessage.MessageId;
            messageSeq = targetMessage.MessageSeq;
        }

        var group = _conversationApplier.GetOrCreatePrivateConversation(conversationId);
        if (!string.IsNullOrWhiteSpace(conversationName) && string.IsNullOrWhiteSpace(group.GroupName))
            group.GroupName = conversationName;

        if (peerUin != 0)
            group.PrivateUin = peerUin;

        if (!string.IsNullOrWhiteSpace(peerUid))
            group.PrivateUid = peerUid;

        await JumpToConversationMessageAsync(group, messageId, messageSeq, clearMessageFilter);
    }

    private async Task JumpToMessageAsync(
        uint groupId,
        long messageId,
        long messageSeq,
        string? groupName,
        bool clearMessageFilter)
    {
        if (groupId == 0)
            return;

        if (_databaseAvailability.HasQQNtMessageDatabase)
        {
            var targetMessage = _jumpTargetResolver.ResolveGroupTarget(groupId, messageId, messageSeq);
            if (targetMessage is null)
                return;

            messageId = targetMessage.MessageId;
            messageSeq = targetMessage.MessageSeq;
        }

        var group = _conversationApplier.GetOrCreateGroup(groupId);
        if (!string.IsNullOrWhiteSpace(groupName) && string.IsNullOrWhiteSpace(group.GroupName))
        {
            group.GroupName = groupName;
        }

        await JumpToConversationMessageAsync(group, messageId, messageSeq, clearMessageFilter);
    }

    private async Task JumpToConversationMessageAsync(
        AvaQQGroup conversation,
        long messageId,
        long messageSeq,
        bool clearMessageFilter)
    {
        SelectSingleGroup(conversation);
        if (clearMessageFilter)
            ClearMessageFilter(conversation);

        var loadVersion = _messageLoadVersion.BeginNext();
        if (View is not null)
            await View.ScrollToGroupAsync(conversation);

        await LoadMessagesAroundAsync(conversation, messageId, messageSeq, loadVersion);
    }

    private void ClearMessageFilter(AvaQQGroup conversation)
    {
        _messageFilterState.Clear(conversation);
        if (SelectedGroup?.ConversationKey == conversation.ConversationKey)
            MessageFilter = MessageFilterCriteria.Empty;
    }

    private MessageRecord? LoadSearchResultTargetMessage(
        AvaQQGroup conversation,
        AvaGroupMessageSearchResult result)
    {
        if (conversation.ConversationType == AvaConversationType.Group)
        {
            return _databaseAvailability.HasQQNtMessageDatabase
                ? _jumpTargetResolver.ResolveGroupTarget(result.GroupId, result.MessageId, result.MessageSeq)
                : null;
        }

        if (conversation.ConversationType == AvaConversationType.Private)
        {
            return _databaseAvailability.HasQQNtMessageDatabase
                ? _jumpTargetResolver.ResolvePrivateTarget(
                    result.PrivateConversationId,
                    [result.MessageId],
                    [],
                    [result.MessageSeq])
                : null;
        }

        var query = new MessageTimelineQuery(new QQDatabaseServiceMessageDatabaseSource(_qqDatabaseService));
        return query.LoadMessageById(conversation, result.MessageSeq, result.MessageId, MessageFilterCriteria.Empty);
    }

    private static AvaQQGroup? CreateSearchResultConversation(AvaGroupMessageSearchResult result)
    {
        return result.ConversationType switch
        {
            AvaConversationType.Group when result.GroupId != 0 => new AvaQQGroup
            {
                ConversationType = AvaConversationType.Group,
                GroupId = result.GroupId,
                GroupName = result.GroupName,
            },
            AvaConversationType.Private when result.PrivateConversationId != 0 => new AvaQQGroup
            {
                ConversationType = AvaConversationType.Private,
                PrivateConversationId = result.PrivateConversationId,
                PrivateUin = result.PeerUin,
                PrivateUid = result.PeerUid,
                GroupName = result.GroupName,
            },
            AvaConversationType.PCQQGroup when result.GroupId != 0 && !string.IsNullOrWhiteSpace(result.PCQQTableName) => new AvaQQGroup
            {
                ConversationType = AvaConversationType.PCQQGroup,
                GroupId = result.GroupId,
                PCQQTableName = result.PCQQTableName,
                GroupName = result.GroupName,
            },
            AvaConversationType.PCQQPrivate when result.PeerUin != 0 && !string.IsNullOrWhiteSpace(result.PCQQTableName) => new AvaQQGroup
            {
                ConversationType = AvaConversationType.PCQQPrivate,
                PrivateUin = result.PeerUin,
                PCQQTableName = result.PCQQTableName,
                GroupName = result.GroupName,
            },
            AvaConversationType.AndroidMobileQQGroup or AvaConversationType.AndroidMobileQQPrivate
                when !string.IsNullOrWhiteSpace(result.AndroidMobileQQPeerUin) &&
                     !string.IsNullOrWhiteSpace(result.AndroidMobileQQTableName) => new AvaQQGroup
            {
                ConversationType = result.ConversationType,
                GroupId = result.GroupId,
                PrivateUin = result.PeerUin,
                AndroidMobileQQTableName = result.AndroidMobileQQTableName,
                AndroidMobileQQPeerUin = result.AndroidMobileQQPeerUin,
                GroupName = result.GroupName,
            },
            AvaConversationType.Icalingua when result.IcalinguaRoomId != 0 => new AvaQQGroup
            {
                ConversationType = AvaConversationType.Icalingua,
                IcalinguaRoomId = result.IcalinguaRoomId,
                GroupName = result.GroupName,
            },
            _ => null,
        };
    }

    private static bool IsSearchResultTargetMessage(AvaQQMessage message, AvaGroupMessageSearchResult result)
    {
        return message.MessageId == result.MessageId ||
               message.MessageSeq == result.MessageSeq &&
               result.ConversationType switch
               {
                   AvaConversationType.PCQQGroup or AvaConversationType.PCQQPrivate => message.MessageId == result.MessageId,
                   _ => true,
               };
    }

    private bool IsActiveConversationKey(string? conversationKey)
    {
        return !string.IsNullOrWhiteSpace(conversationKey) &&
               string.Equals(_conversationList.ActiveConversationKey, conversationKey, StringComparison.Ordinal);
    }
}
