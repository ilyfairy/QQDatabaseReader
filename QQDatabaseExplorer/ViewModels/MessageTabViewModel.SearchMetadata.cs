namespace QQDatabaseExplorer.ViewModels;

public partial class MessageTabViewModel
{
    internal string ResolveProfileDisplayNameForSearch(uint uin, string? ntUid, string fallback)
    {
        return _participantResolver.ResolveProfileDisplayName(uin, ntUid, fallback);
    }
}
