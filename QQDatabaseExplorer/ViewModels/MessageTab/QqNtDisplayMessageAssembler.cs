using System.Collections.Generic;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class QqNtDisplayMessageAssembler
{
    private readonly QqNtMainMessageDisplayContentFactory _mainContentFactory;
    private readonly QqNtForwardedMessageDisplayFactory _forwardedMessageFactory;
    private readonly QqNtSystemHintDisplayFactory _systemHintFactory;
    private readonly QqNtReplyDisplayFactory _replyFactory;
    private readonly MessageParticipantResolver _participantResolver;
    private readonly MessageSenderCache _senderCache;

    public QqNtDisplayMessageAssembler(
        QqNtMessageMediaResolver mediaResolver,
        QqNtForwardedMessageDisplayFactory forwardedMessageFactory,
        QqNtRecalledOriginalMessageFactory recalledOriginalMessageFactory,
        QqNtSystemHintDisplayFactory systemHintFactory,
        QqNtReplyDisplayFactory replyFactory,
        MessageParticipantResolver participantResolver,
        MessageSenderCache senderCache)
    {
        _mainContentFactory = new QqNtMainMessageDisplayContentFactory(
            mediaResolver,
            recalledOriginalMessageFactory);
        _forwardedMessageFactory = forwardedMessageFactory;
        _systemHintFactory = systemHintFactory;
        _replyFactory = replyFactory;
        _participantResolver = participantResolver;
        _senderCache = senderCache;
    }

    public QqNtDisplayMessageAssembly Create(
        MessageRecord item,
        AvaQQGroup conversation,
        IReadOnlyDictionary<ReplyTargetKey, MessageRecord> replyTargetMessages,
        LocalMediaContext mediaContext)
    {
        var senderContext = CreateSenderContext(item, conversation);
        var mainContent = _mainContentFactory.Create(item, senderContext.SenderName, mediaContext);

        var systemHint = _systemHintFactory.Create(
            mainContent.Content,
            conversation.GroupId,
            item.SenderId,
            item.SenderUid,
            mainContent.SenderName) ??
            QqNtSystemHintDisplayFactory.CreateFallback(item, mainContent.Segments);
        var forwardedMessages = _forwardedMessageFactory.CreateForwardedMessages(item, mainContent.Content, mediaContext);
        var reactions = MessageReactionDisplayFactory.Create(item.MessageReactions);
        var reply = _replyFactory.Create(new QqNtReplyDisplayRequest(
            item,
            mainContent.Content,
            conversation,
            mainContent.SenderName,
            senderContext.SenderNames,
            senderContext.MessageSenderInfos,
            replyTargetMessages,
            mediaContext));

        if (systemHint is null)
            QqNtMainMessageDisplayContentFactory.EnsureFallbackText(mainContent.Segments, item);

        return new QqNtDisplayMessageAssembly(
            mainContent.Segments,
            forwardedMessages,
            reply,
            reactions,
            mainContent.RecalledMessage,
            systemHint,
            mainContent.SenderName,
            _participantResolver.ResolveMessageAvatarLocalPath(item, conversation));
    }

    private QqNtDisplaySenderContext CreateSenderContext(MessageRecord item, AvaQQGroup conversation)
    {
        var senderNames = _senderCache.GetSenderNameCache(conversation.ConversationKey);
        var messageSenderInfos = _senderCache.GetMessageSenderInfoCache(conversation.ConversationKey);
        return new QqNtDisplaySenderContext(
            senderNames,
            messageSenderInfos,
            _participantResolver.ResolveMessageSenderName(item, conversation, senderNames));
    }
}
