using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;

namespace QQDatabaseExplorer.Models;

public sealed record ForwardedMessageCard(
    string Title,
    string Footer,
    IReadOnlyList<string> PreviewLines,
    string? Resid,
    string? Uniseq,
    string? FileName,
    int? MessageCount,
    string RawPayload)
{
    public string CopyText => "[聊天记录]";
}

public static class ForwardedMessageCardParser
{
    public static bool TryParse(
        string? appJson,
        string? appResid,
        string? appUniseq,
        out ForwardedMessageCard? card)
    {
        card = null;
        if (string.IsNullOrWhiteSpace(appJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(appJson);
            var root = document.RootElement;
            if (!string.Equals(GetString(root, "app"), "com.tencent.multimsg", StringComparison.Ordinal))
                return false;

            var detail = TryGetProperty(root, "meta", out var meta) &&
                         TryGetProperty(meta, "detail", out var detailElement)
                ? detailElement
                : default;

            var desc = GetString(root, "desc");
            var title = FirstNonEmpty(GetString(detail, "source"), NormalizeBracketTitle(desc), "聊天记录");
            var summary = GetString(detail, "summary");
            var resid = FirstNonEmpty(GetString(detail, "resid"), appResid);
            var uniseq = FirstNonEmpty(GetString(detail, "uniseq"), appUniseq);

            string? fileName = null;
            int? messageCount = null;
            if (GetString(root, "extra") is { } extra)
            {
                TryReadExtra(extra, out fileName, out messageCount);
            }

            var previewLines = ReadPreviewLines(detail);
            var footer = FirstNonEmpty(
                summary,
                messageCount is > 0 ? $"查看{messageCount.Value}条转发消息" : null,
                "查看转发消息");

            card = new ForwardedMessageCard(
                title,
                footer,
                previewLines,
                resid,
                uniseq,
                fileName,
                messageCount,
                appJson);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryParseXml(
        string? xml,
        string? xmlResid,
        string? xmlFileName,
        out ForwardedMessageCard? card)
    {
        card = null;
        if (string.IsNullOrWhiteSpace(xml))
            return false;

        try
        {
            var root = XElement.Parse(xml);
            if (!string.Equals((string?)root.Attribute("action"), "viewMultiMsg", StringComparison.Ordinal) &&
                !string.Equals((string?)root.Attribute("brief"), "[聊天记录]", StringComparison.Ordinal))
            {
                return false;
            }

            var titleElements = root
                .Descendants("item")
                .Elements("title")
                .Select(element => (element.Value ?? string.Empty).Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            var title = FirstNonEmpty(
                titleElements.FirstOrDefault(),
                (string?)root.Element("source")?.Attribute("name"),
                "聊天记录");
            var previewLines = titleElements.Skip(1).ToArray();
            var messageCount = TryParseInt((string?)root.Attribute("tSum"));
            var footer = FirstNonEmpty(
                root.Descendants("summary").Select(element => element.Value?.Trim()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                messageCount is > 0 ? $"查看{messageCount.Value}条转发消息" : null,
                "查看转发消息");

            card = new ForwardedMessageCard(
                title,
                footer,
                previewLines,
                FirstNonEmpty((string?)root.Attribute("m_resid"), xmlResid),
                null,
                FirstNonEmpty((string?)root.Attribute("m_fileName"), xmlFileName),
                messageCount,
                xml);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryParse(
        string? appJson,
        string? appResid,
        string? appUniseq,
        string? xml,
        string? xmlResid,
        string? xmlFileName,
        out ForwardedMessageCard? card)
    {
        return TryParse(appJson, appResid, appUniseq, out card) ||
               TryParseXml(xml, xmlResid, xmlFileName, out card);
    }

    private static IReadOnlyList<string> ReadPreviewLines(JsonElement detail)
    {
        if (detail.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(detail, "news", out var news) ||
            news.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var lines = new List<string>();
        foreach (var item in news.EnumerateArray())
        {
            var text = GetString(item, "text");
            if (!string.IsNullOrWhiteSpace(text))
                lines.Add(text);
        }

        return lines;
    }

    private static void TryReadExtra(string extra, out string? fileName, out int? messageCount)
    {
        fileName = null;
        messageCount = null;

        try
        {
            using var document = JsonDocument.Parse(extra.Trim());
            var root = document.RootElement;
            fileName = GetString(root, "filename");
            if (TryGetProperty(root, "tsum", out var tsum) && tsum.TryGetInt32(out var count))
            {
                messageCount = count;
            }
        }
        catch
        {
        }
    }

    private static string? NormalizeBracketTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']'
            ? trimmed[1..^1]
            : trimmed;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        if (element.ValueKind == JsonValueKind.Object)
            return element.TryGetProperty(propertyName, out property);

        property = default;
        return false;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }

    private static int? TryParseInt(string? value)
    {
        return int.TryParse(value, out var result) ? result : null;
    }
}
