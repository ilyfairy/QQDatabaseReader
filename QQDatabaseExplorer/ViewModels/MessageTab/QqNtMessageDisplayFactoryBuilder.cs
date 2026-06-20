using System;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed record QqNtMessageDisplayFactories(
    LatestMessagePreviewFactory LatestMessagePreviewFactory,
    QqNtDisplayMessageFactory DisplayMessageFactory);

internal static class QqNtMessageDisplayFactoryBuilder
{
    public static QqNtMessageDisplayFactories Create(
        MessageParticipantResolver participantResolver,
        MessageSenderCache senderCache,
        LocalMediaContextFactory localMediaContextFactory,
        Func<bool> alwaysShowMessageTime,
        Func<bool> highlightMentions,
        Func<string?, bool> canPlayVoice,
        Func<string?, int?> getVoiceDurationMilliseconds)
    {
        var mediaApplier = new MessageSegmentMediaApplier(
            canPlayVoice,
            getVoiceDurationMilliseconds);
        var mediaResolver = new QqNtMessageMediaResolver(mediaApplier);
        var forwardedMessageFactory = new QqNtForwardedMessageDisplayFactory(
            mediaResolver,
            highlightMentions);
        var recalledOriginalMessageFactory = new QqNtRecalledOriginalMessageFactory();
        var systemHintFactory = new QqNtSystemHintDisplayFactory(
            participantResolver.ResolveSystemHintParticipantName,
            participantResolver.ResolveSystemHintSourceUin);
        var replySenderNameResolver = new QqNtReplySenderNameResolver(participantResolver);
        var replyFactory = new QqNtReplyDisplayFactory(
            message => QqNtMessageContentParser.TryParse(message.Content),
            QqNtReplyTargetResolver.ResolveTargetConversationForReplyFactory,
            QqNtReplyTargetResolver.ResolveSourceGroupId,
            QqNtReplyTargetResolver.ResolveSourceGroupName,
            replySenderNameResolver.Resolve);
        var latestMessagePreviewFactory = new LatestMessagePreviewFactory(
            systemHintFactory,
            participantResolver.ResolveProfileDisplayName);
        var displayMessageFactory = new QqNtDisplayMessageFactory(
            mediaResolver,
            forwardedMessageFactory,
            recalledOriginalMessageFactory,
            systemHintFactory,
            replyFactory,
            participantResolver,
            senderCache,
            localMediaContextFactory.Create,
            alwaysShowMessageTime,
            highlightMentions);

        return new QqNtMessageDisplayFactories(
            latestMessagePreviewFactory,
            displayMessageFactory);
    }
}
