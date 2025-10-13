using Google.Protobuf;
using System.Text;
using System.Text.Json;

namespace QQDatabaseReader.Database;

/// <summary>
/// Protobuf 逆向解析器 - 无需 .proto 定义，直接从 byte[] 解析成 JSON
/// </summary>
public static class ProtobufReverseParser
{
    /// <summary>
    /// 将 Protobuf 二进制数据解析为 JSON 字符串
    /// </summary>
    /// <param name="data">Protobuf 二进制数据</param>
    /// <param name="indent">是否格式化输出</param>
    public static string ToJson(byte[] data, bool indent = true)
    {
        var result = ParseUnknownMessage(data);
        
        var options = new JsonSerializerOptions 
        { 
            WriteIndented = indent,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        
        return JsonSerializer.Serialize(result, options);
    }

    /// <summary>
    /// 解析未知的 Protobuf 消息
    /// </summary>
    public static Dictionary<int, object?> ParseUnknownMessage(byte[] data)
    {
        var result = new Dictionary<int, object?>();
        var input = new CodedInputStream(data);

        while (!input.IsAtEnd)
        {
            uint tag = input.ReadTag();
            if (tag == 0) break;

            int key = WireFormat.GetTagFieldNumber(tag);
            WireFormat.WireType wireType = WireFormat.GetTagWireType(tag);

            object? value = null;

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
                        value = ParseLengthDelimited(bytes);
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

            // 如果同一个字段出现多次（repeated），转为数组
            if (result.ContainsKey(key))
            {
                if (result[key] is List<object?> list)
                {
                    list.Add(value);
                }
                else
                {
                    result[key] = new List<object?> { result[key], value };
                }
            }
            else
            {
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// 解析 Length-Delimited 类型（可能是 string, bytes 或嵌套消息）
    /// </summary>
    private static object ParseLengthDelimited(ByteString bytes)
    {
        var byteArray = bytes.ToByteArray();

        // 尝试作为 UTF-8 字符串解析
        if (TryParseAsString(byteArray, out string? str))
        {
            return str!;
        }

        // 尝试作为嵌套的 Protobuf 消息解析
        if (TryParseAsMessage(byteArray, out var message))
        {
            return message!;
        }

        // 否则作为 bytes（base64 编码）
        return $"<bytes: {Convert.ToBase64String(byteArray)}>";
    }

    /// <summary>
    /// 尝试将字节数组解析为 UTF-8 字符串
    /// </summary>
    private static bool TryParseAsString(byte[] data, out string? result)
    {
        result = null;
        
        if (data.Length == 0)
        {
            result = "";
            return true;
        }

        try
        {
            // 检查是否为有效的 UTF-8
            var str = Encoding.UTF8.GetString(data);
            
            // 检查是否包含不可打印字符（排除常见的空白字符）
            bool hasUnprintable = str.Any(c => 
                char.IsControl(c) && c != '\n' && c != '\r' && c != '\t');
            
            if (!hasUnprintable)
            {
                result = str;
                return true;
            }
        }
        catch
        {
            // UTF-8 解码失败
        }

        return false;
    }

    /// <summary>
    /// 尝试将字节数组解析为嵌套的 Protobuf 消息
    /// </summary>
    private static bool TryParseAsMessage(byte[] data, out Dictionary<int, object?>? result)
    {
        result = null;

        if (data.Length == 0)
        {
            return false;
        }

        try
        {
            // 尝试解析为 Protobuf 消息
            var parsed = ParseUnknownMessage(data);
            
            // 如果成功解析到至少一个字段，认为是有效消息
            if (parsed.Count > 0)
            {
                result = parsed;
                return true;
            }
        }
        catch
        {
            // 解析失败
        }

        return false;
    }
}
