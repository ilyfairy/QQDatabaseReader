using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class MessagePageDisplayBuilder
{
    private readonly Action<AvaQQGroup, MessagePage> _cacheMessagePage;
    private readonly Action<AvaQQGroup, IEnumerable<MessageRecord>, IReadOnlyList<MessageRecord>?> _cacheSenderInfos;
    private readonly Func<MessageRecord, AvaQQGroup, IReadOnlyDictionary<ReplyTargetKey, MessageRecord>?, AvaQQMessage> _createMessage;

    public MessagePageDisplayBuilder(
        Action<AvaQQGroup, MessagePage> cacheMessagePage,
        Action<AvaQQGroup, IEnumerable<MessageRecord>, IReadOnlyList<MessageRecord>?> cacheSenderInfos,
        Func<MessageRecord, AvaQQGroup, IReadOnlyDictionary<ReplyTargetKey, MessageRecord>?, AvaQQMessage> createMessage)
    {
        _cacheMessagePage = cacheMessagePage;
        _cacheSenderInfos = cacheSenderInfos;
        _createMessage = createMessage;
    }

    public Task<AvaQQMessage[]> CreateAsync(AvaQQGroup conversation, MessagePage page)
    {
        _cacheMessagePage(conversation, page);
        return CreateMessagesAsync(conversation, page.Messages, page.ReplyTargetMessages);
    }

    public Task<AvaQQMessage[]> CreateAsync(
        AvaQQGroup conversation,
        IReadOnlyList<MessageRecord> messages,
        IReadOnlyList<MessageRecord>? referencedMessages,
        IReadOnlyDictionary<ReplyTargetKey, MessageRecord> replyTargetMessages)
    {
        _cacheSenderInfos(conversation, messages, referencedMessages);
        return CreateMessagesAsync(conversation, messages, replyTargetMessages);
    }

    private Task<AvaQQMessage[]> CreateMessagesAsync(
        AvaQQGroup conversation,
        IReadOnlyList<MessageRecord> messages,
        IReadOnlyDictionary<ReplyTargetKey, MessageRecord>? replyTargetMessages)
    {
        return Task.Run(() => messages
            .Select(message => _createMessage(message, conversation, replyTargetMessages))
            .ToArray());
    }
}
