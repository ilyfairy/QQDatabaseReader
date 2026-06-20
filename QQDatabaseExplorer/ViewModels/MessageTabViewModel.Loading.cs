using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QQDatabaseExplorer.Models;
using QQDatabaseExplorer.ViewModels.MessageTab;

namespace QQDatabaseExplorer.ViewModels;

public partial class MessageTabViewModel
{
    private async Task LoadInitialMessagesAsync(AvaQQGroup conversation, int loadVersion)
    {
        try
        {
            IsLoadingInitialMessages = true;
            await _messageReloadRunner.RunAsync(
                conversation,
                loadVersion,
                MessageReloadScrollIntent.ScrollToBottom,
                async context =>
                {
                    var page = await _messageDatabaseQueryRunner.RunAsync(() => LoadMessagePage(conversation, () => _messageTimelineFacade.LoadInitialMessages(conversation, PageSize)));

                    if (!context.IsCurrent)
                        return;

                    var avaMessages = await _messagePageDisplayBuilder.CreateAsync(conversation, page);

                    if (!context.IsCurrent)
                        return;

                    _messages.AddLastRange(avaMessages);
                    UpdateMessageWindowState(page.Messages, olderAvailable: page.Messages.Count == PageSize, newerAvailable: false);
                    UpdateCurrentConversationLatestPreview(conversation, page.Messages);
                    ScrollToBottom();
                });
        }
        finally
        {
            if (_messageLoadVersion.IsCurrentVersion(loadVersion))
                IsLoadingInitialMessages = false;
        }
    }

    private async Task LoadEarliestMessagesAsync(AvaQQGroup conversation, int loadVersion)
    {
        IsLoadingInitialMessages = false;
        await _messageReloadRunner.RunAsync(
            conversation,
            loadVersion,
            MessageReloadScrollIntent.MessageJump,
            async context =>
            {
                var page = await _messageDatabaseQueryRunner.RunAsync(() => LoadMessagePage(conversation, () => _messageTimelineFacade.LoadEarliestMessages(conversation, PageSize)));

                if (!context.IsCurrent)
                    return;

                var avaMessages = await _messagePageDisplayBuilder.CreateAsync(conversation, page);

                if (!context.IsCurrent)
                    return;

                _messages.AddLastRange(avaMessages);
                UpdateMessageWindowState(page.Messages, olderAvailable: false, newerAvailable: page.Messages.Count == PageSize);
                View?.ScrollToTop();
                View?.ShowMessagesImmediately();
            });
    }

    private async Task LoadMessagesAroundAsync(AvaQQGroup? conversation, long messageId, long messageSeq, int loadVersion)
    {
        if (conversation is null)
            return;

        IsLoadingInitialMessages = false;
        await _messageReloadRunner.RunAsync(
            conversation,
            loadVersion,
            MessageReloadScrollIntent.MessageJump,
            async context =>
            {
                var (olderMessages, targetAndNewerMessages, referencedMessages, replyTargetMessages) = await _messageDatabaseQueryRunner.RunAsync(() =>
                {
                    var older = _messageTimelineFacade.LoadOlderMessages(conversation, messageSeq, messageId, JumpContextPageSize);
                    var targetAndNewer = _messageTimelineFacade.LoadTargetAndNewerMessages(conversation, messageSeq, messageId);
                    var messages = older.Concat(targetAndNewer).DistinctBy(message => message.MessageId).ToArray();
                    var page = _messagePageLoader.CreatePage(conversation, messages, PageSize);
                    var referenced = page.ReferencedMessages;
                    var replyTargets = page.ReplyTargetMessages;
                    return (older, targetAndNewer, referenced, replyTargets);
                });

                if (!context.IsCurrent)
                    return;

                var messages = olderMessages
                    .OrderBy(message => message.MessageSeq)
                    .Concat(targetAndNewerMessages)
                    .DistinctBy(message => message.MessageId)
                    .OrderBy(message => message.MessageSeq)
                    .ThenBy(message => message.MessageId)
                    .ToList();

                var avaMessages = await _messagePageDisplayBuilder.CreateAsync(
                    conversation,
                    messages,
                    referencedMessages.Concat(replyTargetMessages.Values).ToArray(),
                    replyTargetMessages);

                if (!context.IsCurrent)
                    return;

                _messages.AddLastRange(avaMessages);
                UpdateMessageWindowState(
                    messages,
                    olderAvailable: olderMessages.Count == JumpContextPageSize,
                    newerAvailable: targetAndNewerMessages.Count == JumpContextPageSize + 1);

                View?.ScrollToMessage(messageId);
            });
    }

    public async Task<int> LoadPreviousMessagesAsync()
    {
        if (IsLoadingPrevious ||
            !HasOlderMessages ||
            SelectedGroup is null ||
            !_databaseAvailability.HasMessageDatabase(SelectedGroup) ||
            _messages.Count == 0)
        {
            return 0;
        }

        try
        {
            IsLoadingPrevious = true;
            var conversation = SelectedGroup;
            var loadVersion = _messageLoadVersion.Current;
            var oldestMessage = _messages.First();
            var messageSeq = oldestMessage.MessageSeq;
            var messageId = oldestMessage.MessageId;
            return await _messageAppendLoadRunner.RunAsync(
                conversation,
                loadVersion,
                () => _messageDatabaseQueryRunner.RunAsync(() => LoadMessagePage(
                    conversation,
                    () => _messageTimelineFacade.LoadOlderMessages(conversation, messageSeq, messageId, PageSize))),
                page => _messagePageDisplayBuilder.CreateAsync(conversation, page),
                (page, avaMessages) =>
                {
                    foreach (var message in avaMessages)
                    {
                        _messages.AddFirst(message);
                    }

                    _messageWindowState.UpdateOlderAvailability(page.Messages.Count, PageSize);
                    NotifyMessageWindowAvailabilityChanged();
                    return page.Messages.Count;
                });
        }
        finally
        {
            IsLoadingPrevious = false;
        }
    }

    public async Task<int> LoadNextMessagesAsync()
    {
        if (IsLoadingNext ||
            !HasNewerMessages ||
            SelectedGroup is null ||
            !_databaseAvailability.HasMessageDatabase(SelectedGroup) ||
            _messages.Count == 0)
        {
            return 0;
        }

        try
        {
            IsLoadingNext = true;
            var conversation = SelectedGroup;
            var loadVersion = _messageLoadVersion.Current;
            var newestMessage = _messages.Last();
            var messageSeq = newestMessage.MessageSeq;
            var messageId = newestMessage.MessageId;
            return await _messageAppendLoadRunner.RunAsync(
                conversation,
                loadVersion,
                () => _messageDatabaseQueryRunner.RunAsync(() => LoadMessagePage(
                    conversation,
                    () => _messageTimelineFacade.LoadNewerMessages(conversation, messageSeq, messageId, PageSize))),
                page => _messagePageDisplayBuilder.CreateAsync(conversation, page),
                (page, avaMessages) =>
                {
                    _messages.AddLastRange(avaMessages);
                    _messageWindowState.UpdateNewerAvailability(page.Messages.Count, PageSize);
                    NotifyMessageWindowAvailabilityChanged();
                    if (!HasNewerMessages)
                    {
                        UpdateCurrentConversationLatestPreview(conversation, page.Messages);
                    }

                    return page.Messages.Count;
                });
        }
        finally
        {
            IsLoadingNext = false;
        }
    }

    private void PrepareMessageWindowForReload()
    {
        ClearMessageSelection();
        _messages.Clear();
        ResetMessageWindowState();
    }

    private bool IsMessageLoadCurrent(AvaQQGroup conversation, int loadVersion)
    {
        return _messageLoadVersion.IsCurrent(SelectedGroup, conversation, loadVersion);
    }

    private void HideMessagesForReload(MessageReloadScrollIntent intent)
    {
        if (intent == MessageReloadScrollIntent.ScrollToBottom)
            View?.HideMessagesUntilNextScrollToBottom();
        else
            View?.HideMessagesUntilNextMessageJump();
    }

    private Task WaitForMessageRefreshFrameAsync()
    {
        return View?.WaitForMessageRefreshFrameAsync() ?? Task.CompletedTask;
    }

    private void ShowMessagesImmediately()
    {
        View?.ShowMessagesImmediately();
    }

    private void ResetMessageWindowState()
    {
        _messageWindowState.Reset();
        IsLoadingInitialMessages = false;
        IsLoadingPrevious = false;
        IsLoadingNext = false;
        NotifyMessageWindowAvailabilityChanged();
    }

    private void UpdateMessageWindowState(
        IReadOnlyCollection<MessageRecord> messages,
        bool olderAvailable,
        bool newerAvailable)
    {
        _messageWindowState.Update(messages, olderAvailable, newerAvailable);
        NotifyMessageWindowAvailabilityChanged();
    }

    private void NotifyMessageWindowAvailabilityChanged()
    {
        OnPropertyChanged(nameof(HasOlderMessages));
        OnPropertyChanged(nameof(HasNewerMessages));
    }

    private void UpdateCurrentConversationLatestPreview(
        AvaQQGroup conversation,
        IReadOnlyCollection<MessageRecord> messages)
    {
        if (!GetCurrentMessageFilter(conversation).IsEmpty)
            return;

        if (messages.Count == 0 ||
            SelectedGroup?.ConversationKey != conversation.ConversationKey)
        {
            return;
        }

        var latestMessage = messages
            .OrderBy(message => message.MessageSeq)
            .ThenBy(message => message.MessageId)
            .Last();
        conversation.LatestMessageText = _latestMessagePreviewFactory.Create(conversation, latestMessage);
        conversation.LatestMessageTime = latestMessage.MessageTime;
    }

    private MessagePage LoadMessagePage(
        AvaQQGroup conversation,
        Func<List<MessageRecord>> loadMessages)
    {
        return _messagePageLoader.Load(conversation, loadMessages, PageSize);
    }

    private void CacheSenderInfos(AvaQQGroup conversation, MessagePage page)
    {
        CacheSenderInfos(
            conversation,
            page.Messages,
            page.ReferencedMessages.Concat(page.ReplyTargetMessages.Values).ToArray());
    }

    private void CacheSenderInfos(
        AvaQQGroup conversation,
        IEnumerable<MessageRecord> messages,
        IReadOnlyList<MessageRecord>? referencedMessages = null)
    {
        _senderCache.Cache(conversation, messages, referencedMessages);
    }
}

internal sealed class MessageAppendLoadRunner
{
    private readonly Func<AvaQQGroup, int, bool> _isCurrent;

    public MessageAppendLoadRunner(Func<AvaQQGroup, int, bool> isCurrent)
    {
        _isCurrent = isCurrent;
    }

    public async Task<int> RunAsync(
        AvaQQGroup conversation,
        int loadVersion,
        Func<Task<MessagePage>> loadPageAsync,
        Func<MessagePage, Task<AvaQQMessage[]>> createDisplayMessagesAsync,
        Func<MessagePage, AvaQQMessage[], int> applyMessages)
    {
        var page = await loadPageAsync();
        if (!_isCurrent(conversation, loadVersion))
            return 0;

        var displayMessages = await createDisplayMessagesAsync(page);
        if (!_isCurrent(conversation, loadVersion))
            return 0;

        return applyMessages(page, displayMessages);
    }
}
