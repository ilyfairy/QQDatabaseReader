using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using QQDatabaseReader.Database;
using QQDatabaseReader.Sqlite;
using QQPinyinLetter;

namespace QQDatabaseReader;

public class QQGroupMessageFtsReader : IQQDatabase
{
    public QQGroupMessageFtsDbContext DbContext { get; private set; } = null!;
    public GroupMessageFtsSearchService SearchService { get; private set; } = null!;

    public RawDatabase RawDatabase { get; }
    public QQDatabaseType DatabaseType => QQDatabaseType.GroupMessageFts;
    public string DatabaseFilePath => RawDatabase.DatabaseFilePath;

    public QQGroupMessageFtsReader(string databaseFilePath, bool useVFS = false)
    {
        RawDatabase = new(databaseFilePath, useVFS);
    }

    public QQGroupMessageFtsReader(
        string databaseFilePath,
        string cipherPassword,
        HashAlgorithmName? cipherKdfAlgorithm = null,
        HashAlgorithmName? cipherHmacAlgorithm = null,
        int cipherPageSize = 4096,
        int cipherKdfIter = 4000,
        bool useVFS = true)
    {
        RawDatabase = new(databaseFilePath, cipherPassword, cipherKdfAlgorithm, cipherHmacAlgorithm, cipherPageSize, cipherKdfIter, useVFS);
    }

    public void Initialize()
    {
        RawDatabase.Initialize();
        QQPinyinLetterTokenizer.Register(RawDatabase.Database.DangerousGetHandle(), "e_sqlcipher");

        var connection = new QQNTDbConnection(RawDatabase.Database, RawDatabase.DatabaseFilePath);
        var optionsBuilder = new DbContextOptionsBuilder<QQGroupMessageFtsDbContext>();
        optionsBuilder.UseSqlite(connection).UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        DbContext = new QQGroupMessageFtsDbContext(optionsBuilder.Options);
        SearchService = new GroupMessageFtsSearchService(DbContext);
    }

    public IReadOnlyList<GroupMessageFtsSearchResult> Search(GroupMessageFtsSearchRequest request)
    {
        return SearchService.Search(request);
    }

    public long Count(GroupMessageFtsCountRequest request)
    {
        return SearchService.Count(request);
    }

    public IReadOnlyList<GroupMessageFtsMatchScanRow> ScanMatches(GroupMessageFtsMatchScanRequest request)
    {
        return SearchService.ScanMatches(request);
    }

    public GroupMessageFtsMatchRowIdPage ScanMatchRowIds(GroupMessageFtsMatchRowIdScanRequest request)
    {
        return SearchService.ScanMatchRowIds(request);
    }

    public void Dispose()
    {
        DbContext.Dispose();
        RawDatabase.Dispose();
    }
}
