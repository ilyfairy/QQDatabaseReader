using System.Threading.Tasks;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.ViewModels;

internal sealed class ConversationSearchResultNavigator
{
    private readonly MessageTabViewModel _messageTabViewModel;
    private readonly MainViewModel _mainViewModel;

    public ConversationSearchResultNavigator(
        MessageTabViewModel messageTabViewModel,
        MainViewModel mainViewModel)
    {
        _messageTabViewModel = messageTabViewModel;
        _mainViewModel = mainViewModel;
    }

    public async Task OpenAsync(AvaGroupMessageSearchResult result, bool clearMessageFilter)
    {
        _mainViewModel.ShowMessageTab();

        if (!result.CanLocate)
            return;

        switch (result.ConversationType)
        {
            case AvaConversationType.Icalingua:
                if (clearMessageFilter)
                {
                    await _messageTabViewModel.JumpToIcalinguaMessageAndClearFilterAsync(
                        result.IcalinguaRoomId,
                        result.MessageId,
                        result.MessageSeq,
                        result.GroupName);
                }
                else
                {
                    await _messageTabViewModel.JumpToIcalinguaMessageAsync(
                        result.IcalinguaRoomId,
                        result.MessageId,
                        result.MessageSeq,
                        result.GroupName);
                }

                return;
            case AvaConversationType.PCQQGroup or AvaConversationType.PCQQPrivate:
                await _messageTabViewModel.JumpToPCQQMessageAsync(
                    result.ConversationType,
                    result.GroupId,
                    result.PeerUin,
                    result.PCQQTableName,
                    result.MessageId,
                    result.MessageSeq,
                    result.GroupName,
                    clearMessageFilter);
                return;
            case AvaConversationType.AndroidMobileQQGroup or AvaConversationType.AndroidMobileQQPrivate:
                await _messageTabViewModel.JumpToAndroidMobileQQMessageAsync(
                    result.ConversationType,
                    result.AndroidMobileQQPeerUin,
                    result.AndroidMobileQQTableName,
                    result.MessageId,
                    result.MessageSeq,
                    result.GroupName,
                    clearMessageFilter);
                return;
        }

        if (result.PrivateConversationId != 0)
        {
            if (clearMessageFilter)
            {
                await _messageTabViewModel.JumpToPrivateMessageAndClearFilterAsync(
                    result.PrivateConversationId,
                    result.MessageId,
                    result.MessageSeq,
                    result.GroupName,
                    result.PeerUin,
                    result.PeerUid);
            }
            else
            {
                await _messageTabViewModel.JumpToPrivateMessageAsync(
                    result.PrivateConversationId,
                    result.MessageId,
                    result.MessageSeq,
                    result.GroupName,
                    result.PeerUin,
                    result.PeerUid);
            }

            return;
        }

        if (clearMessageFilter)
        {
            await _messageTabViewModel.JumpToMessageAndClearFilterAsync(
                result.GroupId,
                result.MessageId,
                result.MessageSeq,
                result.GroupName);
        }
        else
        {
            await _messageTabViewModel.JumpToMessageAsync(
                result.GroupId,
                result.MessageId,
                result.MessageSeq,
                result.GroupName);
        }
    }
}
