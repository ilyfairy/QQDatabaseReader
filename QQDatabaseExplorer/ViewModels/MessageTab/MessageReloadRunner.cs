using System;
using System.Threading.Tasks;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal enum MessageReloadScrollIntent
{
    ScrollToBottom,
    MessageJump,
}

internal sealed class MessageReloadContext
{
    private readonly Func<AvaQQGroup, int, bool> _isCurrent;

    public MessageReloadContext(AvaQQGroup conversation, int loadVersion, Func<AvaQQGroup, int, bool> isCurrent)
    {
        Conversation = conversation;
        LoadVersion = loadVersion;
        _isCurrent = isCurrent;
    }

    public AvaQQGroup Conversation { get; }

    public int LoadVersion { get; }

    public bool IsCurrent => _isCurrent(Conversation, LoadVersion);
}

internal sealed class MessageReloadRunner
{
    private readonly Func<AvaQQGroup, bool> _hasMessageDatabase;
    private readonly Action<MessageReloadScrollIntent> _hideMessages;
    private readonly Action _prepareReload;
    private readonly Func<Task> _waitForRefreshFrameAsync;
    private readonly Func<AvaQQGroup, int, bool> _isCurrent;
    private readonly Func<int, bool> _isCurrentVersion;
    private readonly Action _showMessagesImmediately;

    public MessageReloadRunner(
        Func<AvaQQGroup, bool> hasMessageDatabase,
        Action<MessageReloadScrollIntent> hideMessages,
        Action prepareReload,
        Func<Task> waitForRefreshFrameAsync,
        Func<AvaQQGroup, int, bool> isCurrent,
        Func<int, bool> isCurrentVersion,
        Action showMessagesImmediately)
    {
        _hasMessageDatabase = hasMessageDatabase;
        _hideMessages = hideMessages;
        _prepareReload = prepareReload;
        _waitForRefreshFrameAsync = waitForRefreshFrameAsync;
        _isCurrent = isCurrent;
        _isCurrentVersion = isCurrentVersion;
        _showMessagesImmediately = showMessagesImmediately;
    }

    public async Task RunAsync(
        AvaQQGroup conversation,
        int loadVersion,
        MessageReloadScrollIntent scrollIntent,
        Func<MessageReloadContext, Task> reloadAsync)
    {
        _hideMessages(scrollIntent);
        _prepareReload();

        try
        {
            if (!_hasMessageDatabase(conversation))
            {
                _showMessagesImmediately();
                return;
            }

            await _waitForRefreshFrameAsync();

            var context = new MessageReloadContext(conversation, loadVersion, _isCurrent);
            if (!context.IsCurrent)
                return;

            await reloadAsync(context);
        }
        catch
        {
            if (_isCurrentVersion(loadVersion))
                _showMessagesImmediately();
        }
    }
}
