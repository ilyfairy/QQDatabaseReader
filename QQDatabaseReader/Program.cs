using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QQDatabaseReader;
using QQDatabaseReader.Database;
using QQDatabaseReader.Sqlite;
using SQLitePCL;

Console.OutputEncoding = Encoding.UTF8;

using var messageDb = new QQMessageReader(@"Z:\nt_msg.db");

messageDb.Initialize();

foreach (var item in messageDb.DbContext.GroupMessages)
{
    Console.WriteLine(item.GetText());
}
