using System;
using System.Collections.Generic;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class MessagePageLoader
{
    private readonly MessageSenderCache _senderCache;
    private readonly MessageJumpTargetResolver _jumpTargetResolver;
    private readonly NtMessageJumpTargetRepository _ntRepository;
    private readonly Func<bool> _hasNtMessageDatabase;
    private readonly Func<AvaQQGroup, MessageRecord, QQReplyMessage, ReplyTargetConversation?> _resolveReplyTargetConversation;

    public MessagePageLoader(
        IMessageDatabaseSource databaseSource,
        MessageSenderCache senderCache,
        MessageJumpTargetResolver jumpTargetResolver,
        Func<AvaQQGroup, MessageRecord, QQReplyMessage, ReplyTargetConversation?> resolveReplyTargetConversation)
    {
        _senderCache = senderCache;
        _jumpTargetResolver = jumpTargetResolver;
        _ntRepository = new NtMessageJumpTargetRepository(databaseSource);
        _hasNtMessageDatabase = () => databaseSource.HasNtMessageDatabase;
        _resolveReplyTargetConversation = resolveReplyTargetConversation;
    }

    public MessagePage Load(
        AvaQQGroup conversation,
        Func<List<MessageRecord>> loadMessages,
        int pageSize)
    {
        var messages = loadMessages();
        return CreatePage(conversation, messages, pageSize);
    }

    public MessagePage CreatePage(
        AvaQQGroup conversation,
        IReadOnlyList<MessageRecord> messages,
        int pageSize)
    {
        return new MessagePage(
            messages,
            LoadReferencedPrivateReplySenderInfos(conversation, messages, pageSize),
            LoadReferencedReplyTargetMessages(conversation, messages, pageSize));
    }

    private IReadOnlyList<MessageRecord> LoadReferencedPrivateReplySenderInfos(
        AvaQQGroup conversation,
        IReadOnlyList<MessageRecord> messages,
        int pageSize)
    {
        if (conversation.ConversationType != AvaConversationType.Private ||
            !_hasNtMessageDatabase())
        {
            return [];
        }

        var senderInfos = _senderCache.GetMessageSenderInfoCache(conversation.ConversationKey);

        var missingSeqs = messages
            .Select(message => QqNtMessageContentParser.TryParse(message.Content))
            .Where(content => content is not null)
            .SelectMany(content => content!.Segments)
            .Select(segment => segment.Reply)
            .Where(reply => reply is not null)
            .Select(reply => reply!.MessageSeq2)
            .Where(messageSeq => messageSeq > 0 && !senderInfos.ContainsKey(messageSeq))
            .Distinct()
            .Take(pageSize)
            .ToArray();

        if (missingSeqs.Length == 0)
            return [];

        return _ntRepository.FindPrivateMessagesBySeqs(
            conversation.PrivateConversationId,
            missingSeqs);
    }

    private IReadOnlyDictionary<ReplyTargetKey, MessageRecord> LoadReferencedReplyTargetMessages(
        AvaQQGroup conversation,
        IReadOnlyList<MessageRecord> messages,
        int pageSize)
    {
        if (!_hasNtMessageDatabase())
            return MessagePage.EmptyReplyTargetMessages;

        var replies = messages
            .Select(message => new
            {
                Message = message,
                Reply = QqNtMessageContentParser.TryParse(message.Content)?
                    .Segments
                    .Select(segment => segment.Reply)
                    .FirstOrDefault(reply => reply is not null),
            })
            .Where(item => item.Reply is not null)
            .Take(pageSize)
            .ToArray();

        if (replies.Length == 0)
            return MessagePage.EmptyReplyTargetMessages;

        var result = new Dictionary<ReplyTargetKey, MessageRecord>();
        foreach (var item in replies)
        {
            var reply = item.Reply!;
            if (_resolveReplyTargetConversation(conversation, item.Message, reply) is not { } targetConversation)
                continue;

            var key = ReplyTargetMatcher.CreateReplyTargetKey(
                targetConversation.ConversationType,
                targetConversation.GroupId,
                targetConversation.PrivateConversationId,
                reply);
            if (result.ContainsKey(key))
                continue;

            var target = targetConversation.ConversationType switch
            {
                AvaConversationType.Group => _jumpTargetResolver.ResolveGroupTarget(
                    targetConversation.GroupId,
                    ReplyTargetMatcher.GetReplyMessageIdCandidates(reply),
                    ReplyTargetMatcher.GetReplyMessageRandomCandidates(reply),
                    ReplyTargetMatcher.GetReplyMessageSeqCandidates(reply, targetConversation.ConversationType),
                    null),
                AvaConversationType.Private => _jumpTargetResolver.ResolvePrivateTarget(
                    targetConversation.PrivateConversationId,
                    ReplyTargetMatcher.GetReplyMessageIdCandidates(reply),
                    ReplyTargetMatcher.GetReplyMessageRandomCandidates(reply),
                    ReplyTargetMatcher.GetReplyMessageSeqCandidates(reply, targetConversation.ConversationType),
                    null),
                _ => null,
            };

            if (target is not null)
            {
                result[key] = target;
            }
        }

        return result.Count == 0 ? MessagePage.EmptyReplyTargetMessages : result;
    }
}
