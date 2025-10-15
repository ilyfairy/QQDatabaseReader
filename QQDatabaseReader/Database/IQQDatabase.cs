namespace QQDatabaseReader.Database;

public interface IQQDatabase : IDisposable
{
    QQDatabaseType DatabaseType { get; }
    string DatabaseFilePath { get; }
    RawDatabase RawDatabase { get; }

}
