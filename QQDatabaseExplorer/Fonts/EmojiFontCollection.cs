using System;

using Avalonia.Media.Fonts;

namespace QQDatabaseExplorer.Fonts;

public sealed class EmojiFontCollection : EmbeddedFontCollection
{
    public EmojiFontCollection() : base(
        new Uri("fonts:QQDatabaseExplorerEmoji", UriKind.Absolute),
        new Uri("avares://QQDatabaseExplorer/Assets/Fonts", UriKind.Absolute))
    {
    }
}
