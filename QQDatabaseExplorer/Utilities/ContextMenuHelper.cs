using Avalonia.Controls;

namespace QQDatabaseExplorer.Utilities;

public static class ContextMenuHelper
{
    public static void Open(Control owner, ContextMenu contextMenu)
    {
        var previousContextMenu = owner.ContextMenu;
        contextMenu.Closed += (_, _) =>
        {
            if (ReferenceEquals(owner.ContextMenu, contextMenu))
                owner.ContextMenu = previousContextMenu;
        };

        owner.ContextMenu = contextMenu;
        contextMenu.Open(owner);
    }
}
