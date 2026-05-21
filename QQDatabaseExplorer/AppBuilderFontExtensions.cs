using Avalonia;

using QQDatabaseExplorer.Fonts;

namespace QQDatabaseExplorer;

public static class AppBuilderFontExtensions
{
    public static AppBuilder WithQQDatabaseExplorerFonts(this AppBuilder appBuilder)
    {
        return appBuilder.ConfigureFonts(fontManager =>
        {
            fontManager.AddFontCollection(new EmojiFontCollection());
        });
    }
}
