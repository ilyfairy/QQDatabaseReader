using QQDatabaseExplorer.Models;
using QQDatabaseReader;

namespace QQDatabaseExplorer.Services;

internal sealed record AndroidMobileQQDatabaseRuntimeGroup(
    AndroidMobileQQMessageReader? MessageDatabase,
    string? MobileQQPath)
{
    public static AndroidMobileQQDatabaseRuntimeGroup Empty { get; } = new(null, null);

    public static AndroidMobileQQDatabaseRuntimeGroup Create(
        AndroidMobileQQMessageReader messageDatabase,
        string? mobileQQPath)
    {
        return new AndroidMobileQQDatabaseRuntimeGroup(
            messageDatabase,
            NormalizeOptionalPath(mobileQQPath));
    }

    public DatabaseConfig? CreateConfig()
    {
        if (MessageDatabase is not { } messageDatabase)
            return null;

        return new DatabaseConfig
        {
            Type = DatabasePlatformType.AndroidMobileQQ,
            AndroidMobileQQ = new AndroidMobileQQDatabaseConfig
            {
                RootPath = messageDatabase.RootPath,
                SelfUin = messageDatabase.SelfUin,
                MobileQQPath = MobileQQPath,
            },
        };
    }

    private static string? NormalizeOptionalPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : path;
    }
}
