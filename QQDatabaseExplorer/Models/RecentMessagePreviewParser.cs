using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Google.Protobuf;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.Models;

public static class RecentMessagePreviewParser
{
    public static string Parse(byte[]? content)
    {
        return Parse(content, messageType: null, subMessageType: null);
    }

    public static string Parse(byte[]? content, MessageType messageType, SubMessageType subMessageType)
    {
        return Parse(content, messageType, (SubMessageType?)subMessageType);
    }

    public static string Parse(byte[]? content, MessageType? messageType, SubMessageType? subMessageType = null)
    {
        if (content is null || content.Length == 0)
            return CreateContextFallbackText(messageType, subMessageType);

        if (messageType != MessageType.Voice &&
            TryCreatePriorityText(messageType, subMessageType, out var rowTypeText))
        {
            return rowTypeText;
        }

        var context = new PreviewContext(messageType, subMessageType);
        if (TryParseRecentWrapperPreview(content, context, out var recentPreviewText))
            return recentPreviewText;

        try
        {
            var message = QQMessageReader.ParseMessage(content);
            return Normalize(CreateDisplayText(message.Segments, context));
        }
        catch
        {
            if (TryParseSingleSegmentPreview(content, context, out var segmentText))
                return segmentText;

            var fallbackText = ParseUnknownPreviewBlob(content, context);
            return string.IsNullOrWhiteSpace(fallbackText)
                ? CreateContextFallbackText(messageType, subMessageType)
                : fallbackText;
        }
    }

    private static bool TryParseRecentWrapperPreview(byte[] content, PreviewContext context, out string text)
    {
        text = string.Empty;
        var input = new CodedInputStream(content);

        try
        {
            while (!input.IsAtEnd)
            {
                var tag = input.ReadTag();
                if (tag == 0)
                    break;

                var fieldNum = WireFormat.GetTagFieldNumber(tag);
                if (fieldNum != 40051 ||
                    WireFormat.GetTagWireType(tag) != WireFormat.WireType.LengthDelimited)
                {
                    input.SkipLastField();
                    continue;
                }

                var value = input.ReadBytes().ToByteArray();
                if (TryParseMessageBytes(value, context, out text) ||
                    TryParseSegmentBytes(value, context, out text))
                {
                    return true;
                }
            }
        }
        catch
        {
            text = string.Empty;
        }

        return false;
    }

    private static bool TryParseMessageBytes(byte[] content, PreviewContext context, out string text)
    {
        text = string.Empty;

        try
        {
            var message = QQMessageReader.ParseMessage(content);
            text = Normalize(CreateDisplayText(message.Segments, context));
            return IsUsefulParsedPreviewText(text);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseSegmentBytes(byte[] content, PreviewContext context, out string text)
    {
        text = string.Empty;

        try
        {
            var segment = QQMessageReader.ParseMessageSegment(content);
            if (!HasDisplayContent(segment))
                return false;

            text = Normalize(CreateDisplayText([segment], context));
            return IsUsefulParsedPreviewText(text);
        }
        catch
        {
            return false;
        }
    }

    private static string CreateDisplayText(IEnumerable<QQMessageSegment> segments, PreviewContext context)
    {
        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            if (segment.Type == MessageSegmentType.Reply)
                continue;

            if (segment.SystemHint is { } systemHint &&
                TryCreateSystemHintDisplayText(systemHint, out var systemHintText))
            {
                builder.Append(systemHintText);
                continue;
            }

            if (segment.IsQQFace && segment.FaceId is { } faceId)
            {
                var face = QQFaceCatalog.Get(faceId);
                builder.Append(CreateQQFacePreviewText(faceId, face));
                continue;
            }

            builder.Append(context.HasMessageContext
                ? QQMessageDisplayText.CreateSegmentText(segment, context.MessageType!.Value, context.SubMessageType!.Value)
                : segment.GetDisplayText());
        }

        return builder.ToString();
    }

    private static bool TryCreateSystemHintDisplayText(QQSystemHintMessage systemHint, out string text)
    {
        text = string.Empty;
        if (!string.IsNullOrWhiteSpace(systemHint.DisplayText))
        {
            text = systemHint.DisplayText.Trim();
            return true;
        }

        if (systemHint.Participants.Count < 2 || string.IsNullOrWhiteSpace(systemHint.Action))
            return false;

        var sourceName = systemHint.Participants[0].Nickname.Trim();
        var targetName = systemHint.Participants[1].Nickname.Trim();
        if (string.IsNullOrWhiteSpace(sourceName) || string.IsNullOrWhiteSpace(targetName))
            return false;

        text = $"{sourceName}{systemHint.Action}{targetName}{systemHint.Suffix}";
        return true;
    }

    private static bool TryParseSingleSegmentPreview(byte[] content, PreviewContext context, out string text)
    {
        text = string.Empty;

        try
        {
            var segment = QQMessageReader.ParseMessageSegment(content);
            if (!HasDisplayContent(segment))
                return false;

            text = Normalize(CreateDisplayText([segment], context));
            return !string.IsNullOrWhiteSpace(text);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasDisplayContent(QQMessageSegment segment)
    {
        return segment.Type != 0 ||
               !string.IsNullOrWhiteSpace(segment.Text) ||
               !string.IsNullOrWhiteSpace(segment.AltText) ||
               segment.FaceId is not null ||
               !string.IsNullOrWhiteSpace(segment.MarketFaceName);
    }

    private static string ParseUnknownPreviewBlob(byte[] content, PreviewContext context)
    {
        var candidates = new List<PreviewCandidate>();
        CollectCandidates(content, candidates, context, depth: 0);

        var normalizedCandidates = candidates
            .Select(candidate => candidate with { Text = Normalize(candidate.Text) })
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Text))
            .GroupBy(candidate => candidate.Text, StringComparer.Ordinal)
            .Select(group => new PreviewCandidateGroup(group.Key, group.Select(candidate => candidate.FieldNum).ToArray()))
            .ToList();

        if (normalizedCandidates.Any(candidate => !candidate.Text.All(char.IsAsciiDigit)))
        {
            normalizedCandidates = normalizedCandidates
                .Where(candidate => !candidate.Text.All(char.IsAsciiDigit))
                .ToList();
        }

        var hasImageDisplayText = normalizedCandidates.Any(IsUsefulImageDisplayText);
        var previewParts = normalizedCandidates
            .Where(candidate => IsUsefulPreviewText(candidate.Text))
            .Where(candidate => !IsRedundantMediaMarker(candidate, hasImageDisplayText))
            .Select(candidate => candidate.Text)
            .Take(4)
            .ToList();

        return previewParts.Count == 0
            ? string.Empty
            : string.Join(" ", previewParts);
    }

    private static void CollectCandidates(byte[] bytes, List<PreviewCandidate> candidates, PreviewContext context, int depth)
    {
        if (depth > 6 || bytes.Length == 0)
            return;

        var input = new CodedInputStream(bytes);
        try
        {
            while (!input.IsAtEnd)
            {
                var tag = input.ReadTag();
                if (tag == 0)
                    break;

                var fieldNum = WireFormat.GetTagFieldNumber(tag);
                switch (WireFormat.GetTagWireType(tag))
                {
                    case WireFormat.WireType.Varint:
                        AddVarintCandidate(fieldNum, input.ReadUInt64(), candidates, context);
                        break;

                    case WireFormat.WireType.Fixed64:
                        input.ReadFixed64();
                        break;

                    case WireFormat.WireType.Fixed32:
                        input.ReadFixed32();
                        break;

                    case WireFormat.WireType.LengthDelimited:
                    {
                        var value = input.ReadBytes().ToByteArray();
                        if (TryDecodeUtf8(value, out var text) && IsPreviewTextField(fieldNum))
                        {
                            candidates.Add(new PreviewCandidate(fieldNum, text));
                        }

                        if (LooksLikeProtobuf(value))
                        {
                            CollectCandidates(value, candidates, context, depth + 1);
                        }
                        else if (TryDecodeUtf8(value, out text))
                        {
                            candidates.Add(new PreviewCandidate(fieldNum, text));
                        }

                        break;
                    }

                    default:
                        input.SkipLastField();
                        break;
                }
            }
        }
        catch
        {
            // recent_contact_v3_table.40051 is a compact QQNT preview blob and may contain
            // partially known nested protobuf. Keep the candidates that were already decoded.
        }
    }

    private static void AddVarintCandidate(int fieldNum, ulong value, List<PreviewCandidate> candidates, PreviewContext context)
    {
        if (fieldNum == 45002 && value <= int.MaxValue)
        {
            var marker = context.HasMessageContext
                ? QQMessageDisplayText.CreateSegmentText(
                    new QQMessageSegment { Type = (MessageSegmentType)(int)value },
                    context.MessageType!.Value,
                    context.SubMessageType!.Value)
                : new QQMessageSegment { Type = (MessageSegmentType)(int)value }.GetDisplayText();

            if (!string.IsNullOrWhiteSpace(marker))
                candidates.Add(new PreviewCandidate(fieldNum, marker));
        }
        else if (fieldNum == 47601 && value <= int.MaxValue)
        {
            var faceId = (int)value;
            var face = QQFaceCatalog.Get(faceId);
            candidates.Add(new PreviewCandidate(fieldNum, CreateQQFacePreviewText(faceId, face)));
        }
    }

    private static string CreateQQFacePreviewText(int faceId, QQFaceInfo? face)
    {
        return face is { Name.Length: > 0 } ? $"[{face.Name}]" : $"[QQ表情:{faceId}]";
    }

    private static bool IsPreviewTextField(int fieldNum) => fieldNum switch
    {
        >= 45101 and <= 45130 => true,
        45422 or 45815 => true,
        47602 => true,
        80900 => true,
        49093 => true,
        47705 or 47707 or 47713 or 47714 or 47715 or 47716 => true,
        49154 => true,
        _ => false,
    };

    private static bool TryDecodeUtf8(byte[] bytes, out string text)
    {
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
            var controlCount = text.Count(ch => char.IsControl(ch) && ch is not ('\r' or '\n' or '\t'));
            return controlCount == 0;
        }
        catch
        {
            text = string.Empty;
            return false;
        }
    }

    private static bool LooksLikeProtobuf(byte[] bytes)
    {
        if (bytes.Length < 2)
            return false;

        var input = new CodedInputStream(bytes);
        var fieldCount = 0;
        try
        {
            while (!input.IsAtEnd && fieldCount < 32)
            {
                var tag = input.ReadTag();
                if (tag == 0)
                    return false;

                switch (WireFormat.GetTagWireType(tag))
                {
                    case WireFormat.WireType.Varint:
                        input.ReadUInt64();
                        break;
                    case WireFormat.WireType.Fixed64:
                        input.ReadFixed64();
                        break;
                    case WireFormat.WireType.Fixed32:
                        input.ReadFixed32();
                        break;
                    case WireFormat.WireType.LengthDelimited:
                        input.ReadBytes();
                        break;
                    default:
                        return false;
                }

                fieldCount++;
            }

            return fieldCount > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUsefulPreviewText(string text)
    {
        if (text.Length == 0 || text.Length > 300)
            return false;

        if (text.StartsWith("u_", StringComparison.Ordinal) && text.Length >= 20)
            return false;

        if (text.Contains('\\', StringComparison.Ordinal) ||
            text.Contains("/download?", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("/gchatpic", StringComparison.OrdinalIgnoreCase))
            return false;

        var fileName = Path.GetFileName(text);
        if (!string.IsNullOrWhiteSpace(fileName) &&
            string.Equals(fileName, text, StringComparison.Ordinal) &&
            IsMediaHashFileName(fileName))
        {
            return false;
        }

        return text.Any(ch => !char.IsWhiteSpace(ch));
    }

    private static bool IsUsefulParsedPreviewText(string text)
    {
        return !string.IsNullOrWhiteSpace(text) &&
               !string.Equals(text, "[系统消息]", StringComparison.Ordinal);
    }

    private static bool IsRedundantMediaMarker(PreviewCandidateGroup candidate, bool hasImageDisplayText)
    {
        return (string.Equals(candidate.Text, "[图片]", StringComparison.Ordinal) ||
                string.Equals(candidate.Text, "[动画表情]", StringComparison.Ordinal)) &&
               hasImageDisplayText &&
               candidate.FieldNums.All(fieldNum => fieldNum == 45002);
    }

    private static bool TryCreatePriorityText(
        MessageType? messageType,
        SubMessageType? subMessageType,
        out string text)
    {
        text = string.Empty;
        if (messageType is null || subMessageType is null)
            return false;

        return QQMessageDisplayText.TryGetPriorityText(messageType.Value, subMessageType.Value, out text);
    }

    private static bool TryCreateFallbackText(
        MessageType? messageType,
        SubMessageType? subMessageType,
        out string text)
    {
        text = string.Empty;
        if (messageType is null || subMessageType is null)
            return false;

        return QQMessageDisplayText.TryGetFallbackText(messageType.Value, subMessageType.Value, out text);
    }

    private static string CreateContextFallbackText(MessageType? messageType, SubMessageType? subMessageType)
    {
        if (TryCreatePriorityText(messageType, subMessageType, out var rowTypeText))
            return rowTypeText;

        return TryCreateFallbackText(messageType, subMessageType, out var fallbackText)
            ? fallbackText
            : string.Empty;
    }

    private static bool IsUsefulImageDisplayText(PreviewCandidateGroup candidate)
    {
        return candidate.FieldNums.Any(IsImageDisplayTextField) &&
               IsUsefulPreviewText(candidate.Text);
    }

    private static bool IsImageDisplayTextField(int fieldNum) => fieldNum switch
    {
        >= 45101 and <= 45130 => true,
        45815 or 47602 or 80900 or 49093 => true,
        _ => false,
    };

    private static bool IsMediaHashFileName(string fileName)
    {
        var name = NormalizeMediaHashName(Path.GetFileNameWithoutExtension(fileName));
        return name.Length is 32 or 40;
    }

    private static string NormalizeMediaHashName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var normalized = name.Trim();
        if (normalized.Length >= 2 && normalized[0] == '{' && normalized[^1] == '}')
            normalized = normalized[1..^1];

        normalized = normalized.Replace("-", string.Empty, StringComparison.Ordinal);
        return normalized.Length > 0 && normalized.All(char.IsAsciiHexDigit)
            ? normalized.ToLowerInvariant()
            : string.Empty;
    }

    private static string Normalize(string text)
    {
        return text
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
    }

    private sealed record PreviewCandidate(int FieldNum, string Text);

    private sealed record PreviewCandidateGroup(string Text, int[] FieldNums);

    private readonly record struct PreviewContext(MessageType? MessageType, SubMessageType? SubMessageType)
    {
        public bool HasMessageContext => MessageType is not null && SubMessageType is not null;

    }
}
