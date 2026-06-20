using System;
using System.Collections.Generic;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class QqNtDisplayMessageFactory
{
    private readonly QqNtDisplayMessageAssembler _assembler;
    private readonly Func<LocalMediaContext> _createLocalMediaContext;
    private readonly Func<bool> _alwaysShowMessageTime;
    private readonly Func<bool> _highlightMentions;

    public QqNtDisplayMessageFactory(
        QqNtMessageMediaResolver mediaResolver,
        QqNtForwardedMessageDisplayFactory forwardedMessageFactory,
        QqNtRecalledOriginalMessageFactory recalledOriginalMessageFactory,
        QqNtSystemHintDisplayFactory systemHintFactory,
        QqNtReplyDisplayFactory replyFactory,
        MessageParticipantResolver participantResolver,
        MessageSenderCache senderCache,
        Func<LocalMediaContext> createLocalMediaContext,
        Func<bool> alwaysShowMessageTime,
        Func<bool> highlightMentions)
    {
        _assembler = new QqNtDisplayMessageAssembler(
            mediaResolver,
            forwardedMessageFactory,
            recalledOriginalMessageFactory,
            systemHintFactory,
            replyFactory,
            participantResolver,
            senderCache);
        _createLocalMediaContext = createLocalMediaContext;
        _alwaysShowMessageTime = alwaysShowMessageTime;
        _highlightMentions = highlightMentions;
    }

    public AvaQQMessage Create(
        MessageRecord item,
        AvaQQGroup conversation,
        IReadOnlyDictionary<ReplyTargetKey, MessageRecord> replyTargetMessages)
    {
        var mediaContext = _createLocalMediaContext();
        var assembly = _assembler.Create(item, conversation, replyTargetMessages, mediaContext);

        return QqNtDisplayMessageBuilder.Create(
            item,
            conversation,
            assembly.Segments,
            assembly.ForwardedMessages,
            assembly.Reply,
            assembly.Reactions,
            assembly.RecalledMessage,
            assembly.SystemHint,
            assembly.SenderName,
            assembly.CachedAvatarLocalPath,
            _alwaysShowMessageTime(),
            _highlightMentions());
    }
}
