using ObservableCollections;
using System;
using System.Collections.Generic;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.Services;
using QQDatabaseExplorer.ViewModels.MessageTab;

namespace QQDatabaseExplorer.ViewModels;

public partial class MessageTabViewModel
{
    public MessageTabViewModel(
        QQDatabaseService qqDatabaseService,
        AppSettingsService appSettingsService,
        IDialogService dialogService,
        IVoicePlaybackService voicePlaybackService)
    {
        Groups = _filteredGroups.ToNotifyCollectionChanged();
        Messages = _messages.ToNotifyCollectionChanged();
        _messageSelection = new MessageSelectionState(_messages);
        _conversationList = new ConversationListState(_groups, _filteredGroups);
        _qqDatabaseService = qqDatabaseService;
        _appSettingsService = appSettingsService;
        _dialogService = dialogService;
        _messageFilterOptionProvider = new MessageFilterOptionProvider(_qqDatabaseService);
        _databaseAvailability = new MessageDatabaseAvailability(_qqDatabaseService);
        _participantResolver = new MessageParticipantResolver(
            () => _qqDatabaseService.ProfileInfoDatabase,
            () => _qqDatabaseService.GroupInfoDatabase,
            () => _qqDatabaseService.NtDataPath);
        _conversationApplier = new MessageConversationListApplier(
            _groups,
            _conversationList,
            _participantResolver);
        _senderCache = new MessageSenderCache(CreateMessageSenderInfo);
        var displayComposition = CreateDisplayComposition(
            _qqDatabaseService,
            voicePlaybackService,
            _participantResolver,
            _senderCache,
            () => AlwaysShowMessageTime,
            () => HighlightMentions);
        _latestMessagePreviewFactory = displayComposition.LatestMessagePreviewFactory;
        _qqNtDisplayMessageFactory = displayComposition.QqNtDisplayMessageFactory;
        _pcqqDisplayMessageFactory = displayComposition.PCQQDisplayMessageFactory;
        _androidMobileQQDisplayMessageFactory = displayComposition.AndroidMobileQQDisplayMessageFactory;
        _icalinguaDisplayMessageFactory = displayComposition.IcalinguaDisplayMessageFactory;
        _mentionUinResolver = displayComposition.MentionUinResolver;
        _databaseChangeCoordinator = CreateDatabaseChangeCoordinator(
            _qqDatabaseService,
            _databaseAvailability,
            _latestMessagePreviewFactory,
            _participantResolver,
            _senderCache,
            _conversationApplier,
            () => _groups,
            () => _messages,
            () => SelectedGroup,
            ClearMessageSelection,
            () => _messages.Clear(),
            ClearGroupSelectionState,
            RefreshFilteredGroups);
        _replyTargetConversationResolver = new ReplyTargetConversationResolver(
            _groups,
            _conversationApplier,
            () => SelectedGroup);
        var loadingComposition = CreateLoadingComposition(
            _qqDatabaseService,
            _messageFilterState,
            _latestMessagePreviewFactory,
            _senderCache,
            JumpContextPageSize,
            _databaseAvailability.HasMessageDatabase,
            HideMessagesForReload,
            PrepareMessageWindowForReload,
            WaitForMessageRefreshFrameAsync,
            IsMessageLoadCurrent,
            _messageLoadVersion.IsCurrentVersion,
            ShowMessagesImmediately);
        _messageTimelineFacade = loadingComposition.TimelineFacade;
        _jumpTargetResolver = loadingComposition.JumpTargetResolver;
        _messagePageLoader = loadingComposition.PageLoader;
        _messageReloadRunner = loadingComposition.ReloadRunner;
        _messageAppendLoadRunner = loadingComposition.AppendLoadRunner;
        _messagePageDisplayBuilder = new MessagePageDisplayBuilder(
            (conversation, page) => CacheSenderInfos(conversation, page),
            (conversation, messages, referencedMessages) => CacheSenderInfos(conversation, messages, referencedMessages),
            CreateAvaQQMessage);

        _qqDatabaseService.DatabaseAdded += OnDatabaseAdded;
        _qqDatabaseService.DatabaseAddedAsync += OnDatabaseAddedAsync;
        _qqDatabaseService.DatabaseRemoved += OnDatabaseRemoved;
        _appSettingsService.SettingsChanged += OnAppSettingsChanged;
    }

    private static MessageDisplayComposition CreateDisplayComposition(
        QQDatabaseService databaseService,
        IVoicePlaybackService voicePlaybackService,
        MessageParticipantResolver participantResolver,
        MessageSenderCache senderCache,
        Func<bool> alwaysShowMessageTime,
        Func<bool> highlightMentions)
    {
        var localMediaContextFactory = new LocalMediaContextFactory(databaseService);
        var qqNtDisplayFactories = QqNtMessageDisplayFactoryBuilder.Create(
            participantResolver,
            senderCache,
            localMediaContextFactory,
            alwaysShowMessageTime,
            highlightMentions,
            voicePlaybackService.CanPlay,
            voicePlaybackService.GetDurationMilliseconds);
        var pcqqDisplayMessageFactory = new PCQQDisplayMessageFactory(
            alwaysShowMessageTime,
            highlightMentions,
            () => databaseService.PCQQDataPath,
            uin => databaseService.PCQQMessageDatabase?.ResolveContactName(uin));
        var androidMobileQQDisplayMessageFactory = new AndroidMobileQQDisplayMessageFactory(
            alwaysShowMessageTime,
            highlightMentions);
        var icalinguaDisplayMessageFactory = IcalinguaDisplayMessageFactory.Create(
            alwaysShowMessageTime,
            highlightMentions,
            (file, conversation) => IcalinguaMediaPathResolver.ResolveLocalFilePath(
                file,
                conversation,
                databaseService.IcalinguaMessageDatabases),
            (file, localPath, conversation) => IcalinguaMediaPathResolver.ResolveVideoCoverPath(
                file,
                localPath,
                conversation,
                databaseService.IcalinguaMessageDatabases),
            (conversation, rawId) => databaseService.IcalinguaMessageDatabases?.LoadMessageByRawId(
                conversation.IcalinguaRoomId,
                rawId),
            voicePlaybackService.CanPlay,
            voicePlaybackService.GetDurationMilliseconds);
        var mentionUinResolver = new MentionUinResolver(participantResolver);

        return new MessageDisplayComposition(
            qqNtDisplayFactories.LatestMessagePreviewFactory,
            qqNtDisplayFactories.DisplayMessageFactory,
            pcqqDisplayMessageFactory,
            androidMobileQQDisplayMessageFactory,
            icalinguaDisplayMessageFactory,
            mentionUinResolver);
    }

    private static MessageDatabaseChangeCoordinator CreateDatabaseChangeCoordinator(
        QQDatabaseService databaseService,
        MessageDatabaseAvailability databaseAvailability,
        LatestMessagePreviewFactory latestMessagePreviewFactory,
        MessageParticipantResolver participantResolver,
        MessageSenderCache senderCache,
        MessageConversationListApplier conversationApplier,
        Func<IEnumerable<AvaQQGroup>> getConversations,
        Func<IEnumerable<AvaQQMessage>> getVisibleMessages,
        Func<AvaQQGroup?> getSelectedConversation,
        Action clearMessageSelection,
        Action clearMessages,
        Action clearGroupSelection,
        Action refreshFilteredGroups)
    {
        var profileInfoApplier = new MessageProfileInfoApplier(participantResolver, senderCache);
        var conversationDirectoryLoader = new MessageConversationDirectoryLoader(
            latestMessagePreviewFactory,
            participantResolver,
            () => databaseService.IcalinguaMessageDatabases);
        var conversationDatabaseSynchronizer = new MessageConversationDatabaseSynchronizer(
            conversationDirectoryLoader,
            conversationApplier);

        return new MessageDatabaseChangeCoordinator(
            conversationDatabaseSynchronizer,
            participantResolver,
            profileInfoApplier,
            senderCache,
            () => databaseService.GroupInfoDatabase is not null,
            () => databaseAvailability.HasQQNtMessageDatabase,
            getConversations,
            getVisibleMessages,
            getSelectedConversation,
            clearMessageSelection,
            clearMessages,
            clearGroupSelection,
            refreshFilteredGroups);
    }

    private static MessageLoadingComposition CreateLoadingComposition(
        QQDatabaseService databaseService,
        MessageFilterState messageFilterState,
        LatestMessagePreviewFactory latestMessagePreviewFactory,
        MessageSenderCache senderCache,
        int jumpContextPageSize,
        Func<AvaQQGroup, bool> hasMessageDatabase,
        Action<MessageReloadScrollIntent> hideMessagesForReload,
        Action prepareMessageWindowForReload,
        Func<System.Threading.Tasks.Task> waitForMessageRefreshFrameAsync,
        Func<AvaQQGroup, int, bool> isMessageLoadCurrent,
        Func<int, bool> isCurrentLoadVersion,
        Action showMessagesImmediately)
    {
        var databaseSource = new QQDatabaseServiceMessageDatabaseSource(databaseService);
        var timelineFacade = new MessageTimelineFacade(
            new MessageTimelineQuery(databaseSource),
            messageFilterState,
            jumpContextPageSize);
        var jumpTargetResolver = new MessageJumpTargetResolver(
            databaseSource,
            message => latestMessagePreviewFactory.CreatePreviewText(message));
        var pageLoader = new MessagePageLoader(
            databaseSource,
            senderCache,
            jumpTargetResolver,
            QqNtReplyTargetResolver.ResolveTargetConversation);
        var reloadRunner = new MessageReloadRunner(
            hasMessageDatabase,
            hideMessagesForReload,
            prepareMessageWindowForReload,
            waitForMessageRefreshFrameAsync,
            isMessageLoadCurrent,
            isCurrentLoadVersion,
            showMessagesImmediately);
        var appendLoadRunner = new MessageAppendLoadRunner(isMessageLoadCurrent);

        return new MessageLoadingComposition(
            timelineFacade,
            jumpTargetResolver,
            pageLoader,
            reloadRunner,
            appendLoadRunner);
    }
}

internal sealed record MessageDisplayComposition(
    LatestMessagePreviewFactory LatestMessagePreviewFactory,
    QqNtDisplayMessageFactory QqNtDisplayMessageFactory,
    PCQQDisplayMessageFactory PCQQDisplayMessageFactory,
    AndroidMobileQQDisplayMessageFactory AndroidMobileQQDisplayMessageFactory,
    IcalinguaDisplayMessageFactory IcalinguaDisplayMessageFactory,
    MentionUinResolver MentionUinResolver);

internal sealed record MessageLoadingComposition(
    MessageTimelineFacade TimelineFacade,
    MessageJumpTargetResolver JumpTargetResolver,
    MessagePageLoader PageLoader,
    MessageReloadRunner ReloadRunner,
    MessageAppendLoadRunner AppendLoadRunner);
