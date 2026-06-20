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

        if (result.IcalinguaRoomId != 0)
        {
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
