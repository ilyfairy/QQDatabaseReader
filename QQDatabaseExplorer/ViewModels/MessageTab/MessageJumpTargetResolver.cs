using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class MessageJumpTargetResolver
{
    private readonly IMessageDatabaseSource _databaseSource;
    private readonly NtMessageJumpTargetRepository _ntRepository;
    private readonly Func<MessageRecord, string> _createPreviewText;

    public MessageJumpTargetResolver(
        IMessageDatabaseSource databaseSource,
        Func<MessageRecord, string> createPreviewText)
    {
        _databaseSource = databaseSource;
        _ntRepository = new NtMessageJumpTargetRepository(databaseSource);
        _createPreviewText = createPreviewText;
    }

    public MessageRecord? ResolveGroupTarget(
        uint groupId,
        long messageId,
        long messageSeq)
    {
        return ResolveGroupTarget(groupId, [messageId], [], [messageSeq]);
    }

    public MessageRecord? ResolveGroupTarget(
        uint groupId,
        IReadOnlyList<long> messageIds,
        IReadOnlyList<long> messageRandoms,
        IReadOnlyList<long> messageSeqs,
        AvaReplyMessage? reply = null)
    {
        var messageRandomCandidates = messageRandoms
            .Where(value => value > 0)
            .Distinct()
            .ToArray();
        foreach (var messageRandom in messageRandomCandidates)
        {
            foreach (var messageSeq in messageSeqs.Where(value => value > 0).Distinct())
            {
                var matches = _ntRepository.FindGroupCandidates(
                    groupId,
                    new MessageJumpTargetCandidate(messageRandom, messageSeq),
                    16);
                var targetMessage = SelectMessageRecordJumpTarget(matches, reply);
                if (targetMessage is not null)
                    return targetMessage;
            }

            foreach (var messageId in messageIds.Where(value => value > 0).Distinct())
            {
                var matches = _ntRepository.FindGroupCandidates(
                    groupId,
                    new MessageJumpTargetCandidate(messageRandom, MessageId: messageId),
                    16);
                var targetMessage = SelectMessageRecordJumpTarget(matches, reply);
                if (targetMessage is not null)
                    return targetMessage;
            }

            var randomMatches = _ntRepository.FindGroupCandidates(
                groupId,
                new MessageJumpTargetCandidate(messageRandom),
                16);
            var randomMatch = SelectMessageRecordJumpTarget(randomMatches, reply);
            if (randomMatch is not null)
                return randomMatch;
        }

        if (messageRandomCandidates.Length > 0)
            return null;

        foreach (var messageId in messageIds.Where(value => value > 0).Distinct())
        {
            var matches = _ntRepository.FindGroupCandidates(
                groupId,
                new MessageJumpTargetCandidate(MessageId: messageId),
                16);
            var targetMessage = SelectMessageRecordJumpTarget(matches, reply);
            if (targetMessage is not null)
                return targetMessage;
        }

        foreach (var messageSeq in messageSeqs.Where(value => value > 0).Distinct())
        {
            var seqMatches = _ntRepository.FindGroupCandidates(
                groupId,
                new MessageJumpTargetCandidate(MessageSeq: messageSeq),
                32);
            var seqMatch = SelectMessageRecordJumpTarget(seqMatches, reply);
            if (seqMatch is not null)
                return seqMatch;
        }

        return null;
    }

    public MessageRecord? ResolvePrivateTarget(
        long conversationId,
        IReadOnlyList<long> messageIds,
        IReadOnlyList<long> messageRandoms,
        IReadOnlyList<long> messageSeqs,
        AvaReplyMessage? reply = null)
    {
        var messageRandomCandidates = messageRandoms
            .Where(value => value > 0)
            .Distinct()
            .ToArray();
        foreach (var messageRandom in messageRandomCandidates)
        {
            foreach (var messageSeq in messageSeqs.Where(value => value > 0).Distinct())
            {
                var matches = _ntRepository.FindPrivateCandidates(
                    conversationId,
                    new MessageJumpTargetCandidate(messageRandom, messageSeq),
                    16);
                var targetMessage = SelectMessageRecordJumpTarget(matches, reply);
                if (targetMessage is not null)
                    return targetMessage;
            }

            foreach (var messageId in messageIds.Where(value => value > 0).Distinct())
            {
                var matches = _ntRepository.FindPrivateCandidates(
                    conversationId,
                    new MessageJumpTargetCandidate(messageRandom, MessageId: messageId),
                    16);
                var targetMessage = SelectMessageRecordJumpTarget(matches, reply);
                if (targetMessage is not null)
                    return targetMessage;
            }

            var randomMatches = _ntRepository.FindPrivateCandidates(
                conversationId,
                new MessageJumpTargetCandidate(messageRandom),
                16);
            var randomMatch = SelectMessageRecordJumpTarget(randomMatches, reply);
            if (randomMatch is not null)
                return randomMatch;
        }

        if (messageRandomCandidates.Length > 0 && messageSeqs.Count == 0)
            return null;

        foreach (var messageId in messageIds.Where(value => value > 0).Distinct())
        {
            var matches = _ntRepository.FindPrivateCandidates(
                conversationId,
                new MessageJumpTargetCandidate(MessageId: messageId),
                16);
            var targetMessage = SelectMessageRecordJumpTarget(matches, reply);
            if (targetMessage is not null)
                return targetMessage;
        }

        foreach (var messageSeq in messageSeqs.Where(value => value > 0).Distinct())
        {
            var seqMatches = _ntRepository.FindPrivateCandidates(
                conversationId,
                new MessageJumpTargetCandidate(MessageSeq: messageSeq),
                32);
            var seqMatch = SelectMessageRecordJumpTarget(seqMatches, reply);
            if (seqMatch is not null)
                return seqMatch;
        }

        return null;
    }

    public MessageRecord? ResolveIcalinguaReplyTarget(
        AvaQQGroup conversation,
        AvaReplyMessage reply)
    {
        if (_databaseSource.IcalinguaMessageDatabases is not { } icalinguaDatabases ||
            conversation.IcalinguaRoomId == 0)
        {
            return null;
        }

        var targetMessage = !string.IsNullOrWhiteSpace(reply.RawMessageId)
            ? icalinguaDatabases.LoadMessageByRawId(conversation.IcalinguaRoomId, reply.RawMessageId)
            : null;
        if (targetMessage is null && reply.MessageRandom > 0)
        {
            targetMessage = icalinguaDatabases.LoadMessageByRawId(
                conversation.IcalinguaRoomId,
                reply.MessageRandom.ToString(CultureInfo.InvariantCulture));
        }

        return targetMessage is null
            ? null
            : MessageRecordFactory.FromIcalingua(targetMessage, conversation);
    }

    public MessageRecord? ResolvePCQQReplyTarget(
        AvaQQGroup conversation,
        AvaReplyMessage reply,
        AvaConversationType sourceConversationType)
    {
        if (_databaseSource.PCQQMessageDatabase is not { } pcqqDatabase ||
            string.IsNullOrWhiteSpace(conversation.PCQQTableName))
        {
            return null;
        }

        PCQQMessageRecord? targetMessage = null;
        foreach (var messageSeq in ReplyTargetMatcher.GetReplyMessageSeqCandidates(reply, sourceConversationType))
        {
            targetMessage = pcqqDatabase.LoadMessageBySeq(conversation.PCQQTableName, messageSeq);
            if (targetMessage is not null)
                break;
        }

        return targetMessage is null
            ? null
            : MessageRecordFactory.FromPCQQ(targetMessage, conversation);
    }

    private MessageRecord? SelectMessageRecordJumpTarget(
        IReadOnlyList<MessageRecord> candidates,
        AvaReplyMessage? reply)
    {
        return ReplyTargetMatcher.SelectReplyTargetCandidate(
            candidates,
            reply,
            message => message.SenderId,
            message => message.MessageTime,
            _createPreviewText);
    }
}
