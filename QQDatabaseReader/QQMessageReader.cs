using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
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

public partial class QQMessageReader : IQQDatabase
{
    private const int MaxForwardedMessageDepth = 8;

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

}

public class QQAndroidMessageReader : IQQDatabase
{
    public QQAndroidMessageDbContext DbContext { get; private set; } = null!;

    public RawDatabase RawDatabase { get; init; }
    public QQDatabaseType DatabaseType => QQDatabaseType.AndroidMessage;

    public string DatabaseFilePath => RawDatabase.DatabaseFilePath;

    public QQAndroidMessageReader(string databaseFilePath, bool useVFS = false)
    {
        RawDatabase = new(databaseFilePath, useVFS);
    }

    public QQAndroidMessageReader(
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
        var connection = new QQNTDbConnection(RawDatabase.Database, RawDatabase.DatabaseFilePath);
        var optionsBuilder = new DbContextOptionsBuilder<QQAndroidMessageDbContext>();
        optionsBuilder.UseSqlite(connection).UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        DbContext = new QQAndroidMessageDbContext(optionsBuilder.Options);
    }

    public void Dispose()
    {
        DbContext.Dispose();
        RawDatabase.Dispose();
    }
}

public partial class QQMessageReader
{
    public static QQMessageContent ParseMessage(byte[] protobufMessage)
    {
        QQMessageSegment? firstMessage = null;
        List<QQMessageSegment>? messages = null;
        var position = 0;

        while (position < protobufMessage.Length)
        {
            if (!TryReadVarint32(protobufMessage, ref position, out var tag))
                break;

            int key = WireFormat.GetTagFieldNumber(tag);

            if (key != 40800 || WireFormat.GetTagWireType(tag) != WireFormat.WireType.LengthDelimited)
                break;

            if (!TryReadVarint32(protobufMessage, ref position, out var segmentLength))
                break;

            var remaining = protobufMessage.Length - position;
            var safeLength = segmentLength <= remaining ? (int)segmentLength : Math.Max(0, remaining);
            if (safeLength == 0)
                break;

            var currentMessage = ParseMessageSegment(protobufMessage.AsSpan(position, safeLength).ToArray());
            position += safeLength;

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

    private static bool TryReadVarint32(ReadOnlySpan<byte> data, ref int position, out uint value)
    {
        value = 0;
        var result = 0u;
        var shift = 0;

        while (position < data.Length && shift < 32)
        {
            var current = data[position++];
            result |= (uint)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                value = result;
                return true;
            }

            shift += 7;
        }

        return false;
    }

    public static QQMessageSegment ParseMessageSegment(byte[] segmentMessage)
    {
        var currentMessage = new QQMessageSegment();
        var input = new CodedInputStream(segmentMessage);

        while (!input.IsAtEnd)
        {
            uint tag;
            try
            {
                tag = input.ReadTag();
            }
            catch (Exception ex)
            {
                currentMessage.ParseError = ex.Message;
                break;
            }

            if (tag == 0)
                break;

            var key = WireFormat.GetTagFieldNumber(tag);

            if (key == 45001) // 消息段 ID
            {
                currentMessage.Id = input.ReadInt64();
            }
            else if (key == 45002)
            {
                currentMessage.Type = (MessageSegmentType)input.ReadInt32();
            }
            else if (key == 45003)
            {
                currentMessage.FaceType = input.ReadInt32();
            }
            else if (key == 40020)
            {
                currentMessage.SenderUid = input.ReadBytes().ToStringUtf8();
            }
            else if (key == 40021)
            {
                currentMessage.PeerUid = input.ReadBytes().ToStringUtf8();
            }
            else if (key == 45101) // 消息文本
            {
                currentMessage.Text = input.ReadBytes().ToStringUtf8();
            }
            else if (key == 45402)
            {
                currentMessage.ImageFileName = input.ReadBytes().ToStringUtf8();
            }
            else if (key == 45405)
            {
                currentMessage.MediaFileSize = input.ReadInt64();
            }
            else if (key == 45406)
            {
                currentMessage.ImageMd5 = input.ReadBytes().ToByteArray();
            }
            else if (key == 45411)
            {
                currentMessage.ImageWidth = input.ReadInt32();
            }
            else if (key == 45412)
            {
                currentMessage.ImageHeight = input.ReadInt32();
            }
            else if (key == 45415)
            {
                currentMessage.VideoDurationMilliseconds = input.ReadInt32();
            }
            else if (key == 45422)
            {
                currentMessage.VideoCoverFileName = input.ReadBytes().ToStringUtf8();
            }
            else if (key == 45503)
            {
                currentMessage.ImageRKey = input.ReadBytes().ToStringUtf8();
            }
            else if (key == 45802)
            {
                currentMessage.ImageThumbnailPath = input.ReadBytes().ToStringUtf8();
            }
            else if (key == 45803)
            {
                currentMessage.ImageLargePath = input.ReadBytes().ToStringUtf8();
            }
            else if (key == 45804)
            {
                currentMessage.ImageOriginalPath = input.ReadBytes().ToStringUtf8();
            }
            else if (key == 45812)
            {
                currentMessage.ImageLocalPath = input.ReadBytes().ToStringUtf8();
            }
            else if (key == 45816)
            {
                currentMessage.ImageHost = input.ReadBytes().ToStringUtf8();
            }
            else if (key == 45815)
            {
                currentMessage.AppendAltText(input.ReadBytes().ToStringUtf8());
            }
            else if (key == 49093)
            {
                currentMessage.AppendAltText(input.ReadBytes().ToStringUtf8());
            }
            else if (key == 47601)
            {
                currentMessage.FaceId = input.ReadInt32();
            }
            else if (key == 80810)
            {
                currentMessage.MarketFacePackageId = input.ReadInt32();
            }
            else if (key == 80900)
            {
                currentMessage.MarketFaceName = input.ReadBytes().ToStringUtf8();
            }
            else if (key == 80903)
            {
                currentMessage.MarketFaceImageIdBytes = input.ReadBytes().ToByteArray();
            }
            else if (key == 80909)
            {
                currentMessage.MarketFaceWidth = input.ReadInt32();
            }
            else if (key == 80910)
            {
                currentMessage.MarketFaceHeight = input.ReadInt32();
            }
            else if (key == 47901)
            {
                currentMessage.AppJson = input.ReadBytes().ToStringUtf8();
            }
            else if (key == 47902)
            {
                currentMessage.AppResid = input.ReadBytes().ToStringUtf8();
            }
            else if (key == 47904)
            {
                currentMessage.AppUniseq = input.ReadBytes().ToStringUtf8();
            }
            else if (key == 48601)
            {
                currentMessage.XmlResid = input.ReadBytes().ToStringUtf8();
            }
            else if (key == 48602)
            {
                currentMessage.Xml = input.ReadBytes().ToStringUtf8();
            }
            else if (key == 48603)
            {
                currentMessage.XmlFileName = input.ReadBytes().ToStringUtf8();
            }
            else if (key == 48210 && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
            {
                ReadSystemHintParticipant(currentMessage, input.ReadBytes().ToByteArray());
            }
            else if (key == 48214 && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
            {
                currentMessage.SystemHint ??= new QQSystemHintMessage();
                var xml = input.ReadBytes().ToStringUtf8();
                currentMessage.SystemHint.Xml = xml;
                ApplyXmlSystemHint(currentMessage, xml);
            }
            else if (key == 48217 && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
            {
                ReadSystemHintProperty(currentMessage, input.ReadBytes().ToByteArray());
            }
            else if (IsRecallSystemHintField(currentMessage, key))
            {
                ReadRecallSystemHintField(currentMessage, key, tag, input);
            }
            else if (key == 48271 && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
            {
                var json = input.ReadBytes().ToStringUtf8();
                currentMessage.SystemHint ??= new QQSystemHintMessage();
                currentMessage.SystemHint.Json = json;
                ApplyJsonSystemHint(currentMessage, json);
            }
            else if (key is 48275 or 48501 or 48503 or 48504 or 48505 or 48506 or 48507 or 48508 or 48511)
            {
                ReadJoinGroupSystemHintField(currentMessage, key, tag, input);
            }
            else if (key is >= 47401 and <= 47499)
            {
                ReadReplyField(currentMessage, key, tag, input);
            }
            else
            {
                try
                {
                    input.SkipLastField();
                }
                catch (Exception ex)
                {
                    currentMessage.ParseError = ex.Message;
                }
            }
        }

        return currentMessage;
    }

    private static bool IsRecallSystemHintField(QQMessageSegment currentMessage, int key)
    {
        if (currentMessage.Type == MessageSegmentType.System &&
            key is >= 47702 and <= 47715)
        {
            return true;
        }

        return key is 49154 or 49155 &&
               string.Equals(currentMessage.SystemHint?.GetProperty("hint_type"), "recall", StringComparison.Ordinal);
    }

    private static void ReadRecallSystemHintField(
        QQMessageSegment currentMessage,
        int key,
        uint tag,
        CodedInputStream input)
    {
        var wireType = WireFormat.GetTagWireType(tag);
        var hint = currentMessage.SystemHint ??= new QQSystemHintMessage();
        hint.SetProperty("hint_type", "recall");

        try
        {
            switch (key)
            {
                case 47702 when wireType == WireFormat.WireType.Varint:
                    hint.SetProperty("recall_subtype", input.ReadUInt64().ToString());
                    break;
                case 47703 when wireType == WireFormat.WireType.LengthDelimited:
                    hint.SetProperty("recall_source_uid", input.ReadBytes().ToStringUtf8());
                    break;
                case 47704 when wireType == WireFormat.WireType.LengthDelimited:
                    hint.SetProperty("recall_target_uid", input.ReadBytes().ToStringUtf8());
                    break;
                case 47705 when wireType == WireFormat.WireType.LengthDelimited:
                    hint.SetProperty("recall_actor_name", input.ReadBytes().ToStringUtf8());
                    break;
                case 47706 when wireType == WireFormat.WireType.LengthDelimited:
                    SetSystemHintPropertyIfMissing(hint, "recalled_text", input.ReadBytes().ToStringUtf8());
                    break;
                case 47710 when wireType == WireFormat.WireType.LengthDelimited:
                {
                    var originalBytes = input.ReadBytes().ToByteArray();
                    hint.RecalledOriginalMessageContent = originalBytes;
                    SetSystemHintPropertyIfMissing(
                        hint,
                        "recalled_text",
                        TryCreateRecallPreviewText(originalBytes));
                    break;
                }
                case 47711 when wireType == WireFormat.WireType.Varint:
                    hint.SetProperty("recall_has_original", input.ReadUInt64().ToString());
                    break;
                case 47713 when wireType == WireFormat.WireType.LengthDelimited:
                    hint.SetProperty("recall_extra", input.ReadBytes().ToStringUtf8());
                    break;
                case 47714 when wireType == WireFormat.WireType.LengthDelimited:
                    SetSystemHintPropertyIfMissing(hint, "recall_actor_name", input.ReadBytes().ToStringUtf8());
                    break;
                case 47715 when wireType == WireFormat.WireType.LengthDelimited:
                    SetSystemHintPropertyIfMissing(hint, "recalled_text", input.ReadBytes().ToStringUtf8());
                    break;
                case 49154 when wireType == WireFormat.WireType.LengthDelimited:
                    hint.SetProperty("recall_flag", input.ReadBytes().ToStringUtf8());
                    break;
                case 49155 when wireType == WireFormat.WireType.Varint:
                    hint.SetProperty("recall_time", input.ReadUInt64().ToString());
                    break;
                default:
                    input.SkipLastField();
                    break;
            }

            ApplyRecallSystemHint(hint);
        }
        catch
        {
            // Keep fields decoded before an unknown/truncated recall payload.
        }
    }

    private static string? TryCreateRecallPreviewText(byte[] data)
    {
        try
        {
            var segment = ParseMessageSegment(data);
            var text = segment.GetDisplayText();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch
        {
        }

        try
        {
            var text = ParseMessage(data).GetText();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyRecallSystemHint(QQSystemHintMessage hint)
    {
        var actorName = FirstNonEmpty(hint.GetProperty("recall_actor_name"));
        var actorUid = FirstNonEmpty(
            hint.GetProperty("recall_source_uid"),
            hint.GetProperty("recall_target_uid"));
        if (!string.IsNullOrWhiteSpace(actorName))
        {
            AddOrUpdateSystemHintParticipant(hint, new QQSystemHintParticipant(actorUid, actorName));
        }

        var recalledText = FirstNonEmpty(hint.RecalledMessageText);
        hint.SetProperty("action_str", "撤回了一条消息");
        if (!string.IsNullOrWhiteSpace(recalledText))
        {
            hint.SetProperty("source_name", recalledText);
            hint.SetProperty("source_is_user", "0");
            hint.SetProperty("single_actor", "1");
            hint.SetProperty("display_text", $"{recalledText}撤回了一条消息");
            return;
        }

        if (!string.IsNullOrWhiteSpace(actorName))
        {
            hint.SetProperty("source_name", actorName);
            hint.SetProperty("source_is_user", "1");
            hint.SetProperty("single_actor", "1");
            hint.SetProperty("display_text", $"{actorName}撤回了一条消息");
        }
    }

    private static void ReadJoinGroupSystemHintField(
        QQMessageSegment currentMessage,
        int key,
        uint tag,
        CodedInputStream input)
    {
        var wireType = WireFormat.GetTagWireType(tag);
        var hint = currentMessage.SystemHint ??= new QQSystemHintMessage();

        if (key is 48275 or 48501 or 48511 &&
            wireType == WireFormat.WireType.Varint)
        {
            hint.SetProperty(key switch
            {
                48275 => "join_group_subtype",
                48501 => "join_group_type",
                _ => "join_group_flag",
            }, input.ReadUInt64().ToString());

            ApplyJoinGroupSystemHint(currentMessage);
            return;
        }

        if (key is >= 48503 and <= 48508 &&
            wireType == WireFormat.WireType.LengthDelimited)
        {
            hint.SetProperty(key switch
            {
                48503 => "join_user_uid",
                48504 => "join_user_name",
                48505 => "join_user_display_name",
                48506 => "join_group_uid",
                48507 => "join_group_name",
                _ => "join_group_display_name",
            }, input.ReadBytes().ToStringUtf8());

            ApplyJoinGroupSystemHint(currentMessage);
            return;
        }

        TrySkipLastField(input);
    }

    private static void ApplyJoinGroupSystemHint(QQMessageSegment currentMessage)
    {
        var hint = currentMessage.SystemHint;
        if (hint is null)
            return;

        var userName = FirstNonEmpty(
            hint.GetProperty("join_user_display_name"),
            hint.GetProperty("join_user_name"));
        if (string.IsNullOrWhiteSpace(userName))
            return;

        var userUid = hint.GetProperty("join_user_uid") ?? string.Empty;
        var groupName = FirstNonEmpty(
            hint.GetProperty("join_group_display_name"),
            hint.GetProperty("join_group_name"));

        hint.Participants.Clear();
        hint.Participants.Add(new QQSystemHintParticipant(userUid, userName));
        hint.SetProperty("action_str", "加入了群聊。");
        hint.SetProperty("display_text", $"{userName}加入了群聊。");
        hint.SetProperty("single_actor", "1");
        if (!string.IsNullOrWhiteSpace(groupName))
        {
            hint.SetProperty("target_name", groupName);
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static void ReadSystemHintParticipant(QQMessageSegment currentMessage, byte[] data)
    {
        var input = new CodedInputStream(data);
        string? uid = null;
        string? nickname = null;

        try
        {
            while (!input.IsAtEnd)
            {
                var tag = input.ReadTag();
                if (tag == 0)
                    break;

                var key = WireFormat.GetTagFieldNumber(tag);
                var wireType = WireFormat.GetTagWireType(tag);
                if (key == 1005 && wireType == WireFormat.WireType.LengthDelimited)
                {
                    uid = input.ReadBytes().ToStringUtf8();
                    continue;
                }

                if (key == 1006 && wireType == WireFormat.WireType.LengthDelimited)
                {
                    nickname = input.ReadBytes().ToStringUtf8();
                    continue;
                }

                if (!TrySkipLastField(input))
                    break;
            }
        }
        catch
        {
            // Keep any participant fields decoded before an unknown/truncated tail.
        }

        if (string.IsNullOrWhiteSpace(uid) && string.IsNullOrWhiteSpace(nickname))
            return;

        currentMessage.SystemHint ??= new QQSystemHintMessage();
        currentMessage.SystemHint.Participants.Add(new QQSystemHintParticipant(
            uid ?? string.Empty,
            nickname ?? string.Empty));
    }

    private static void ReadSystemHintProperty(QQMessageSegment currentMessage, byte[] data)
    {
        var input = new CodedInputStream(data);
        string? name = null;
        string? value = null;

        try
        {
            while (!input.IsAtEnd)
            {
                var tag = input.ReadTag();
                if (tag == 0)
                    break;

                var key = WireFormat.GetTagFieldNumber(tag);
                var wireType = WireFormat.GetTagWireType(tag);
                if (key == 1005 && wireType == WireFormat.WireType.LengthDelimited)
                {
                    name = input.ReadBytes().ToStringUtf8();
                    continue;
                }

                if (key == 1006 && wireType == WireFormat.WireType.LengthDelimited)
                {
                    value = input.ReadBytes().ToStringUtf8();
                    continue;
                }

                if (!TrySkipLastField(input))
                    break;
            }
        }
        catch
        {
            // Keep any key/value fields decoded before an unknown/truncated tail.
        }

        if (string.IsNullOrWhiteSpace(name))
            return;

        currentMessage.SystemHint ??= new QQSystemHintMessage();
        currentMessage.SystemHint.SetProperty(name, value ?? string.Empty);
    }

    private static void ApplyXmlSystemHint(QQMessageSegment currentMessage, string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return;

        try
        {
            var document = XDocument.Parse(xml);
            if (document.Root?.Name.LocalName != "gtip")
                return;

            var hint = currentMessage.SystemHint ??= new QQSystemHintMessage();
            var textParts = new List<string>();
            var displayParts = new List<string>();
            string? targetName = null;

            foreach (var element in document.Root.Elements())
            {
                switch (element.Name.LocalName)
                {
                    case "qq":
                    {
                        var uid = FirstNonEmpty(
                            GetXmlAttribute(element, "uin"),
                            GetXmlAttribute(element, "uid"),
                            GetXmlAttribute(element, "jp"));
                        var uin = GetXmlAttribute(element, "jp")?.Trim() ?? string.Empty;
                        var nickname = GetXmlAttribute(element, "nm")?.Trim() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(nickname))
                        {
                            nickname = hint.Participants
                                .FirstOrDefault(participant => string.Equals(participant.Uid, uid, StringComparison.Ordinal))
                                ?.Nickname ?? string.Empty;
                        }

                        AddOrUpdateSystemHintParticipant(
                            hint,
                            new QQSystemHintParticipant(uid.Trim(), nickname));

                        if (!string.IsNullOrWhiteSpace(uin))
                        {
                            var participantIndex = hint.Participants.FindIndex(participant =>
                                string.Equals(participant.Uid, uid, StringComparison.Ordinal));
                            if (participantIndex >= 0)
                            {
                                SetSystemHintPropertyIfMissing(hint, $"uin_str{participantIndex + 1}", uin);
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(nickname))
                        {
                            displayParts.Add(nickname);
                        }

                        break;
                    }
                    case "nor":
                    {
                        var text = GetXmlAttribute(element, "txt");
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            textParts.Add(text);
                            displayParts.Add(text);
                        }

                        break;
                    }
                    case "url":
                    {
                        var text = GetXmlAttribute(element, "txt");
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            targetName = text.Trim();
                            displayParts.Add(targetName);
                        }

                        SetSystemHintPropertyIfMissing(hint, "msg_seq", GetXmlAttribute(element, "msgseq"));
                        break;
                    }
                    case "face":
                    {
                        var faceId = GetXmlAttribute(element, "id");
                        SetSystemHintPropertyIfMissing(hint, "face_type", GetXmlAttribute(element, "type"));
                        SetSystemHintPropertyIfMissing(hint, "face_id", faceId);
                        if (!string.IsNullOrWhiteSpace(faceId))
                        {
                            displayParts.Add($"[QQ表情:{faceId.Trim()}]");
                        }

                        break;
                    }
                }
            }

            if (textParts.Count > 0)
            {
                SetSystemHintPropertyIfMissing(hint, "action_str", textParts[0].Trim());
            }

            SetSystemHintPropertyIfMissing(hint, "target_name", targetName);
            if (displayParts.Count > 0)
            {
                SetSystemHintPropertyIfMissing(hint, "display_text", string.Concat(displayParts).Trim());
            }
        }
        catch
        {
            // Keep the raw XML; some system hints are not strict XML.
        }
    }

    private static string? GetXmlAttribute(XElement element, string name)
    {
        return element.Attribute(name)?.Value;
    }

    private static void ApplyJsonSystemHint(QQMessageSegment currentMessage, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var hint = currentMessage.SystemHint ??= new QQSystemHintMessage();
            var participants = new List<(string Uid, string Uin, string Nickname)>();
            var textParts = new List<string>();
            var displayParts = new List<string>();
            string? actionImageUrl = null;

            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var type = GetJsonString(item, "type");
                if (string.Equals(type, "qq", StringComparison.OrdinalIgnoreCase))
                {
                    var nickname = GetJsonString(item, "nm")?.Trim() ?? string.Empty;
                    var uid = FirstNonEmpty(
                        GetJsonString(item, "uid"),
                        GetJsonString(item, "jp"),
                        GetJsonString(item, "uin"));
                    var uin = GetJsonString(item, "uin")?.Trim() ?? string.Empty;

                    participants.Add((uid.Trim(), uin, nickname));
                    if (!string.IsNullOrWhiteSpace(nickname))
                    {
                        displayParts.Add(nickname);
                    }

                    continue;
                }

                if (!string.Equals(type, "img", StringComparison.OrdinalIgnoreCase))
                {
                    var text = GetJsonString(item, "txt");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        textParts.Add(text);
                        displayParts.Add(text);
                    }

                    continue;
                }

                actionImageUrl ??= GetJsonString(item, "src")?.Trim();
            }

            for (var i = 0; i < participants.Count; i++)
            {
                var participant = participants[i];
                AddOrUpdateSystemHintParticipant(
                    hint,
                    new QQSystemHintParticipant(participant.Uid, participant.Nickname));
                SetSystemHintPropertyIfMissing(hint, $"uin_str{i + 1}", participant.Uin);
            }

            SetSystemHintPropertyIfMissing(hint, "action_img_url", actionImageUrl);
            if (displayParts.Count > 0)
            {
                SetSystemHintPropertyIfMissing(hint, "display_text", string.Concat(displayParts).Trim());
            }

            if (textParts.Count == 0)
                return;

            SetSystemHintPropertyIfMissing(hint, "action_str", textParts[0].Trim());
            if (participants.Count <= 1)
            {
                SetSystemHintPropertyIfMissing(hint, "single_actor", "1");
            }
            else if (textParts.Count > 1)
            {
                SetSystemHintPropertyIfMissing(hint, "suffix_str", string.Concat(textParts.Skip(1)).Trim());
            }
        }
        catch
        {
            // Some system hints use the same field for opaque text; keep the raw JSON string only.
        }
    }

    private static void AddOrUpdateSystemHintParticipant(
        QQSystemHintMessage hint,
        QQSystemHintParticipant participant)
    {
        if (string.IsNullOrWhiteSpace(participant.Uid) &&
            string.IsNullOrWhiteSpace(participant.Nickname))
        {
            return;
        }

        for (var i = 0; i < hint.Participants.Count; i++)
        {
            var existing = hint.Participants[i];
            var sameUid = !string.IsNullOrWhiteSpace(participant.Uid) &&
                          string.Equals(existing.Uid, participant.Uid, StringComparison.Ordinal);
            var sameName = string.IsNullOrWhiteSpace(existing.Uid) &&
                           string.IsNullOrWhiteSpace(participant.Uid) &&
                           !string.IsNullOrWhiteSpace(participant.Nickname) &&
                           string.Equals(existing.Nickname, participant.Nickname, StringComparison.Ordinal);
            if (!sameUid && !sameName)
                continue;

            if (string.IsNullOrWhiteSpace(existing.Nickname) &&
                !string.IsNullOrWhiteSpace(participant.Nickname))
            {
                hint.Participants[i] = participant;
            }

            return;
        }

        hint.Participants.Add(participant);
    }

    private static void SetSystemHintPropertyIfMissing(
        QQSystemHintMessage hint,
        string name,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.IsNullOrWhiteSpace(hint.GetProperty(name)))
        {
            return;
        }

        hint.SetProperty(name, value);
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            ? GetJsonString(property)
            : null;
    }

    private static string? GetJsonString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static bool TrySkipLastField(CodedInputStream input)
    {
        try
        {
            input.SkipLastField();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ReadReplyField(
        QQMessageSegment currentMessage,
        int key,
        uint tag,
        CodedInputStream input)
    {
        var reply = currentMessage.Reply ??= new QQReplyMessage();

        switch (key)
        {
            case 47401:
                reply.ReplySegmentId = input.ReadInt64();
                break;
            case 47402:
                reply.MessageSeq = input.ReadInt64();
                break;
            case 47403:
                reply.SenderId = input.ReadUInt32();
                break;
            case 47404:
                reply.MessageTime = input.ReadInt32();
                break;
            case 47410 when WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited:
                ParseReplySourceMetadata(input.ReadBytes().ToByteArray(), reply);
                UpdateReplySourceGroupId(reply);
                break;
            case 47411:
                reply.PeerId = input.ReadUInt32();
                UpdateReplySourceGroupId(reply);
                break;
            case 47412 when WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited:
                reply.SourceGroupName = input.ReadBytes().ToStringUtf8();
                UpdateReplySourceGroupId(reply);
                break;
            case 47413:
                reply.PreviewText = input.ReadBytes().ToStringUtf8();
                break;
            case 47415:
                reply.Flag = input.ReadInt32();
                break;
            case 47416:
                reply.InternalMessageId = input.ReadInt64();
                break;
            case 47419:
                reply.MessageSeq2 = input.ReadInt64();
                break;
            case 47422:
                reply.MessageId = input.ReadInt64();
                break;
            case 47423 when WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited:
                AppendReplyPreviewSegments(reply, input.ReadBytes().ToByteArray());
                break;
            default:
                input.SkipLastField();
                break;
        }
    }

    private static void ParseReplySourceMetadata(byte[] data, QQReplyMessage reply)
    {
        var input = new CodedInputStream(data);
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0)
                break;

            var key = WireFormat.GetTagFieldNumber(tag);
            if (key == 1 && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
            {
                ParseReplySourceIdentity(input.ReadBytes().ToByteArray(), reply);
                continue;
            }

            if (key == 2 && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
            {
                ParseReplyTargetMetadata(input.ReadBytes().ToByteArray(), reply);
                continue;
            }

            input.SkipLastField();
        }
    }

    private static void ParseReplySourceIdentity(byte[] data, QQReplyMessage reply)
    {
        var input = new CodedInputStream(data);
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0)
                break;

            var key = WireFormat.GetTagFieldNumber(tag);
            if (key is 7 or 8 && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
            {
                ParseReplySourceSender(input.ReadBytes().ToByteArray(), reply);
                continue;
            }

            input.SkipLastField();
        }
    }

    private static void ParseReplySourceSender(byte[] data, QQReplyMessage reply)
    {
        var input = new CodedInputStream(data);
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0)
                break;

            var key = WireFormat.GetTagFieldNumber(tag);
            if (key is 4 or 6 && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
            {
                reply.SourceSenderName = input.ReadBytes().ToStringUtf8();
                continue;
            }

            input.SkipLastField();
        }
    }

    private static void ParseReplyTargetMetadata(byte[] data, QQReplyMessage reply)
    {
        var input = new CodedInputStream(data);
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0)
                break;

            var key = WireFormat.GetTagFieldNumber(tag);
            var wireType = WireFormat.GetTagWireType(tag);
            switch (key)
            {
                case 1 when wireType == WireFormat.WireType.Varint:
                    reply.TargetConversationType = input.ReadInt32();
                    UpdateReplySourceGroupId(reply);
                    break;
                case 2 when wireType == WireFormat.WireType.Varint:
                    reply.TargetConversationSubType1 = input.ReadInt32();
                    break;
                case 3 when wireType == WireFormat.WireType.Varint:
                    reply.TargetConversationSubType2 = input.ReadInt32();
                    break;
                case 5:
                    if (reply.MessageSeq == 0)
                        reply.MessageSeq = input.ReadInt64();
                    else
                        input.SkipLastField();
                    break;
                case 6:
                    if (reply.MessageTime == 0)
                        reply.MessageTime = input.ReadInt32();
                    else
                        input.SkipLastField();
                    break;
                case 12:
                    reply.MessageRandom = input.ReadInt64();
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
    }

    private static void UpdateReplySourceGroupId(QQReplyMessage reply)
    {
        reply.SourceGroupId = reply.PeerId != 0 && IsReplySourceGroup(reply)
            ? reply.PeerId
            : 0;
    }

    private static bool IsReplySourceGroup(QQReplyMessage reply)
    {
        return !string.IsNullOrWhiteSpace(reply.SourceGroupName) ||
               reply.TargetConversationType == QQReplyMessage.GroupTargetConversationType;
    }

    private static void AppendReplyPreviewSegments(QQReplyMessage reply, byte[] data)
    {
        var segments = ParseReplyPreviewSegments(data);
        if (segments.Count == 0)
            return;

        reply.Segments = reply.Segments.Count == 0
            ? segments
            : reply.Segments.Concat(segments).ToArray();
    }

    private static IReadOnlyList<QQMessageSegment> ParseReplyPreviewSegments(byte[] data)
    {
        if (data.Length == 0)
            return [];

        try
        {
            return ParseMessage(data).Segments;
        }
        catch
        {
            try
            {
                return [ParseMessageSegment(data)];
            }
            catch
            {
                return [];
            }
        }
    }

    public static IReadOnlyList<QQForwardedMessage> ParseForwardedMessages(byte[] subContent)
    {
        return ParseForwardedMessages(subContent, 0);
    }

    public static QQForwardedMessage ParseEmbeddedMessageRecord(byte[] data)
    {
        QQForwardedMessage? message = null;
        try
        {
            message = ParseForwardedMessage(data, 0);
            if (message.Segments.Count > 0 || message.NestedForwardedMessages.Count > 0)
                return message;
        }
        catch
        {
        }

        try
        {
            var segment = ParseMessageSegment(data);
            if (HasMessageSegmentOwnContent(segment))
            {
                message ??= new QQForwardedMessage();
                message.Segments = [segment];
                return message;
            }
        }
        catch
        {
        }

        try
        {
            var content = ParseMessage(data);
            message ??= new QQForwardedMessage();
            message.Segments = content.Segments;
            return message;
        }
        catch
        {
        }

        if (message is not null)
            return message;

        throw new InvalidOperationException("Embedded message record parsing failed.");
    }

    private static bool HasMessageSegmentOwnContent(QQMessageSegment segment)
    {
        return segment.Type != default ||
               !string.IsNullOrWhiteSpace(segment.Text) ||
               !string.IsNullOrWhiteSpace(segment.AltText) ||
               segment.FaceId is not null ||
               segment.SystemHint is not null ||
               segment.MediaFileSize is > 0 ||
               !string.IsNullOrWhiteSpace(segment.ImageFileName) ||
               segment.ImageMd5 is { Length: > 0 } ||
               !string.IsNullOrWhiteSpace(segment.ImageLocalPath) ||
               !string.IsNullOrWhiteSpace(segment.ImageThumbnailPath) ||
               !string.IsNullOrWhiteSpace(segment.ImageLargePath) ||
               !string.IsNullOrWhiteSpace(segment.ImageOriginalPath) ||
               !string.IsNullOrWhiteSpace(segment.MarketFaceName) ||
               segment.MarketFaceImageIdBytes is { Length: > 0 } ||
               !string.IsNullOrWhiteSpace(segment.AppJson) ||
               !string.IsNullOrWhiteSpace(segment.Xml);
    }

    private static IReadOnlyList<QQForwardedMessage> ParseForwardedMessages(byte[] subContent, int depth)
    {
        var root = new CodedInputStream(subContent);
        var messages = new List<QQForwardedMessage>();

        if (depth > MaxForwardedMessageDepth)
            return messages;

        while (!root.IsAtEnd)
        {
            var tag = root.ReadTag();
            if (tag == 0)
                break;

            var fieldNumber = WireFormat.GetTagFieldNumber(tag);
            if (fieldNumber != 40900 || WireFormat.GetTagWireType(tag) != WireFormat.WireType.LengthDelimited)
            {
                root.SkipLastField();
                continue;
            }

            messages.Add(ParseForwardedMessage(root.ReadBytes().ToByteArray(), depth));
        }

        return messages;
    }

    private static QQForwardedMessage ParseForwardedMessage(byte[] data, int depth)
    {
        var input = new CodedInputStream(data);
        var message = new QQForwardedMessage();
        var segments = new List<QQMessageSegment>();
        var nestedMessages = new List<QQForwardedMessage>();

        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0)
                break;

            var key = WireFormat.GetTagFieldNumber(tag);
            try
            {
                switch (key)
                {
                    case 40001:
                        message.MessageId = input.ReadInt64();
                        break;
                    case 40003:
                        message.MessageSeq = input.ReadInt64();
                        break;
                    case 40010:
                        message.ChatType = (ChatType)input.ReadInt32();
                        break;
                    case 40011:
                        message.MessageType = (MessageType)input.ReadInt32();
                        break;
                    case 40012:
                        message.SubMessageType = (SubMessageType)input.ReadInt32();
                        break;
                    case 40020:
                        message.SenderUid = input.ReadBytes().ToStringUtf8();
                        break;
                    case 40021:
                        message.PeerUid = input.ReadBytes().ToStringUtf8();
                        break;
                    case 40030:
                        message.SavedGroupId = input.ReadUInt32();
                        break;
                    case 40033:
                        message.SenderId = input.ReadUInt32();
                        break;
                    case 40050:
                        message.MessageTime = input.ReadInt32();
                        break;
                    case 40090:
                        message.SendMemberName = input.ReadBytes().ToStringUtf8();
                        break;
                    case 40093:
                        message.SendNickName = input.ReadBytes().ToStringUtf8();
                        break;
                    case 40600:
                        message.AvatarUrl = ParseForwardedMessageAvatarUrl(input.ReadBytes().ToByteArray());
                        break;
                    case 40800:
                        segments.Add(ParseForwardedMessageSegment(input.ReadBytes().ToByteArray()));
                        break;
                    case 40900 when WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited:
                        nestedMessages.AddRange(ParseNestedForwardedMessages(input.ReadBytes().ToByteArray(), depth + 1));
                        break;
                    default:
                        input.SkipLastField();
                        break;
                }
            }
            catch
            {
                try
                {
                    input.SkipLastField();
                }
                catch
                {
                    break;
                }
            }
        }

        message.Segments = segments;
        message.NestedForwardedMessages = nestedMessages;
        return message;
    }

    private static IReadOnlyList<QQForwardedMessage> ParseNestedForwardedMessages(byte[] data, int depth)
    {
        if (data.Length == 0 || depth > MaxForwardedMessageDepth)
            return [];

        try
        {
            var message = ParseForwardedMessage(data, depth);
            if (HasForwardedMessageOwnContent(message))
                return [message];

            if (message.NestedForwardedMessages.Count > 0)
                return message.NestedForwardedMessages;
        }
        catch
        {
        }

        try
        {
            return ParseForwardedMessages(data, depth);
        }
        catch
        {
            return [];
        }
    }

    private static bool HasForwardedMessageOwnContent(QQForwardedMessage message)
    {
        return message.MessageId != 0 ||
               message.MessageSeq != 0 ||
               message.SenderId != 0 ||
               message.Segments.Count > 0;
    }

    private static string? ParseForwardedMessageAvatarUrl(byte[] data)
    {
        var input = new CodedInputStream(data);
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0)
                break;

            var key = WireFormat.GetTagFieldNumber(tag);
            if (key == 42341 && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
            {
                var url = ParseForwardedMessageAvatarInfo(input.ReadBytes().ToByteArray());
                if (!string.IsNullOrWhiteSpace(url))
                    return url;

                continue;
            }

            input.SkipLastField();
        }

        return null;
    }

    private static string? ParseForwardedMessageAvatarInfo(byte[] data)
    {
        var input = new CodedInputStream(data);
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0)
                break;

            var key = WireFormat.GetTagFieldNumber(tag);
            if (key == 42346 && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
            {
                return input.ReadBytes().ToStringUtf8();
            }

            input.SkipLastField();
        }

        return null;
    }

    private static QQMessageSegment ParseForwardedMessageSegment(byte[] data)
    {
        try
        {
            return ParseMessageSegment(data);
        }
        catch
        {
            return ParseMessage(data).FirstSegment;
        }
    }
}

public class QQProfileInfoReader : IQQDatabase
{
    public QQProfileInfoDbContext DbContext { get; private set; } = null!;

    public RawDatabase RawDatabase { get; }
    public QQDatabaseType DatabaseType => QQDatabaseType.ProfileInfo;
    public string DatabaseFilePath => RawDatabase.DatabaseFilePath;

    public QQProfileInfoReader(string databaseFilePath, bool useVFS = false)
    {
        RawDatabase = new(databaseFilePath, useVFS);
    }

    public QQProfileInfoReader(
        string databaseFilePath,
        string cipherPassword,
        HashAlgorithmName? cipherKdfAlgorithm = null,
        HashAlgorithmName? cipherHmacAlgorithm = null,
        int cipherPageSize = 4096,
        int cipherKdfIter = 4000,
        bool useVFS = true)
    {
        RawDatabase = new(
            databaseFilePath,
            cipherPassword,
            cipherKdfAlgorithm,
            cipherHmacAlgorithm,
            cipherPageSize,
            cipherKdfIter,
            useVFS);
    }

    public void Initialize()
    {
        RawDatabase.Initialize();
        var connection = new QQNTDbConnection(RawDatabase.Database, RawDatabase.DatabaseFilePath);
        var optionsBuilder = new DbContextOptionsBuilder<QQProfileInfoDbContext>();
        optionsBuilder.UseSqlite(connection).UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        DbContext = new QQProfileInfoDbContext(optionsBuilder.Options);
    }

    public void Dispose()
    {
        DbContext.Dispose();
        RawDatabase.Dispose();
    }
}

public class QQMessageContent
{
    public QQMessageSegment FirstSegment { get; set; }
    public IReadOnlyList<QQMessageSegment> Segments { get; set; }

    public QQMessageContent(IReadOnlyList<QQMessageSegment> segments)
    {
        Segments = segments;
        FirstSegment = segments[0];
    }

    public QQMessageContent(QQMessageSegment firstSegment)
    {
        FirstSegment = firstSegment;
        Segments = [firstSegment];
    }

    public string GetText()
    {
        return string.Concat(Segments.Select(v => v.GetDisplayText()));
    }

    public string GetText(MessageType messageType, SubMessageType subMessageType)
    {
        return QQMessageDisplayText.CreateText(this, messageType, subMessageType);
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

public class QQForwardedMessage
{
    public long MessageId { get; set; }
    public long MessageSeq { get; set; }
    public ChatType ChatType { get; set; }
    public MessageType MessageType { get; set; }
    public SubMessageType SubMessageType { get; set; }
    public string SenderUid { get; set; } = string.Empty;
    public string PeerUid { get; set; } = string.Empty;
    public uint SavedGroupId { get; set; }
    public uint SenderId { get; set; }
    public int MessageTime { get; set; }
    public string? SendMemberName { get; set; }
    public string? SendNickName { get; set; }
    public string? AvatarUrl { get; set; }
    public IReadOnlyList<QQMessageSegment> Segments { get; set; } = [];
    public IReadOnlyList<QQForwardedMessage> NestedForwardedMessages { get; set; } = [];
}

public class QQReplyMessage
{
    public long ReplySegmentId { get; set; }
    public long MessageSeq { get; set; }
    public long MessageSeq2 { get; set; }
    public uint SenderId { get; set; }

    /// <summary>
    /// 47411，回复来源会话的数字 ID。它可能是群号，也可能是私聊对象 QQ 号，
    /// 不能脱离 47410.2.1 或 47412 直接当群号使用。
    /// </summary>
    public uint PeerId { get; set; }

    public int MessageTime { get; set; }
    public int Flag { get; set; }
    public long InternalMessageId { get; set; }
    public long MessageId { get; set; }
    public long MessageRandom { get; set; }
    public string? PreviewText { get; set; }

    internal const int GroupTargetConversationType = 82;

    /// <summary>
    /// 47410.2.1，被回复消息所在会话类型。已知 82 表示群聊来源，
    /// 9 + 47410.2.2/47410.2.3 为 11 表示私聊来源。
    /// </summary>
    public int? TargetConversationType { get; set; }

    /// <summary>
    /// 47410.2.2，被回复消息会话类型的子类型。私聊回复样本中通常为 11。
    /// </summary>
    public int? TargetConversationSubType1 { get; set; }

    /// <summary>
    /// 47410.2.3，被回复消息会话类型的子类型。私聊回复样本中通常为 11。
    /// </summary>
    public int? TargetConversationSubType2 { get; set; }

    /// <summary>
    /// 47410 里的原消息发送者昵称。跨群回复时，当前群的成员名缓存里通常找不到这个人。
    /// </summary>
    public string? SourceSenderName { get; set; }

    /// <summary>
    /// 根据 47410.2.1/47412 判定出的原消息所在群号。不要直接从 47411 赋值，
    /// 因为 QQ 号和群号可能相同，单看数字无法区分会话类型。
    /// </summary>
    public uint SourceGroupId { get; set; }

    /// <summary>
    /// 47412，跨聊天回复时表示原消息所在群名。
    /// </summary>
    public string? SourceGroupName { get; set; }

    public IReadOnlyList<QQMessageSegment> Segments { get; set; } = [];
}

public sealed class QQSystemHintMessage
{
    private readonly Dictionary<string, string> _properties = new(StringComparer.Ordinal);

    public List<QQSystemHintParticipant> Participants { get; } = [];
    public IReadOnlyDictionary<string, string> Properties => _properties;
    public string? Xml { get; set; }
    public string? Json { get; set; }

    public string? Action => GetProperty("action_str");
    public string? Suffix => GetProperty("suffix_str");
    public string? ActionImageUrl => GetProperty("action_img_url");
    public string? DisplayText => GetProperty("display_text");
    public bool IsSingleActor => string.Equals(GetProperty("single_actor"), "1", StringComparison.Ordinal);
    public string? SourceName => GetProperty("source_name");
    public bool SourceIsUser => !string.Equals(GetProperty("source_is_user"), "0", StringComparison.Ordinal);
    public string? TargetName => GetProperty("target_name");
    public long TargetMessageSeq => long.TryParse(GetProperty("msg_seq"), out var value) ? value : 0;
    public int FaceId => int.TryParse(GetProperty("face_id"), out var value) ? value : 0;
    public string? RecalledMessageText => GetProperty("recalled_text");
    public byte[]? RecalledOriginalMessageContent { get; set; }

    public void SetProperty(string name, string value)
    {
        _properties[name] = value;
    }

    public string? GetProperty(string name)
    {
        return _properties.TryGetValue(name, out var value) ? value : null;
    }
}

public sealed record QQSystemHintParticipant(string Uid, string Nickname);

public class QQMessageSegment
{
    /// <summary>
    /// 45001
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// 45002
    /// </summary>
    public MessageSegmentType Type { get; set; }

    public string? SenderUid { get; set; }

    public string? PeerUid { get; set; }

    /// <summary>
    /// 45101
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// 45402
    /// </summary>
    public string? ImageFileName { get; set; }

    /// <summary>
    /// 45422. 视频封面文件名。
    /// </summary>
    public string? VideoCoverFileName { get; set; }

    /// <summary>
    /// 45406
    /// </summary>
    public byte[]? ImageMd5 { get; set; }

    /// <summary>
    /// 45405. 语音消息里是本地 Ptt 文件大小。
    /// </summary>
    public long? MediaFileSize { get; set; }

    /// <summary>
    /// 45503
    /// </summary>
    public string? ImageRKey { get; set; }

    /// <summary>
    /// 45802
    /// </summary>
    public string? ImageThumbnailPath { get; set; }

    /// <summary>
    /// 45803
    /// </summary>
    public string? ImageLargePath { get; set; }

    /// <summary>
    /// 45804
    /// </summary>
    public string? ImageOriginalPath { get; set; }

    /// <summary>
    /// 45812
    /// </summary>
    public string? ImageLocalPath { get; set; }

    /// <summary>
    /// 45816
    /// </summary>
    public string? ImageHost { get; set; }

    /// <summary>
    /// 45815
    /// </summary>
    public string? AltText { get; set; }

    /// <summary>
    /// 45411
    /// </summary>
    public int? ImageWidth { get; set; }

    /// <summary>
    /// 45412
    /// </summary>
    public int? ImageHeight { get; set; }

    /// <summary>
    /// 45415. 视频时长，单位是毫秒。
    /// </summary>
    public int? VideoDurationMilliseconds { get; set; }

    /// <summary>
    /// 45003
    /// </summary>
    public int? FaceType { get; set; }

    /// <summary>
    /// 47601
    /// </summary>
    public int? FaceId { get; set; }

    /// <summary>
    /// 80810，QQNT 商城表情包 ID。本地文件位于
    /// nt_data/Emoji/marketface/&lt;表情包 ID&gt;/。
    /// </summary>
    public int? MarketFacePackageId { get; set; }

    /// <summary>
    /// 80900，商城表情显示名称，通常是“[吃饭]”这种文本。
    /// </summary>
    public string? MarketFaceName { get; set; }

    /// <summary>
    /// 80903，商城表情图片 ID 的 16 字节原始值。这里必须保留原始字节，
    /// 因为无 schema 的 protobuf 解码器容易把同一段字节误判成嵌套字段。
    /// 查找本地文件时再把它转成十六进制字符串。
    /// </summary>
    public byte[]? MarketFaceImageIdBytes { get; set; }

    /// <summary>
    /// 80909，QQNT 声明的商城表情显示宽度。
    /// </summary>
    public int? MarketFaceWidth { get; set; }

    /// <summary>
    /// 80910，QQNT 声明的商城表情显示高度。
    /// </summary>
    public int? MarketFaceHeight { get; set; }

    /// <summary>
    /// 47901, JSON payload for app/multimsg cards.
    /// </summary>
    public string? AppJson { get; set; }

    /// <summary>
    /// 47902
    /// </summary>
    public string? AppResid { get; set; }

    /// <summary>
    /// 47904
    /// </summary>
    public string? AppUniseq { get; set; }

    /// <summary>
    /// 48601
    /// </summary>
    public string? XmlResid { get; set; }

    /// <summary>
    /// 48602, XML payload for app/multimsg cards.
    /// </summary>
    public string? Xml { get; set; }

    /// <summary>
    /// 48603
    /// </summary>
    public string? XmlFileName { get; set; }

    public string? ParseError { get; set; }

    public QQReplyMessage? Reply { get; set; }

    public QQSystemHintMessage? SystemHint { get; set; }

    public string? MarketFaceImageId => MarketFaceImageIdBytes is { Length: > 0 }
        ? Convert.ToHexString(MarketFaceImageIdBytes).ToLowerInvariant()
        : null;

    public bool IsQQFace => Type == MessageSegmentType.Emoji && FaceId is not null;
    public bool IsMarketFace => Type == MessageSegmentType.MarketFace;
    public bool IsImage => Type is MessageSegmentType.Image;
    public bool IsVoice => Type == MessageSegmentType.Record;
    public bool IsVideo => Type == MessageSegmentType.Video;
    public string? VoiceFileName => IsVoice ? ImageFileName : null;
    public byte[]? VoiceMd5 => IsVoice ? ImageMd5 : null;
    public string? VideoFileName => IsVideo ? ImageFileName : null;
    public byte[]? VideoMd5 => IsVideo ? ImageMd5 : null;

    public void AppendAltText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        if (string.IsNullOrEmpty(AltText))
        {
            AltText = text;
            return;
        }

        if (!AltText.Contains(text, StringComparison.Ordinal))
        {
            AltText += text;
        }
    }

    public string GetDisplayText()
    {
        if (!string.IsNullOrEmpty(Text))
            return Text;

        if (!string.IsNullOrEmpty(AltText))
            return AltText;

        if (FaceId is { } faceId)
            return $"[QQ表情:{faceId}]";

        if (!string.IsNullOrWhiteSpace(MarketFaceName))
            return MarketFaceName;

        if (SystemHint is { } systemHint && TryCreateSystemHintDisplayText(systemHint, out var systemHintText))
            return systemHintText;

        return Type switch
        {
            MessageSegmentType.Image => "[图片]",
            MessageSegmentType.File => "[文件]",
            MessageSegmentType.Record => "[语音]",
            MessageSegmentType.Video => "[视频]",
            MessageSegmentType.Reply => "[回复]",
            MessageSegmentType.System => "[系统消息]",
            MessageSegmentType.App => "[应用消息]",
            MessageSegmentType.RichMedia => "[应用卡片]",
            MessageSegmentType.MarketFace => "[商城表情]",
            MessageSegmentType.Xml => "[XML消息]",
            MessageSegmentType.Call => "[通话]",
            MessageSegmentType.Dynamic => "[动态消息]",
            _ => string.Empty,
        };
    }

    private static bool TryCreateSystemHintDisplayText(QQSystemHintMessage systemHint, out string text)
    {
        text = string.Empty;
        if (!string.IsNullOrWhiteSpace(systemHint.DisplayText))
        {
            text = systemHint.DisplayText;
            return true;
        }

        if (string.IsNullOrWhiteSpace(systemHint.Action))
            return false;

        var sourceName = FirstNonEmptyDisplayValue(
            systemHint.SourceName,
            systemHint.Participants.FirstOrDefault()?.Nickname);
        var targetName = systemHint.Participants.Count >= 2
            ? systemHint.Participants[1].Nickname
            : string.Empty;
        if (string.IsNullOrWhiteSpace(targetName))
            targetName = systemHint.TargetName ?? string.Empty;

        if (string.IsNullOrWhiteSpace(sourceName))
            return false;

        text = $"{sourceName}{systemHint.Action}{targetName}{systemHint.Suffix}";
        return true;
    }

    private static string FirstNonEmptyDisplayValue(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}

public enum MessageSegmentType : int
{
    Text = 1,
    Image = 2,
    File = 3,
    Record = 4,
    Video = 5,
    Emoji = 6,
    Reply = 7,
    System = 8,
    App = 9,
    RichMedia = 10,
    MarketFace = 11,
    Xml = 16,
    Call = 21,
    Dynamic = 26,
}
