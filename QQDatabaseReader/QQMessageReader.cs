using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Google.Protobuf;
using QQDatabaseReader.Sqlite;
using QQDatabaseReader.Database;

namespace QQDatabaseReader;

public class QQGroupInfoReader : IQQDatabase
{
    public QQGroupInfoDbContext DbContext { get; private set; } = null!;

    public RawDatabase RawDatabase { get; }
    public QQDatabaseType DatabaseType => QQDatabaseType.GroupInfo;
    public string DatabaseFilePath => RawDatabase.DatabaseFilePath;

    public QQGroupInfoReader(string databaseFilePath, bool useVFS = false)
    {
        RawDatabase = new(databaseFilePath, useVFS);
    }

    public QQGroupInfoReader(string databaseFilePath, string cipherPassword, HashAlgorithmName? cipherKdfAlgorithm = null, HashAlgorithmName? cipherHmacAlgorithm = null, int cipherPageSize = 4096, int cipherKdfIter = 4000, bool useVFS = true)
    {
        RawDatabase = new(databaseFilePath, cipherPassword, cipherKdfAlgorithm, cipherHmacAlgorithm, cipherPageSize, cipherKdfIter, useVFS);
    }

    public void Initialize()
    {
        RawDatabase.Initialize();
        var connection = new QQNTDbConnection(RawDatabase.Database, RawDatabase.DatabaseFilePath);
        var optionsBuilder = new DbContextOptionsBuilder<QQGroupInfoDbContext>();
        optionsBuilder.UseSqlite(connection).UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        DbContext = new QQGroupInfoDbContext(optionsBuilder.Options);
    }

    public void Dispose()
    {

    }
}

public class QQMessageReader : IQQDatabase
{
    public QQMessageDbContext DbContext { get; private set; } = null!;

    public RawDatabase RawDatabase { get; init; }
    public QQDatabaseType DatabaseType => QQDatabaseType.Message;

    public string DatabaseFilePath => RawDatabase.DatabaseFilePath;

    public QQMessageReader(string databaseFilePath, bool useVFS = false)
    {
        RawDatabase = new(databaseFilePath, useVFS);
    }

    public QQMessageReader(string databaseFilePath, string cipherPassword, HashAlgorithmName? cipherKdfAlgorithm = null, HashAlgorithmName? cipherHmacAlgorithm = null, int cipherPageSize = 4096, int cipherKdfIter = 4000, bool useVFS = true)
    {
        RawDatabase = new(databaseFilePath, cipherPassword, cipherKdfAlgorithm, cipherHmacAlgorithm, cipherPageSize, cipherKdfIter, useVFS);
    }

    public void Initialize()
    {
        RawDatabase.Initialize();
        var connection = new QQNTDbConnection(RawDatabase.Database, RawDatabase.DatabaseFilePath);
        var optionsBuilder = new DbContextOptionsBuilder<QQMessageDbContext>();
        optionsBuilder.UseSqlite(connection).UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        DbContext = new QQMessageDbContext(optionsBuilder.Options);
    }


    public void Dispose()
    {
        DbContext.Dispose();
        RawDatabase.Dispose();
    }


    public static QQMessageContent ParseMessage(byte[] protobufMessage)
    {
        var root = new CodedInputStream(protobufMessage);
        QQMessageSegment? firstMessage = null;
        List<QQMessageSegment>? messages = null;

        while (!root.IsAtEnd)
        {
            int key = WireFormat.GetTagFieldNumber(root.ReadTag());

            if (key != 40800)
                break;

            var currentMessage = new QQMessageSegment();
            var input = new CodedInputStream(root.ReadBytes().ToByteArray());

            while (!input.IsAtEnd)
            {
                uint tag = input.ReadTag();
                if (tag == 0)
                    break;

                key = WireFormat.GetTagFieldNumber(tag);
                var strKey = key.ToString();
                WireFormat.WireType wireType = WireFormat.GetTagWireType(tag);

                // 直接使用字段编号作为 key
                object? value = null;

                if (key == 45001) // 消息段 ID
                {
                    currentMessage.Id = input.ReadInt32();
                }
                else if (key == 45002)
                {
                    currentMessage.Type = (MessageSegmentType)input.ReadInt32();
                }
                else if (key == 45101) // 消息文本
                {
                    currentMessage.Text = input.ReadBytes().ToStringUtf8();
                }
                else if (key == 45402)
                {
                    currentMessage.ImageFileName = input.ReadBytes().ToStringUtf8();
                }
                else if (key == 45503)
                {
                    currentMessage.ImageRKey = input.ReadBytes().ToStringUtf8();
                }
                else if (key == 45816)
                {
                    currentMessage.ImageHost = input.ReadBytes().ToStringUtf8();
                }
                else if (key == 45815)
                {
                    currentMessage.AltText += input.ReadBytes().ToStringUtf8();
                }
                else
                {
                    try
                    {
                        switch (wireType)
                        {
                            case WireFormat.WireType.Varint:
                                // 可能是 int32, int64, uint32, uint64, bool, enum
                                value = input.ReadInt64();
                                break;

                            case WireFormat.WireType.Fixed64:
                                // 可能是 fixed64, sfixed64, double
                                value = input.ReadFixed64();
                                break;

                            case WireFormat.WireType.LengthDelimited:
                                // 可能是 string, bytes, 嵌套消息, packed repeated
                                var bytes = input.ReadBytes();
                                //value = ParseLengthDelimited(bytes);
                                break;

                            case WireFormat.WireType.Fixed32:
                                // 可能是 fixed32, sfixed32, float
                                value = input.ReadFixed32();
                                break;

                            case WireFormat.WireType.StartGroup:
                            case WireFormat.WireType.EndGroup:
                                // 已废弃的 group 类型
                                value = "<deprecated group>";
                                break;

                            default:
                                value = "<unknown>";
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        value = $"<parse error: {ex.Message}>";
                    }
                }

                //// 如果同一个字段出现多次（repeated），转为数组
                //if (singleMessage.ContainsKey(strKey))
                //{
                //    var oldValue = singleMessage[strKey];
                //    if (oldValue is JsonArray jsonArray)
                //    {
                //        jsonArray.Add(JsonValue.Create(value));
                //    }
                //    else if (oldValue is null)
                //    {
                //        var newValue = JsonValue.Create(value);
                //        singleMessage[strKey] = new JsonArray(null, newValue);
                //    }
                //    else
                //    {
                //        var newValue = JsonValue.Create(value);
                //        singleMessage[strKey] = new JsonArray(oldValue.DeepClone(), newValue);
                //    }
                //}
                //else
                //{
                //    singleMessage[strKey] = JsonValue.Create(value);
                //}
            }

            if (firstMessage == null)
            {
                firstMessage = currentMessage;
            }
            else if (messages == null)
            {
                messages = [firstMessage, currentMessage];
            }
            else
            {
                messages.Add(currentMessage);
            }
        }

        if (firstMessage is null)
            throw new Exception("消息解析失败");

        if (messages is null)
        {
            return new QQMessageContent(firstMessage);
        }
        else
        {
            return new QQMessageContent(messages);
        }
        //return new QQMessage()
        //{
        //    Content = message,
        //};
    }
}


public class QQMessageContent
{
    public QQMessageSegment FirstSegment { get; set; }
    public IReadOnlyList<QQMessageSegment>? Segments { get; set; }

    public QQMessageContent(params IReadOnlyList<QQMessageSegment> segments)
    {
        Segments = segments;
        FirstSegment = segments[0];
    }

    public QQMessageContent(QQMessageSegment firstSegment)
    {
        FirstSegment = firstSegment;
    }

    public string GetText()
    {
        if (Segments == null)
        {
            return FirstSegment.Text ?? string.Empty;
        }
        else
        {
            return string.Concat(Segments.Select(v => v.Text ?? v.AltText ?? $"{v.Type}"));
        }
    }

    //public required JsonNode Content { get; init; }

    //public string GetText()
    //{
    //    if (Content is JsonObject jsonObject)
    //    {
    //        if (jsonObject.ContainsKey("45101"))
    //        {
    //            return jsonObject["45101"].GetValue<string>();
    //        }
    //        else
    //        {

    //        }
    //    }
    //    else if (Content is JsonArray jsonArray)
    //    {
    //        StringBuilder s = new();
    //        foreach (JsonObject item in jsonArray)
    //        {
    //            if (item.ContainsKey("45101"))
    //            {
    //                s.Append(item["45101"].GetValue<string>());
    //            }
    //            else
    //            {

    //            }
    //        }
    //        return s.ToString();
    //    }

    //    return "";
    //}
}

public class QQMessageSegment
{
    /// <summary>
    /// 45001
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// 45002
    /// </summary>
    public MessageSegmentType Type { get; set; }

    /// <summary>
    /// 45101
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// 45402
    /// </summary>
    public string? ImageFileName { get; set; }

    /// <summary>
    /// 45503
    /// </summary>
    public string? ImageRKey { get; set; }

    /// <summary>
    /// 45816
    /// </summary>
    public string? ImageHost { get; set; }

    /// <summary>
    /// 45815
    /// </summary>
    public string? AltText { get; set; }
}

public enum MessageSegmentType : int
{
    Text = 1,
    Image = 2,
    File = 3,
    Record = 4,
    Emoji = 6,
    Reply = 7,
    System = 8,
    App = 9,
    Emoji2 = 11,
    Xml = 16,
    Call = 21,
    Dynamic = 26,
}