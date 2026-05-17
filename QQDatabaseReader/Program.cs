using System.Text;
using Microsoft.EntityFrameworkCore;
using QQDatabaseReader;
using QQDatabaseReader.Sqlite;
using SQLitePCL;

class Program
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        raw.SetProvider(new SQLite3Provider_e_sqlcipher());

        if (args is ["pcqq-decrypt", var encryptedDb, var key, ..])
        {
            var output = args.Length >= 4
                ? args[3]
                : Path.ChangeExtension(encryptedDb, ".plain.db");

            PcqqDatabaseDecryptor.DecryptToFile(encryptedDb, output, PcqqDatabaseDecryptor.ParseKey(key));
            Console.WriteLine($"decrypted -> {output}");
            return 0;
        }

        if (args is ["pcqq-schema", var encryptedDbForSchema, var schemaKey, ..])
        {
            var markdown = args.Length >= 4
                ? args[3]
                : Path.ChangeExtension(encryptedDbForSchema, ".schema.md");

            PCQQPageDecryptVfs.Register(schemaKey);
            DumpSchemaMarkdown(encryptedDbForSchema, markdown, PCQQPageDecryptVfs.VfsName);
            Console.WriteLine($"schema -> {markdown}");
            return 0;
        }

        if (args is ["pcqq-ef-test", var encryptedDbForEf, var efKey, ..])
        {
            using var rawDatabase = RawDatabase.OpenPCQQ(encryptedDbForEf, efKey);
            rawDatabase.Initialize();

            using var connection = new QQNTDbConnection(rawDatabase.Database, rawDatabase.DatabaseFilePath);
            connection.Open();

            var options = new DbContextOptionsBuilder<DbContext>()
                .UseSqlite(connection)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .Options;

            using var dbContext = new DbContext(options);
            var tableCount = dbContext.Database
                .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table'")
                .Single();

            Console.WriteLine($"EF Core table count = {tableCount}");
            return 0;
        }

        if (args is ["pcqq-read-test", var encryptedDbForRead, var readKey, ..])
        {
            string? infoDbPath = args.Length >= 5 ? args[3] : null;
            string? infoDbKey = args.Length >= 5 ? args[4] : null;
            using var reader = new PCQQMessageReader(encryptedDbForRead, readKey, infoDbPath, infoDbKey);
            reader.Initialize();

            var conversations = reader.GetConversations();
            Console.WriteLine($"Conversations = {conversations.Count}");
            foreach (var conversation in conversations.Take(10))
            {
                Console.WriteLine($"{conversation.ConversationType} peer={conversation.PeerId} raw={conversation.RawPeerId} name={conversation.DisplayName} groupId={conversation.InfoGroupId} groupCode={conversation.InfoGroupCode} {conversation.LatestMessageTime}: {conversation.LatestMessageText}");

                foreach (var message in reader.LoadLatestMessages(conversation.TableName, 3))
                {
                    var text = PCQQMessageContentParser.GetDisplayText(message.Content);
                    var sender = string.IsNullOrWhiteSpace(message.SenderNickname)
                        ? message.SenderUin.ToString()
                        : $"{message.SenderNickname}({message.SenderUin})";
                    Console.WriteLine($"  {message.MessageTime}/{message.MessageRandom} {sender}: {text}");
                }
            }

            return 0;
        }

        if (args is ["pcqq-read-table", var encryptedDbForTableRead, var tableReadKey, var tableName, ..])
        {
            string? infoDbPath = args.Length >= 6 ? args[4] : null;
            string? infoDbKey = args.Length >= 6 ? args[5] : null;
            using var reader = new PCQQMessageReader(encryptedDbForTableRead, tableReadKey, infoDbPath, infoDbKey);
            reader.Initialize();

            foreach (var message in reader.LoadLatestMessages(tableName, 10))
            {
                var text = PCQQMessageContentParser.GetDisplayText(message.Content);
                var sender = string.IsNullOrWhiteSpace(message.SenderNickname)
                    ? message.SenderUin.ToString()
                    : $"{message.SenderNickname}({message.SenderUin})";
                Console.WriteLine($"{message.MessageTime}/{message.MessageRandom} {sender}: {text}");
            }

            return 0;
        }

        if (args is ["pcqq-info-test", var infoDb, var infoKey, ..])
        {
            var reader = new PCQQInfoDbReader(infoDb, infoKey);
            var streams = reader.ReadPlainStreams();
            var groups = reader.GetGroups();
            var contacts = reader.GetContacts();
            Console.WriteLine($"Info.db streams = {streams.Count}");
            Console.WriteLine($"Groups = {groups.Count}");
            Console.WriteLine($"Contacts = {contacts.Count}");
            foreach (var group in groups.Take(30))
            {
                Console.WriteLine($"groupId={group.GroupId} groupCode={group.GroupCode} name={group.GroupName}");
            }

            return 0;
        }

        if (args is ["pcqq-info-contact", var contactInfoDb, var contactInfoKey, var uinText, ..])
        {
            if (!uint.TryParse(uinText, out var uin))
            {
                Console.WriteLine($"invalid uin: {uinText}");
                return 1;
            }

            var reader = new PCQQInfoDbReader(contactInfoDb, contactInfoKey);
            if (!reader.TryGetContact(uin, out var contact))
            {
                Console.WriteLine($"contact not found: {uin}");
                return 1;
            }

            Console.WriteLine($"uin={contact.Uin}");
            Console.WriteLine($"nickname={contact.Nickname}");
            Console.WriteLine($"remark={contact.RemarkName}");
            Console.WriteLine($"display={contact.DisplayName}");
            return 0;
        }

        if (args is ["pcqq-info-dump", var dumpInfoDb, var dumpInfoKey, var streamName, ..])
        {
            var reader = new PCQQInfoDbReader(dumpInfoDb, dumpInfoKey);
            if (!reader.ReadPlainStreams().TryGetValue(streamName, out var stream))
            {
                Console.WriteLine($"stream not found: {streamName}");
                return 1;
            }

            var limit = args.Length >= 5 && int.TryParse(args[4], out var parsedLimit) ? parsedLimit : 30;
            var objects = PCQQInfoDbReader.ParseTxDataObjects(stream).Take(limit).ToArray();
            Console.WriteLine($"stream={streamName} objects={objects.Length}");
            foreach (var obj in objects)
            {
                Console.WriteLine();
                Console.WriteLine($"TD @0x{obj.Offset:X} fields={obj.Fields.Count}");
                foreach (var field in obj.Fields.Take(40))
                {
                    Console.WriteLine($"  {field.Key,-32} type=0x{field.Value.Type:X2} value={FormatInfoValue(field.Value.Value)}");
                }
            }

            return 0;
        }

        Console.WriteLine("Usage:");
        Console.WriteLine("  QQDatabaseReader pcqq-decrypt   <Msg3.0.db> <hex-or-ascii-key> [output.plain.db]");
        Console.WriteLine("  QQDatabaseReader pcqq-schema    <Msg3.0.db> <hex-or-ascii-key> [schema.md]");
        Console.WriteLine("  QQDatabaseReader pcqq-ef-test   <Msg3.0.db> <hex-or-ascii-key>");
        Console.WriteLine("  QQDatabaseReader pcqq-read-test <Msg3.0.db> <hex-or-ascii-key> [Info.db Info.db-key]");
        Console.WriteLine("  QQDatabaseReader pcqq-read-table <Msg3.0.db> <hex-or-ascii-key> <table-name> [Info.db Info.db-key]");
        Console.WriteLine("  QQDatabaseReader pcqq-info-test <Info.db> <hex-or-ascii-key>");
        Console.WriteLine("  QQDatabaseReader pcqq-info-contact <Info.db> <hex-or-ascii-key> <uin>");
        Console.WriteLine("  QQDatabaseReader pcqq-info-dump <Info.db> <hex-or-ascii-key> <stream-name> [limit]");
        return 2;


    }


    static void DumpSchemaMarkdown(string sqlitePath, string markdownPath, string? vfsName = null)
    {
        var rc = raw.sqlite3_open_v2(sqlitePath, out sqlite3 db, raw.SQLITE_OPEN_READONLY, vfsName);
        if (rc != raw.SQLITE_OK)
            throw new InvalidOperationException($"sqlite3_open_v2 failed: {raw.sqlite3_errmsg(db).utf8_to_string()}");

        try
        {
            var tables = Query(db, "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")
                .Select(row => row.GetValueOrDefault("name") ?? "")
                .Where(name => name.Length > 0)
                .ToArray();

            var builder = new StringBuilder();
            builder.AppendLine("# Msg3.0.db Schema");
            builder.AppendLine();
            builder.AppendLine($"- Source: `{Path.GetFileName(sqlitePath)}`");
            builder.AppendLine($"- Tables: {tables.Length}");
            builder.AppendLine();

            foreach (var table in tables)
            {
                builder.AppendLine($"## Table {table}");
                builder.AppendLine();
                builder.AppendLine("### Columns");
                builder.AppendLine();
                builder.AppendLine("| cid | name | type | pk |");
                builder.AppendLine("| --- | --- | --- | --- |");

                foreach (var column in Query(db, $"PRAGMA table_info({QuoteIdent(table)})"))
                {
                    builder.Append("| ")
                        .Append(EscapeMd(column.GetValueOrDefault("cid"))).Append(" | ")
                        .Append(EscapeMd(column.GetValueOrDefault("name"))).Append(" | ")
                        .Append(EscapeMd(column.GetValueOrDefault("type"))).Append(" | ")
                        .Append(EscapeMd(column.GetValueOrDefault("pk"))).AppendLine(" |");
                }

                builder.AppendLine();
                builder.AppendLine("### Indexes");
                builder.AppendLine();
                builder.AppendLine("| name | unique | columns |");
                builder.AppendLine("| --- | --- | --- |");

                var indexes = Query(db, $"PRAGMA index_list({QuoteIdent(table)})").ToArray();
                if (indexes.Length == 0)
                {
                    builder.AppendLine("| _none_ | no | |");
                }
                else
                {
                    foreach (var index in indexes)
                    {
                        var indexName = index.GetValueOrDefault("name") ?? "";
                        var unique = index.GetValueOrDefault("unique") == "1" ? "yes" : "no";
                        var columns = string.Join(", ", Query(db, $"PRAGMA index_info({QuoteIdent(indexName)})")
                            .OrderBy(row => int.TryParse(row.GetValueOrDefault("seqno"), out var seq) ? seq : int.MaxValue)
                            .Select(row => row.GetValueOrDefault("name") ?? $"cid:{row.GetValueOrDefault("cid")}"));

                        builder.Append("| ")
                            .Append(EscapeMd(indexName)).Append(" | ")
                            .Append(unique).Append(" | ")
                            .Append(EscapeMd(columns)).AppendLine(" |");
                    }
                }

                builder.AppendLine();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(markdownPath))!);
            File.WriteAllText(markdownPath, builder.ToString(), new UTF8Encoding(false));
        }
        finally
        {
            raw.sqlite3_close(db);
        }
    }

    static List<Dictionary<string, string?>> Query(sqlite3 db, string sql)
    {
        var rc = raw.sqlite3_prepare_v2(db, sql, out sqlite3_stmt statement);
        if (rc != raw.SQLITE_OK)
            throw new InvalidOperationException($"prepare failed: {raw.sqlite3_errmsg(db).utf8_to_string()}; sql={sql}");

        try
        {
            var rows = new List<Dictionary<string, string?>>();
            while ((rc = raw.sqlite3_step(statement)) == raw.SQLITE_ROW)
            {
                var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                var columnCount = raw.sqlite3_column_count(statement);
                for (var i = 0; i < columnCount; i++)
                {
                    var name = raw.sqlite3_column_name(statement, i).utf8_to_string();
                    row[name] = raw.sqlite3_column_text(statement, i).utf8_to_string();
                }

                rows.Add(row);
            }

            if (rc != raw.SQLITE_DONE)
                throw new InvalidOperationException($"step failed: {raw.sqlite3_errmsg(db).utf8_to_string()}; sql={sql}");

            return rows;
        }
        finally
        {
            raw.sqlite3_finalize(statement);
        }
    }

    static string QuoteIdent(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    static string EscapeMd(string? value) => (value ?? "").Replace("|", "\\|").Replace("`", "\\`");

    static string FormatInfoValue(object? value)
    {
        return value switch
        {
            null => "<null>",
            string text => text.Length <= 120 ? text : text[..120] + "...",
            byte[] bytes => $"bytes[{bytes.Length}] {Convert.ToHexString(bytes.AsSpan(0, Math.Min(24, bytes.Length)))}",
            IReadOnlyList<object?> list => $"array[{list.Count}]",
            PCQQTxDataObject obj => $"TD fields={obj.Fields.Count}",
            _ => value.ToString() ?? string.Empty,
        };
    }
}