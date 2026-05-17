using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Google.Protobuf;

namespace QQDatabaseExplorer.ViewModels;

public partial class ProtobufAnalyzerDialogViewModel : ObservableObject
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    [ObservableProperty]
    public partial string SourceBase64 { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string JsonView { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasData { get; set; }

    /// <summary>0=Tree, 1=JSON</summary>
    [ObservableProperty]
    public partial int ViewMode { get; set; }

    public ObservableCollection<ProtobufFieldNode> RootNodes { get; } = new();

    public void LoadFromBase64(string base64)
    {
        SourceBase64 = base64;
        ParseAndDisplay();
    }

    public void LoadFromHex(string hex)
    {
        SourceBase64 = hex;
        ParseAndDisplay();
    }

    [RelayCommand]
    private void Parse() => ParseAndDisplay();

    [RelayCommand]
    private void Clear()
    {
        SourceBase64 = string.Empty;
        RootNodes.Clear();
        JsonView = string.Empty;
        HasData = false;
        ViewMode = 0;
        StatusText = string.Empty;
    }

    [RelayCommand]
    private void SwitchToTree() => ViewMode = 0;

    [RelayCommand]
    private void SwitchToJson() => ViewMode = 1;

    private void ParseAndDisplay()
    {
        RootNodes.Clear();
        JsonView = string.Empty;
        HasData = false;
        StatusText = string.Empty;

        if (string.IsNullOrWhiteSpace(SourceBase64))
            return;

        try
        {
            byte[] data;
            var input = SourceBase64.Trim();

            if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || IsHexString(input))
                data = HexToBytes(input);
            else
                data = Convert.FromBase64String(input);

            HasData = true;
            ViewMode = 0;
            var byteOffset = 0;
            var rootInput = new CodedInputStream(data);
            int segIdx = 0;

            while (!rootInput.IsAtEnd)
            {
                uint tag = rootInput.ReadTag();
                if (tag == 0) break;

                int fieldNum = WireFormat.GetTagFieldNumber(tag);
                var wireType = WireFormat.GetTagWireType(tag);
                int tagSize = CodedOutputStream.ComputeTagSize(fieldNum);

                if (fieldNum == 40800 && wireType == WireFormat.WireType.LengthDelimited)
                {
                    var segBytes = rootInput.ReadBytes().ToByteArray();
                    int totalSize = tagSize + CodedOutputStream.ComputeLengthSize(segBytes.Length) + segBytes.Length;

                    var segNode = new ProtobufFieldNode
                    {
                        FieldNumber = 40800,
                        FieldDisplay = "40800",
                        WireType = "LD",
                        Description = $"segment[{segIdx}]",
                        DecodedValue = $"({segBytes.Length}B)",
                        CopyValue = Convert.ToBase64String(segBytes),
                        ByteStart = byteOffset,
                        ByteEnd = byteOffset + totalSize - 1,
                        RawHex = BytesToHex(segBytes),
                        RawBase64 = Convert.ToBase64String(segBytes),
                        IsSegment = true,
                    };

                    ParseFieldsRecursive(segBytes, segNode);
                    RootNodes.Add(segNode);
                    byteOffset += totalSize;
                    segIdx++;
                }
                else
                {
                    RootNodes.Add(ParseSingleField(rootInput, fieldNum, wireType, ref byteOffset, tagSize));
                }
            }

            JsonView = GenerateJson(data);
        }
        catch (FormatException) { /* no-op, status already empty or set elsewhere */ }
        catch (Exception ex) { StatusText = $"解析失败: {ex.Message}"; }
    }

    private static void ParseFieldsRecursive(byte[] data, ProtobufFieldNode parent)
    {
        var input = new CodedInputStream(data);
        int offset = 0;
        while (!input.IsAtEnd)
        {
            uint tag = input.ReadTag();
            if (tag == 0) break;
            int fieldNum = WireFormat.GetTagFieldNumber(tag);
            var wireType = WireFormat.GetTagWireType(tag);
            int tagSize = CodedOutputStream.ComputeTagSize(fieldNum);
            parent.Children.Add(ParseSingleField(input, fieldNum, wireType, ref offset, tagSize));
        }
    }

    private static ProtobufFieldNode ParseSingleField(
        CodedInputStream input, int fieldNum, WireFormat.WireType wireType,
        ref int byteOffset, int tagSize)
    {
        try
        {
            return ParseSingleFieldStrict(input, fieldNum, wireType, ref byteOffset, tagSize);
        }
        catch (Exception ex)
        {
            var valueStart = byteOffset + tagSize;
            var node = new ProtobufFieldNode
            {
                FieldNumber = fieldNum,
                FieldDisplay = fieldNum.ToString(),
                WireType = WireTypeShort(wireType),
                Description = GetFieldDescription(fieldNum),
                ByteStart = byteOffset,
                DecodedValue = $"<error: {ex.Message}>",
            };
            node.CopyValue = node.DecodedValue;
            node.ByteEnd = valueStart;
            byteOffset = valueStart;
            return node;
        }
    }

    private static ProtobufFieldNode ParseSingleFieldStrict(
        CodedInputStream input, int fieldNum, WireFormat.WireType wireType,
        ref int byteOffset, int tagSize)
    {
        var node = new ProtobufFieldNode
        {
            FieldNumber = fieldNum,
            FieldDisplay = fieldNum.ToString(),
            WireType = WireTypeShort(wireType),
            Description = GetFieldDescription(fieldNum),
            ByteStart = byteOffset,
        };

        int valueStart = byteOffset + tagSize;

        switch (wireType)
        {
            case WireFormat.WireType.Varint:
                var v = input.ReadUInt64();
                int vs = CodedOutputStream.ComputeUInt64Size(v);
                node.DecodedValue = InterpretVarint(fieldNum, v);
                node.CopyValue = node.DecodedValue;
                node.ByteEnd = valueStart + vs - 1;
                byteOffset = node.ByteEnd + 1;
                break;
            case WireFormat.WireType.Fixed64:
                var f64 = input.ReadFixed64();
                node.DecodedValue = $"{f64} | double:{BitConverter.Int64BitsToDouble((long)f64):G17}";
                node.CopyValue = node.DecodedValue;
                node.RawHex = $"0x{f64:X16}";
                node.ByteEnd = valueStart + 7;
                byteOffset = node.ByteEnd + 1;
                break;
            case WireFormat.WireType.LengthDelimited:
                var bytes = input.ReadBytes().ToByteArray();
                int ls = CodedOutputStream.ComputeLengthSize(bytes.Length);
                node.RawHex = BytesToHex(bytes);
                node.RawBase64 = Convert.ToBase64String(bytes);
                node.ByteLength = bytes.Length;
                node.ByteEnd = valueStart + ls + bytes.Length - 1;
                byteOffset = node.ByteEnd + 1;
                node.DecodedValue = DecodeLD(fieldNum, bytes, node);
                break;
            case WireFormat.WireType.Fixed32:
                var f32 = input.ReadFixed32();
                node.DecodedValue = $"{f32} | float:{BitConverter.Int32BitsToSingle((int)f32):G9}";
                node.CopyValue = node.DecodedValue;
                node.RawHex = $"0x{f32:X8}";
                node.ByteEnd = valueStart + 3;
                byteOffset = node.ByteEnd + 1;
                break;
            default:
                throw new InvalidOperationException($"Unknown wire type: {wireType}");
        }

        return node;
    }

    private static string WireTypeShort(WireFormat.WireType wt) => wt switch
    {
        WireFormat.WireType.Varint => "Varint",
        WireFormat.WireType.Fixed64 => "F64",
        WireFormat.WireType.LengthDelimited => "LD",
        WireFormat.WireType.Fixed32 => "F32",
        WireFormat.WireType.StartGroup => "SG",
        WireFormat.WireType.EndGroup => "EG",
        _ => "?",
    };

    private static string DecodeLD(int fieldNum, byte[] bytes, ProtobufFieldNode node)
    {
        if (IsRawHexField(fieldNum))
        {
            var rawHex = BytesToLowerHex(bytes);
            node.CopyValue = rawHex;
            return rawHex;
        }

        // 1. 已知文本字段优先 UTF-8
        if (IsTextField(fieldNum) && TryUtf8(bytes, out var text))
        {
            node.CopyValue = text;
            return text;
        }

        // 2. 尝试递归解析嵌套 protobuf（自适应性，和 protobuf-decoder 一致）
        if (TryDecodeNested(bytes, node))
        {
            node.CopyValue = node.RawBase64 ?? BytesToHex(bytes);
            return $"{{{node.Children.Count} fields}}";
        }

        // 3. 非已知文本字段但 UTF-8 可读
        if (!IsTextField(fieldNum) && TryUtf8(bytes, out var utf8))
        {
            node.CopyValue = utf8;
            return utf8;
        }

        // 4. 回退到十六进制
        var hex = BytesToSpacedHex(bytes);
        node.CopyValue = hex;
        return hex;
    }

    private static bool TryDecodeNested(byte[] bytes, ProtobufFieldNode node)
    {
        if (bytes.Length == 0)
            return false;

        try
        {
            var input = new CodedInputStream(bytes);
            int off = 0;
            var children = new List<ProtobufFieldNode>();
            while (!input.IsAtEnd)
            {
                uint t = input.ReadTag();
                if (t == 0)
                    return false;
                int fn = WireFormat.GetTagFieldNumber(t);
                var wt = WireFormat.GetTagWireType(t);
                int ts = CodedOutputStream.ComputeTagSize(fn);
                children.Add(ParseSingleFieldStrict(input, fn, wt, ref off, ts));
            }

            if (children.Count == 0 || off != bytes.Length)
                return false;

            foreach (var child in children)
                node.Children.Add(child);

            return true;
        }
        catch { return false; }
    }

    // ==================== 工具方法 ====================

    private static bool IsHexString(string s)
    {
        s = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        return s.Length > 1 && s.Length % 2 == 0 && s.All(char.IsAsciiHexDigit);
    }

    private static byte[] HexToBytes(string hex)
    {
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        if (hex.Length % 2 != 0) hex = "0" + hex;
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static string BytesToHex(byte[] b)
    {
        var sb = new StringBuilder(b.Length * 2);
        foreach (var x in b) sb.Append(x.ToString("X2"));
        return sb.ToString();
    }

    private static string BytesToLowerHex(byte[] b)
    {
        var sb = new StringBuilder(b.Length * 2);
        foreach (var x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>protobuf-decoder 风格: 小写空格分隔, e.g. "59 e7 8d de cf 50 d3 39"</summary>
    private static string BytesToSpacedHex(byte[] b)
    {
        var sb = new StringBuilder(b.Length * 3);
        for (int i = 0; i < b.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(b[i].ToString("x2"));
        }
        return sb.ToString();
    }

    private static bool TryUtf8(byte[] bytes, out string text)
    {
        try
        {
            text = StrictUtf8.GetString(bytes);
            int bad = text.Count(c => c < 0x20 && c != '\n' && c != '\r' && c != '\t');
            if (bad < text.Length * 0.05) return true;
        }
        catch { }
        text = string.Empty;
        return false;
    }

    private static bool IsTextField(int fieldNum) => fieldNum switch
    {
        >= 45101 and <= 45130 => true,
        45402 or 45403 or 45422 => true,
        45503 or 45504 => true,
        >= 45802 and <= 45804 => true,
        45812 or 45815 or 45816 or 45986 => true,
        47405 or 47413 or 47414 or 47415 => true,
        47602 => true,
        47703 or 47704 or 47705 or 47706 or 47713 or 47714 or 47715 => true,
        80900 => true,
        49154 => true,
        _ => false,
    };

    private static bool IsRawHexField(int fieldNum) => fieldNum switch
    {
        // 商城表情图片 ID 是 16 字节原始值。无 schema 递归解码会把它误判成 protobuf；
        // UI 查找 nt_data/Emoji/marketface/<表情包 ID>/<图片 ID> 本体文件时需要直接使用十六进制。
        80903 => true,
        _ => false,
    };

    // ==================== 值解释 ====================

    private static string InterpretVarint(int fieldNum, ulong value)
    {
        var lines = new List<string> { $"As uint: {value}" };

        // zigzag decode (protobuf-decoder 风格: 始终尝试 sint 解释)
        long sint = (long)(value >> 1) ^ -(long)(value & 1);
        if (sint != (long)value)
            lines.Add($"As sint: {sint}");

        // 二进制补码解释 — 仅在对应位宽 MSB 置位即负数解释不同于无符号时显示
        if (value <= 0xFF && (value >> 7) != 0)
        {
            sbyte i8 = unchecked((sbyte)(byte)value);
            lines.Add($"As int8: {i8}");
        }
        else if (value <= 0xFFFF && (value >> 15) != 0)
        {
            short i16 = unchecked((short)(ushort)value);
            lines.Add($"As int16: {i16}");
        }
        else if (value <= 0xFFFF_FFFF && (value >> 31) != 0)
        {
            int i32 = unchecked((int)(uint)value);
            lines.Add($"As int32: {i32}");
        }
        else if (value > 0xFFFF_FFFF && (value >> 63) != 0)
        {
            long i64 = unchecked((long)value);
            lines.Add($"As int64: {i64}");
        }

        // 时间戳检测
        if (value > 1_000_000_000 && value < 2_000_000_000)
        {
            try
            {
                var dt = DateTimeOffset.FromUnixTimeSeconds((long)value).LocalDateTime;
                lines.Add(dt.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch { }
        }

        // QQ 字段特定提示
        var hint = fieldNum switch
        {
            45002 => GuessSegmentType((int)(uint)value),
            45003 => GuessFaceType((int)(uint)value),
            45416 => GuessImageFormat((int)(uint)value),
            45550 => GuessFileType((int)(uint)value),
            _ => null
        };
        if (hint is not null) lines.Add(hint);

        return string.Join("\n", lines);
    }

    // ==================== QQ 字段描述 ====================

    public static string GetFieldDescription(int fieldNum) => fieldNum switch
    {
        40010 => "ChatType(1=私聊 2=群聊)",
        40020 => "SenderUid",
        40021 => "PeerUid",
        40800 => "消息段(repeated)",
        40801 => "辅助数据",

        45001 => "段ID",
        45002 => "段类型",
        45003 => "FaceType",
        45004 => "位标志",

        45101 => "文本内容",
        45102 => "标志",
        45103 => "attr3",
        45104 => "attr4",
        45105 => "attr5",
        45106 => "attr6",
        45108 => "attr8",
        45109 => "attr9",
        45110 => "attr10",
        45111 => "attr11",
        45112 => "链接元数据",

        45402 => "文件hash",
        45403 => "源路径",
        45405 => "字节数",
        45406 => "MD5(16B)",
        45407 => "SHA1?(16B)",
        45408 => "hash(20B)",
        45409 => "hash(20B)",
        45411 => "图片宽度",
        45412 => "图片高度",
        45413 => "尺寸属性",
        45414 => "尺寸属性",
        45415 => "过期天数",
        45416 => "图片格式(1000=贴纸 1001=普通)",
        45418 => "标志",
        45421 => "文件大小",
        45422 => "显示文件名",
        45424 => "贴纸元数据",

        45501 => "文件子类型",
        45503 => "rkey",
        45504 => "文件元数据",
        45505 => "时间戳相关",
        45510 => "标志",
        45511 => "下载标志1",
        45512 => "下载标志2",
        45513 => "下载标志3",
        45514 => "下载标志4",
        45517 => "过期时间戳",
        45518 => "过期时长(秒)",
        45526 => "文件Extra",
        45550 => "文件类型(1=视频 2=图片 4=文件)",

        45601 => "语音时长(秒)",

        45802 => "缩略图URL",
        45803 => "大图URL",
        45804 => "原图URL",
        45805 => "标志",
        45812 => "本地路径",
        45815 => "附带文字",
        45816 => "服务器域名",
        45817 => "download17",
        45818 => "download18",
        45819 => "download19",
        45820 => "download20",
        45821 => "download21",
        45822 => "download22",
        45823 => "download23",
        45824 => "download24",
        45825 => "download25",
        45826 => "download26",
        45827 => "download27",
        45828 => "download28",

        45954 => "路径Extra",
        45986 => "文件UUID",

        47401 => "回复段ID",
        47402 => "被回复msgSeq",
        47403 => "被回复者QQ",
        47404 => "被回复时间戳",
        47410 => "被回复来源元数据",
        47411 => "被回复会话数字ID",
        47412 => "被回复来源群名",
        47413 => "被回复文本预览",
        47415 => "回复标志",
        47416 => "被回复msgId",
        47418 => "回复标志2",
        47419 => "被回复seq2",
        47422 => "被回复原始ID",
        47423 => "被回复段",

        47601 => "小黄脸ID",
        47602 => "表情名称",

        47702 => "系统消息类型",
        47703 => "操作者UID",
        47704 => "目标UID",
        47705 => "操作者昵称",
        47706 => "操作者摘要",
        47710 => "原消息段",
        47711 => "系统标志",
        47713 => "群名片",
        47714 => "昵称(重复)",
        47715 => "摘要(重复)",

        80810 => "商城表情包ID(packId)",
        80900 => "商城表情名称",
        80903 => "商城表情图片ID(原始字节转十六进制)",
        80909 => "商城表情宽度",
        80910 => "商城表情高度",

        49154 => "辅助标志",
        49155 => "辅助时间戳",
        _ => "",
    };

    private static string GuessSegmentType(int t) => t switch
    {
        1 => "文本", 2 => "媒体", 3 => "文件", 4 => "语音",
        6 => "QQFace", 7 => "回复", 8 => "系统消息", 10 => "应用卡片", 11 => "商城表情",
        _ => $"?{t}"
    };

    private static string GuessFaceType(int t) => t switch
    {
        0 => "Pic", 1 => "Emoji", 2 => "SuperEmoji",
        9 => "收文件", 11 => "发文件",
        _ => $"?{t}"
    };

    private static string GuessImageFormat(int f) => f switch
    { 1000 => "贴纸", 1001 => "普通", 2000 => "其他", _ => $"{f}" };

    private static string GuessFileType(int t) => t switch
    { 1 => "视频", 2 => "图片", 4 => "文件", _ => $"{t}" };

    // ==================== JSON 生成 (raw keys only) ====================

    private static string GenerateJson(byte[] data)
    {
        try
        {
            var dict = ProtobufToDict(data);
            return JsonSerializer.Serialize(dict, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
        catch (Exception ex) { return $"JSON error: {ex.Message}"; }
    }

    private static Dictionary<string, object?> ProtobufToDict(byte[] data)
    {
        var result = new Dictionary<string, object?>();
        var input = new CodedInputStream(data);
        int segIdx = 0;

        while (!input.IsAtEnd)
        {
            uint tag = input.ReadTag();
            if (tag == 0) break;
            int fn = WireFormat.GetTagFieldNumber(tag);
            var wt = WireFormat.GetTagWireType(tag);

            object? value = wt switch
            {
                WireFormat.WireType.Varint => input.ReadUInt64().ToString(),
                WireFormat.WireType.Fixed64 => input.ReadFixed64(),
                WireFormat.WireType.Fixed32 => input.ReadFixed32(),
                WireFormat.WireType.LengthDelimited => JsonDecodeLD(input.ReadBytes().ToByteArray(), fn),
                _ => "<unknown>"
            };

            string key = fn == 40800 ? $"seg[{segIdx++}]" : fn.ToString();

            if (result.TryGetValue(key, out var existing))
            {
                if (existing is List<object?> l) l.Add(value);
                else result[key] = new List<object?> { existing, value };
            }
            else result[key] = value;
        }
        return result;
    }

    private static object? JsonDecodeLD(byte[] bytes, int fn)
    {
        if (IsRawHexField(fn)) return BytesToLowerHex(bytes);
        if (IsTextField(fn) && TryUtf8(bytes, out var text)) return text;
        try { var n = ProtobufToDict(bytes); if (n.Count > 0) return n; } catch { }
        if (TryUtf8(bytes, out var utf8)) return utf8;
        return Convert.ToBase64String(bytes);
    }
}

// ==================== 树节点模型 ====================

public partial class ProtobufFieldNode : ObservableObject
{
    public int FieldNumber { get; set; }
    public string FieldDisplay { get; set; } = "";
    public string WireType { get; set; } = "";
    public string Description { get; set; } = "";
    public string? DecodedValue { get; set; }
    public string? CopyValue { get; set; }
    public string? RawHex { get; set; }
    public string? RawBase64 { get; set; }
    public int ByteLength { get; set; }
    public int ByteStart { get; set; }
    public int ByteEnd { get; set; }
    public ObservableCollection<ProtobufFieldNode> Children { get; } = new();

    /// <summary>是否为 40800 消息段根节点 (用于画分隔线)</summary>
    [ObservableProperty]
    public partial bool IsSegment { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;
}

// ==================== ViewMode 相等转换器 ====================

public class ViewModeEqualityConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly ViewModeEqualityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is int mode && parameter is string s && int.TryParse(s, out var target))
            return mode == target;
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
