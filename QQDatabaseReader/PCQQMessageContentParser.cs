using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using Google.Protobuf;

namespace QQDatabaseReader;

public static class PCQQMessageContentParser
{
    private static readonly Encoding Unicode = Encoding.Unicode;
    private static readonly byte[] PCQQMessageHeader = "MSG\0"u8.ToArray();
    private static readonly Regex AdjacentDuplicateMentionRegex = new(
        @"(?<!\S)(@\S+)(?:\s+\1)(?=\s|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const byte TextElementType = 1;
    private const byte FaceElementType = 2;
    private const byte GroupImageElementType = 3;
    private const byte PrivateImageElementType = 6;
    private const byte VoiceElementType = 7;
    private const byte NicknameElementType = 18;
    private const byte ReplyElementType = 25;
    private const byte VideoElementType = 26;
    private static readonly IReadOnlyDictionary<int, string> FaceNames = new Dictionary<int, string>
    {
        [0] = "惊讶",
        [1] = "撇嘴",
        [2] = "色",
        [3] = "发呆",
        [4] = "得意",
        [5] = "流泪",
        [6] = "害羞",
        [7] = "闭嘴",
        [8] = "睡",
        [9] = "大哭",
        [10] = "尴尬",
        [11] = "发怒",
        [12] = "调皮",
        [13] = "呲牙",
        [14] = "微笑",
        [15] = "难过",
        [16] = "酷",
        [18] = "抓狂",
        [19] = "吐",
        [20] = "偷笑",
        [21] = "可爱",
        [22] = "白眼",
        [23] = "傲慢",
        [24] = "饥饿",
        [25] = "困",
        [26] = "惊恐",
        [27] = "流汗",
        [28] = "憨笑",
        [29] = "悠闲",
        [30] = "奋斗",
        [31] = "咒骂",
        [32] = "疑问",
        [33] = "嘘",
        [34] = "晕",
        [35] = "折磨",
        [36] = "衰",
        [37] = "骷髅",
        [38] = "敲打",
        [39] = "再见",
        [41] = "发抖",
        [42] = "爱情",
        [43] = "跳跳",
        [46] = "猪头",
        [49] = "拥抱",
        [53] = "蛋糕",
        [54] = "闪电",
        [55] = "炸弹",
        [56] = "刀",
        [57] = "足球",
        [59] = "便便",
        [60] = "咖啡",
        [61] = "饭",
        [63] = "玫瑰",
        [64] = "凋谢",
        [66] = "爱心",
        [67] = "心碎",
        [69] = "礼物",
        [74] = "太阳",
        [75] = "月亮",
        [76] = "赞",
        [77] = "踩",
        [78] = "握手",
        [79] = "胜利",
        [85] = "飞吻",
        [86] = "怄火",
        [89] = "西瓜",
        [96] = "冷汗",
        [97] = "擦汗",
        [98] = "抠鼻",
        [99] = "鼓掌",
        [100] = "糗大了",
        [101] = "坏笑",
        [102] = "左哼哼",
        [103] = "右哼哼",
        [104] = "哈欠",
        [105] = "鄙视",
        [106] = "委屈",
        [107] = "快哭了",
        [108] = "阴险",
        [109] = "左亲亲",
        [110] = "吓",
        [111] = "可怜",
        [112] = "菜刀",
        [113] = "啤酒",
        [114] = "篮球",
        [115] = "乒乓",
        [116] = "示爱",
        [117] = "瓢虫",
        [118] = "抱拳",
        [119] = "勾引",
        [120] = "拳头",
        [121] = "差劲",
        [122] = "爱你",
        [123] = "NO",
        [124] = "OK",
        [125] = "转圈",
        [126] = "磕头",
        [127] = "回头",
        [128] = "跳绳",
        [129] = "挥手",
        [130] = "激动",
        [131] = "街舞",
        [132] = "献吻",
        [133] = "左太极",
        [134] = "右太极",
        [136] = "双喜",
        [137] = "鞭炮",
        [138] = "灯笼",
        [140] = "K歌",
        [144] = "喝彩",
        [145] = "祈祷",
        [146] = "爆筋",
        [147] = "棒棒糖",
        [148] = "喝奶",
        [151] = "飞机",
        [158] = "钞票",
        [168] = "药",
        [169] = "手枪",
        [171] = "茶",
        [172] = "眨眼睛",
        [173] = "泪奔",
        [174] = "无奈",
        [175] = "卖萌",
        [176] = "小纠结",
        [177] = "喷血",
        [178] = "斜眼笑",
        [179] = "doge",
        [180] = "惊喜",
        [181] = "骚扰",
        [182] = "笑哭",
        [183] = "我最美",
        [184] = "河蟹",
        [185] = "羊驼",
        [187] = "幽灵",
        [188] = "蛋",
        [190] = "菊花",
        [192] = "红包",
        [193] = "大笑",
        [194] = "不开心",
        [197] = "冷漠",
        [198] = "呃",
        [199] = "好棒",
        [200] = "拜托",
        [201] = "点赞",
        [202] = "无聊",
        [203] = "托脸",
        [204] = "吃",
        [205] = "送花",
        [206] = "害怕",
        [207] = "花痴",
        [208] = "小样儿",
        [210] = "飙泪",
        [211] = "我不看",
        [212] = "托腮",
        [214] = "啵啵",
        [215] = "糊脸",
        [216] = "拍头",
        [217] = "扯一扯",
        [218] = "舔一舔",
        [219] = "蹭一蹭",
        [220] = "拽炸天",
        [221] = "顶呱呱",
        [222] = "抱抱",
        [223] = "暴击",
        [224] = "开枪",
        [225] = "撩一撩",
        [226] = "拍桌",
        [227] = "拍手",
        [228] = "恭喜",
        [229] = "干杯",
        [230] = "嘲讽",
        [231] = "哼",
        [232] = "佛系",
        [233] = "掐一掐",
        [234] = "惊呆",
        [235] = "颤抖",
        [236] = "啃头",
        [237] = "偷看",
        [238] = "扇脸",
        [239] = "原谅",
        [240] = "喷脸",
        [241] = "生日快乐",
        [242] = "头撞击",
        [243] = "甩头",
        [244] = "扔狗",
        [245] = "加油必胜",
        [246] = "加油抱抱",
        [247] = "口罩护体",
        [260] = "搬砖中",
        [261] = "忙到飞起",
        [262] = "脑阔疼",
        [263] = "沧桑",
        [264] = "捂脸",
        [265] = "辣眼睛",
        [266] = "哦哟",
        [267] = "头秃",
        [268] = "问号脸",
        [269] = "暗中观察",
        [270] = "emm",
        [271] = "吃瓜",
        [272] = "呵呵哒",
        [273] = "我酸了",
        [274] = "太南了",
        [276] = "辣椒酱",
        [277] = "汪汪",
        [278] = "汗",
        [279] = "打脸",
        [280] = "击掌",
        [281] = "无眼笑",
        [282] = "敬礼",
        [283] = "狂笑",
        [284] = "面无表情",
        [285] = "摸鱼",
        [286] = "魔鬼笑",
        [287] = "哦",
        [288] = "请",
        [289] = "睁眼",
        [290] = "敲开心",
        [291] = "震惊",
        [292] = "让我康康",
        [293] = "摸锦鲤",
        [294] = "期待",
        [295] = "拿到红包",
        [296] = "真好",
        [297] = "拜谢",
        [298] = "元宝",
        [299] = "牛啊",
        [300] = "胖三斤",
        [301] = "好闪",
        [302] = "左拜年",
        [303] = "右拜年",
        [304] = "红包包",
        [305] = "右亲亲",
        [306] = "牛气冲天",
        [307] = "喵喵",
        [308] = "求红包",
        [309] = "谢红包",
        [310] = "新年烟花",
        [311] = "打call",
        [312] = "变形",
        [313] = "嗑到了",
        [314] = "仔细分析",
        [315] = "加油",
        [316] = "我没事",
        [317] = "菜汪",
        [318] = "崇拜",
        [319] = "比心",
        [320] = "庆祝",
        [321] = "老色痞",
        [322] = "拒绝",
        [323] = "嫌弃",
        [324] = "吃糖",
        [325] = "惊吓",
        [326] = "生气",
        [327] = "加一",
        [328] = "错号",
        [329] = "对号",
        [330] = "完成",
        [331] = "明白",
    };

    public static string GetDisplayText(byte[]? content)
    {
        return Parse(content).DisplayText;
    }

    public static string ExtractText(byte[]? content)
    {
        return Parse(content).Text;
    }

    public static PCQQParsedMessage Parse(byte[]? content)
    {
        if (content is not { Length: > 0 })
            return PCQQParsedMessage.Empty;

        if (content.AsSpan().StartsWith(PCQQMessageHeader))
        {
            return ParseRichTextMessage(content);
        }

        var fragments = ExtractUtf16LengthPrefixedStrings(content)
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Select(CleanupText)
            .Where(static text => text.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var text = fragments.Length == 0 ? string.Empty : string.Join("", fragments);
        return new PCQQParsedMessage(
            text,
            string.IsNullOrWhiteSpace(text) ? "[PCQQ消息]" : text,
            null,
            0,
            null,
            string.IsNullOrWhiteSpace(text)
                ? []
                : [PCQQMessageSegment.CreateText(text)]);
    }

    private static PCQQParsedMessage ParseRichTextMessage(byte[] content)
    {
        var reader = new PCQQMessageBuffer(content);
        if (!reader.Skip(8) ||
            !reader.TryReadUInt32(out _) ||
            !reader.TryReadUInt32(out _) ||
            !reader.TryReadUInt32(out _) ||
            !reader.Skip(4) ||
            !reader.TryReadUInt16(out var fontNameByteLength) ||
            !reader.Skip(fontNameByteLength) ||
            !reader.Skip(2))
        {
            return PCQQParsedMessage.Unknown;
        }

        var textBuilder = new StringBuilder();
        var segments = new List<PCQQMessageSegment>();
        long messageSeq = 0;
        PCQQReplyMessage? reply = null;
        string? senderNickname = null;

        while (reader.TryReadTlv(out var type, out var value))
        {
            switch (type)
            {
                case TextElementType:
                    AppendTextElement(value, textBuilder, segments);
                    break;
                case FaceElementType:
                    AppendFaceElement(value, textBuilder, segments);
                    break;
                case GroupImageElementType:
                case PrivateImageElementType:
                    AppendImageElement(value, textBuilder, segments);
                    break;
                case VoiceElementType:
                    AppendPlaceholder("[语音]", textBuilder, segments);
                    break;
                case NicknameElementType:
                    senderNickname ??= DecodeNicknameElement(value);
                    break;
                case ReplyElementType:
                    reply ??= DecodeReplyElement(value);
                    break;
                case VideoElementType:
                    AppendPlaceholder("[视频]", textBuilder, segments);
                    break;
            }

            if (messageSeq == 0)
                messageSeq = TryDecodeMessageSeqFromTxData(value);
        }

        segments = NormalizeSegments(segments);
        var text = NormalizeDisplayText(CleanupText(textBuilder.ToString()));
        return new PCQQParsedMessage(
            text,
            string.IsNullOrWhiteSpace(text) ? "[PCQQ消息]" : text,
            string.IsNullOrWhiteSpace(senderNickname) ? null : senderNickname,
            messageSeq,
            reply,
            segments);
    }

    private static void AppendTextElement(
        ReadOnlySpan<byte> value,
        StringBuilder builder,
        List<PCQQMessageSegment> segments)
    {
        var reader = new PCQQMessageBuffer(value);
        while (reader.TryReadTlv(out var type, out var nestedValue))
        {
            if (type != TextElementType)
                continue;

            var text = DecodeUtf16(nestedValue);
            if (!string.IsNullOrEmpty(text))
            {
                builder.Append(text);
                segments.Add(IsMentionElement(value) && text.StartsWith('@')
                    ? PCQQMessageSegment.CreateMention(text)
                    : PCQQMessageSegment.CreateText(text));
            }

            return;
        }
    }

    private static void AppendFaceElement(
        ReadOnlySpan<byte> value,
        StringBuilder builder,
        List<PCQQMessageSegment> segments)
    {
        var reader = new PCQQMessageBuffer(value);
        while (reader.TryReadTlv(out var type, out var nestedValue))
        {
            if (type != TextElementType)
                continue;

            var id = DecodeBigEndianInt(nestedValue);
            var text = FaceNames.TryGetValue(id, out var name) ? $"[{name}]" : $"[QQ表情:{id}]";
            builder.Append(text);
            segments.Add(PCQQMessageSegment.CreateFace(id, text));
            return;
        }
    }

    private static void AppendImageElement(
        ReadOnlySpan<byte> value,
        StringBuilder builder,
        List<PCQQMessageSegment> segments)
    {
        const string imageText = "[图片]";
        builder.Append(imageText);
        var image = DecodeImageElement(value);
        segments.Add(PCQQMessageSegment.CreateImage(image.RelativePath, image.Width, image.Height));
    }

    private static void AppendPlaceholder(
        string text,
        StringBuilder builder,
        List<PCQQMessageSegment> segments)
    {
        builder.Append(text);
        segments.Add(PCQQMessageSegment.CreateText(text));
    }

    private static string? DecodeImageRelativePath(ReadOnlySpan<byte> value)
    {
        return DecodeImageElement(value).RelativePath;
    }

    private static PCQQImageElement DecodeImageElement(ReadOnlySpan<byte> value)
    {
        string? relativePath = null;
        var width = 0;
        var height = 0;
        var reader = new PCQQMessageBuffer(value);
        while (reader.TryReadTlv(out var type, out var nestedValue))
        {
            // PCQQ 图片 TLV 的嵌套 type=2 是 UTF-16LE 的本地相对路径，
            // 常见格式为 UserDataImage:Group2\XX\YY\file.jpg。
            if (type == 2)
            {
                var text = CleanupText(DecodeUtf16(nestedValue));
                if (!string.IsNullOrWhiteSpace(text))
                    relativePath = NormalizeImageRelativePath(text);

                continue;
            }

            // PCQQ 图片 TLV 里的 type=0 / type=10 可能嵌 TXData 对象。
            // 官方显示尺寸来自 dwPicWidth / dwPicHeight；优先用它，避免小图在本地文件尺寸读不到时被当成默认大图。
            if (TryDecodeImageSizeFromTxData(nestedValue, out var txWidth, out var txHeight))
            {
                width = txWidth;
                height = txHeight;
            }
        }

        return new PCQQImageElement(relativePath, width > 0 ? width : null, height > 0 ? height : null);
    }

    private static bool TryDecodeImageSizeFromTxData(ReadOnlySpan<byte> value, out int width, out int height)
    {
        width = 0;
        height = 0;

        foreach (var obj in ParseTxDataObjects(value))
        {
            if (!TryGetTxUInt32(obj, "dwPicWidth", out var txWidth) ||
                !TryGetTxUInt32(obj, "dwPicHeight", out var txHeight) ||
                txWidth == 0 ||
                txHeight == 0 ||
                txWidth > int.MaxValue ||
                txHeight > int.MaxValue)
            {
                continue;
            }

            width = (int)txWidth;
            height = (int)txHeight;
            return true;
        }

        return false;
    }

    private static long TryDecodeMessageSeqFromTxData(ReadOnlySpan<byte> value)
    {
        foreach (var obj in ParseTxDataObjects(value))
        {
            if (TryGetTxUInt32(obj, "dwMsgSeq", out var messageSeq) && messageSeq != 0)
                return messageSeq;
        }

        return 0;
    }

    private static PCQQReplyMessage? DecodeReplyElement(ReadOnlySpan<byte> value)
    {
        var payload = TryReadNestedValue(value, TextElementType);
        if (payload.IsEmpty)
            return null;

        var body = payload.ToArray();
        if (!TryParseReplyElementBody(body, out var messageSeq, out var senderUin, out var previewText))
            return null;

        return new PCQQReplyMessage(messageSeq, senderUin, previewText);
    }

    private static bool TryParseReplyElementBody(
        byte[] data,
        out long messageSeq,
        out uint senderUin,
        out string previewText)
    {
        messageSeq = 0;
        senderUin = 0;
        previewText = string.Empty;

        if (!TryReadReplyPayload(data, out var payload))
            return false;

        messageSeq = payload.MessageSeq;
        senderUin = payload.SenderUin;
        previewText = string.Concat(payload.PreviewParts).Trim();
        return messageSeq != 0 && !string.IsNullOrWhiteSpace(previewText);
    }

    private static bool TryReadReplyPayload(byte[] data, out PCQQReplyPayload payload)
    {
        payload = new PCQQReplyPayload(0, 0, []);
        if (data.Length == 0)
            return false;

        var input = new CodedInputStream(data);
        try
        {
            while (!input.IsAtEnd)
            {
                var tag = input.ReadTag();
                if (tag == 0)
                    break;

                if (WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited &&
                    WireFormat.GetTagFieldNumber(tag) == 45)
                {
                    return TryReadReplyPayloadMessage(input.ReadBytes().ToByteArray(), out payload);
                }

                input.SkipLastField();
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryReadReplyPayloadMessage(byte[] data, out PCQQReplyPayload payload)
    {
        payload = new PCQQReplyPayload(0, 0, []);
        long messageSeq = 0;
        uint senderUin = 0;
        var previewParts = new List<string>();
        var input = new CodedInputStream(data);
        try
        {
            while (!input.IsAtEnd)
            {
                var tag = input.ReadTag();
                if (tag == 0)
                    break;

                var fieldNumber = WireFormat.GetTagFieldNumber(tag);
                switch (WireFormat.GetTagWireType(tag))
                {
                    case WireFormat.WireType.Varint when fieldNumber == 1:
                        messageSeq = checked((long)input.ReadUInt64());
                        break;
                    case WireFormat.WireType.Varint when fieldNumber == 2:
                        senderUin = input.ReadUInt32();
                        break;
                    case WireFormat.WireType.LengthDelimited when fieldNumber == 5:
                        if (TryReadReplyPreviewPart(input.ReadBytes().ToByteArray(), out var previewPart))
                            previewParts.Add(previewPart);
                        break;
                    default:
                        input.SkipLastField();
                        break;
                }
            }
        }
        catch
        {
            return false;
        }

        payload = new PCQQReplyPayload(messageSeq, senderUin, previewParts);
        return messageSeq != 0 && previewParts.Count > 0;
    }

    private static bool TryReadReplyPreviewPart(byte[] data, out string text)
    {
        text = string.Empty;
        if (!TryReadSingleLengthDelimited(data, out var first) ||
            !TryReadSingleLengthDelimited(first, out var textBytes))
        {
            return false;
        }

        try
        {
            var value = CleanupText(Encoding.UTF8.GetString(textBytes));
            if (!LooksLikeUserText(value))
                return false;

            text = value;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadSingleLengthDelimited(byte[] data, out byte[] value)
    {
        value = [];
        if (data.Length == 0)
            return false;

        var input = new CodedInputStream(data);
        try
        {
            var tag = input.ReadTag();
            if (tag == 0 ||
                WireFormat.GetTagWireType(tag) != WireFormat.WireType.LengthDelimited)
            {
                return false;
            }

            value = input.ReadBytes().ToByteArray();
            return value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static List<PCQQMessageSegment> NormalizeSegments(IReadOnlyList<PCQQMessageSegment> segments)
    {
        if (segments.Count == 0)
            return [];

        var result = new List<PCQQMessageSegment>(segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (segment.IsMention &&
                i + 2 < segments.Count &&
                IsWhitespaceText(segments[i + 1]) &&
                segments[i + 2].IsMention &&
                string.Equals(segment.Text, segments[i + 2].Text, StringComparison.Ordinal))
            {
                result.Add(segment);
                i += 2;
                continue;
            }

            result.Add(segment);
        }

        return result;
    }

    private static bool IsWhitespaceText(PCQQMessageSegment segment)
    {
        return segment.Type == PCQQMessageSegmentType.Text &&
               string.IsNullOrWhiteSpace(segment.Text);
    }

    private static bool IsMentionElement(ReadOnlySpan<byte> value)
    {
        var reader = new PCQQMessageBuffer(value);
        while (reader.TryReadTlv(out var type, out _))
        {
            if (type is 6 or 7)
                return true;
        }

        return false;
    }

    private static ReadOnlySpan<byte> TryReadNestedValue(ReadOnlySpan<byte> value, byte expectedType)
    {
        var reader = new PCQQMessageBuffer(value);
        while (reader.TryReadTlv(out var type, out var nestedValue))
        {
            if (type == expectedType)
                return nestedValue;
        }

        return default;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, PCQQTxField>> ParseTxDataObjects(ReadOnlySpan<byte> data)
    {
        var objects = new List<IReadOnlyDictionary<string, PCQQTxField>>();
        for (var offset = 0; offset <= data.Length - 4; offset++)
        {
            if (data[offset] != (byte)'T' ||
                data[offset + 1] != (byte)'D' ||
                data[offset + 2] != 1 ||
                data[offset + 3] != 1)
            {
                continue;
            }

            if (TryParseTxDataObject(data, offset, out var obj))
                objects.Add(obj);
        }

        return objects;
    }

    private static bool TryParseTxDataObject(
        ReadOnlySpan<byte> data,
        int offset,
        out IReadOnlyDictionary<string, PCQQTxField> fields)
    {
        fields = new Dictionary<string, PCQQTxField>();
        if (offset + 6 > data.Length)
            return false;

        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 4, 2));
        if (count is 0 or > 4096)
            return false;

        var pos = offset + 6;
        var parsedFields = new Dictionary<string, PCQQTxField>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            if (pos + 7 > data.Length)
                return false;

            var type = data[pos++];
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos, 2));
            pos += 2;
            if (nameLength == 0 || nameLength > 4096 || pos + nameLength + 4 > data.Length)
                return false;

            var name = DecodeTxAscii(data.Slice(pos, nameLength));
            pos += nameLength;

            var valueLength = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos, 4));
            pos += 4;
            if (valueLength > data.Length - pos)
                return false;

            parsedFields[name] = new PCQQTxField(type, data.Slice(pos, checked((int)valueLength)).ToArray());
            pos += checked((int)valueLength);
        }

        fields = parsedFields;
        return true;
    }

    private static bool TryGetTxUInt32(
        IReadOnlyDictionary<string, PCQQTxField> fields,
        string name,
        out uint value)
    {
        value = 0;
        if (!fields.TryGetValue(name, out var field))
            return false;

        var raw = field.RawValue;
        switch (field.Type)
        {
            case 0x01 or 0x05 or 0x06 or 0x07 when raw.Length == 4:
                value = BinaryPrimitives.ReadUInt32LittleEndian(raw);
                return true;
            case 0x03 or 0x04 when raw.Length == 2:
                value = BinaryPrimitives.ReadUInt16LittleEndian(raw);
                return true;
            case 0x02 when raw.Length == 1:
                value = raw[0];
                return true;
            default:
                return false;
        }
    }

    private static string DecodeTxAscii(ReadOnlySpan<byte> raw)
    {
        var decoded = DecodeTxObfuscated(raw);
        return Encoding.ASCII.GetString(decoded)
            .Replace("\0", string.Empty)
            .Trim();
    }

    private static byte[] DecodeTxObfuscated(ReadOnlySpan<byte> raw)
    {
        var key = (byte)((raw.Length & 0xFF) ^ ((raw.Length >> 8) & 0xFF));
        var output = new byte[raw.Length];
        for (var i = 0; i < raw.Length; i++)
            output[i] = (byte)(((~raw[i]) & 0xFF) ^ key);

        return output;
    }

    private static string? NormalizeImageRelativePath(string value)
    {
        var path = value.Trim();
        if (path.Length == 0)
            return null;

        foreach (var prefix in new[] { "UserDataImage:", "DataImage:" })
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[prefix.Length..];
                break;
            }
        }

        path = path.TrimStart('\\', '/');
        return path.Length == 0 ? null : path;
    }

    private static string? DecodeNicknameElement(ReadOnlySpan<byte> value)
    {
        var reader = new PCQQMessageBuffer(value);
        while (reader.TryReadTlv(out var type, out var nestedValue))
        {
            if (type is not (1 or 2))
                continue;

            var text = CleanupText(DecodeUtf16(nestedValue));
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return null;
    }

    private static IEnumerable<string> ExtractUtf16LengthPrefixedStrings(byte[] content)
    {
        for (var i = 0; i + 4 <= content.Length; i++)
        {
            var byteLength = BitConverter.ToUInt16(content, i);
            if (byteLength < 2 ||
                byteLength > 4096 ||
                (byteLength & 1) != 0 ||
                i + 2 + byteLength > content.Length)
            {
                continue;
            }

            var text = Unicode.GetString(content, i + 2, byteLength);
            if (LooksLikeUserText(text))
            {
                yield return text;
                i += byteLength + 1;
            }
        }
    }

    private static string DecodeUtf16(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
            return string.Empty;

        if ((value.Length & 1) != 0)
            value = value[..^1];

        return value.IsEmpty ? string.Empty : Unicode.GetString(value);
    }

    private static int DecodeBigEndianInt(ReadOnlySpan<byte> value)
    {
        var result = 0;
        foreach (var item in value)
        {
            result = (result << 8) | item;
        }

        return result;
    }

    private static bool LooksLikeUserText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2048)
            return false;

        var useful = 0;
        foreach (var ch in text)
        {
            if (char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t')
                return false;

            if (char.IsLetterOrDigit(ch) ||
                char.IsPunctuation(ch) ||
                char.IsSymbol(ch) ||
                char.IsWhiteSpace(ch) ||
                IsCjk(ch))
            {
                useful++;
            }
        }

        return useful > 0;
    }

    private static bool IsCjk(char ch)
    {
        return ch is >= '\u3400' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF';
    }

    private static string CleanupText(string text)
    {
        var cleaned = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch == '\0')
                continue;

            if (char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t')
                continue;

            cleaned.Append(ch);
        }

        return cleaned.ToString().Trim();
    }

    private static string NormalizeDisplayText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // PCQQ 的 @ 消息经常把 @ 名称作为两个相邻文本片段保存，预览时需要按 QQ 的显示去重。
        string previous;
        do
        {
            previous = text;
            text = AdjacentDuplicateMentionRegex.Replace(text, "$1");
        } while (!string.Equals(previous, text, StringComparison.Ordinal));

        return text;
    }

    private ref struct PCQQMessageBuffer
    {
        private readonly ReadOnlySpan<byte> _buffer;
        private int _offset;

        public PCQQMessageBuffer(ReadOnlySpan<byte> buffer)
        {
            _buffer = buffer;
            _offset = 0;
        }

        public bool Skip(int count)
        {
            if (count < 0 || _offset + count > _buffer.Length)
                return false;

            _offset += count;
            return true;
        }

        public bool TryReadUInt32(out uint value)
        {
            if (_offset + 4 > _buffer.Length)
            {
                value = 0;
                return false;
            }

            value = BitConverter.ToUInt32(_buffer[_offset..(_offset + 4)]);
            _offset += 4;
            return true;
        }

        public bool TryReadUInt16(out ushort value)
        {
            if (_offset + 2 > _buffer.Length)
            {
                value = 0;
                return false;
            }

            value = BitConverter.ToUInt16(_buffer[_offset..(_offset + 2)]);
            _offset += 2;
            return true;
        }

        public bool TryReadTlv(out byte type, out ReadOnlySpan<byte> value)
        {
            if (_offset + 3 > _buffer.Length)
            {
                type = 0;
                value = default;
                return false;
            }

            type = _buffer[_offset];
            var length = BitConverter.ToUInt16(_buffer[(_offset + 1)..(_offset + 3)]);
            if (_offset + 3 + length > _buffer.Length)
            {
                value = default;
                return false;
            }

            value = _buffer.Slice(_offset + 3, length);
            _offset += 3 + length;
            return true;
        }
    }
}

public sealed record PCQQParsedMessage(
    string Text,
    string DisplayText,
    string? SenderNickname,
    long MessageSeq,
    PCQQReplyMessage? Reply,
    IReadOnlyList<PCQQMessageSegment> Segments)
{
    public static PCQQParsedMessage Empty { get; } = new(string.Empty, string.Empty, null, 0, null, []);
    public static PCQQParsedMessage Unknown { get; } = new(string.Empty, "[PCQQ消息]", null, 0, null, []);
}

public sealed record PCQQMessageSegment(
    PCQQMessageSegmentType Type,
    string Text,
    int? FaceId,
    string? ImageRelativePath,
    int? ImageWidth,
    int? ImageHeight,
    bool IsMention)
{
    public static PCQQMessageSegment CreateText(string text) =>
        new(PCQQMessageSegmentType.Text, text, null, null, null, null, false);

    public static PCQQMessageSegment CreateMention(string text) =>
        new(PCQQMessageSegmentType.Text, text, null, null, null, null, true);

    public static PCQQMessageSegment CreateFace(int faceId, string text) =>
        new(PCQQMessageSegmentType.Face, text, faceId, null, null, null, false);

    public static PCQQMessageSegment CreateImage(string? imageRelativePath, int? width, int? height) =>
        new(PCQQMessageSegmentType.Image, "[图片]", null, imageRelativePath, width, height, false);
}

sealed record PCQQImageElement(string? RelativePath, int? Width, int? Height);

sealed record PCQQTxField(byte Type, byte[] RawValue);

sealed record PCQQReplyPayload(long MessageSeq, uint SenderUin, IReadOnlyList<string> PreviewParts);

public sealed record PCQQReplyMessage(
    long MessageSeq,
    uint SenderUin,
    string PreviewText);

public enum PCQQMessageSegmentType
{
    Text,
    Face,
    Image,
}
