using System;
using System.Net;
using System.Text.Json;

namespace QQDatabaseExplorer.Models;

public sealed record MiniAppCard(
    MiniAppCardKind Kind,
    string AppName,
    string Title,
    string? HostName,
    string? IconUrl,
    string? PreviewUrl,
    string? JumpUrl,
    string RawPayload)
{
    public string CopyText => string.IsNullOrWhiteSpace(Title)
        ? $"[{AppName}]"
        : $"[{AppName}] {Title}";
}

public enum MiniAppCardKind
{
    Generic,
    Bilibili,
}

public static class MiniAppCardParser
{
    private const string MiniAppId = "com.tencent.miniapp_01";
    private const string BilibiliDetailAppId = "1109937557";
    private const string BilibiliRootAppId = "100951776";

    public static bool TryParse(string? appJson, out MiniAppCard? card)
    {
        card = null;
        if (string.IsNullOrWhiteSpace(appJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(appJson);
            var root = document.RootElement;
            if (!string.Equals(GetString(root, "app"), MiniAppId, StringComparison.Ordinal))
                return false;

            if (!TryGetProperty(root, "meta", out var meta))
            {
                return false;
            }

            var detail = TryGetProperty(meta, "detail_1", out var detail1)
                ? detail1
                : TryGetProperty(meta, "news", out var news)
                    ? news
                    : default;
            if (detail.ValueKind != JsonValueKind.Object)
                return false;

            var detailAppId = GetString(detail, "appid");
            var rootAppId = GetString(root, "appID");
            var appName = FirstNonEmpty(
                GetString(detail, "title"),
                TrimPromptPrefix(GetString(root, "prompt")),
                "QQ小程序");
            var isBilibili = string.Equals(detailAppId, BilibiliDetailAppId, StringComparison.Ordinal) ||
                             string.Equals(rootAppId, BilibiliRootAppId, StringComparison.Ordinal) ||
                             appName.Contains("哔哩", StringComparison.Ordinal);
            var jumpUrl = NormalizeUrl(FirstNonEmpty(
                FindKnownUrl(root),
                GetString(detail, "qqdocurl"),
                GetString(detail, "qddocurl"),
                GetString(detail, "url"),
                GetString(detail, "contentJumpUrl"),
                FindStringProperty(root, "pcJumpUrl"),
                FindStringProperty(root, "jumpUrl"),
                FindStringProperty(root, "contentJumpUrl")));
            if (!isBilibili && string.IsNullOrWhiteSpace(jumpUrl))
                return false;

            card = new MiniAppCard(
                isBilibili ? MiniAppCardKind.Bilibili : MiniAppCardKind.Generic,
                appName,
                FirstNonEmpty(GetString(detail, "desc"), TrimPromptPrefix(GetString(root, "prompt")), appName),
                TryGetProperty(detail, "host", out var host) ? GetString(host, "nick") : null,
                NormalizeUrl(GetString(detail, "icon")),
                NormalizeUrl(FirstNonEmpty(GetString(detail, "preview"), FindStringProperty(root, "preview"))),
                jumpUrl,
                appJson);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TrimPromptPrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        var colonIndex = trimmed.IndexOfAny(['：', ':']);
        return colonIndex >= 0 && colonIndex + 1 < trimmed.Length
            ? trimmed[(colonIndex + 1)..].Trim()
            : trimmed;
    }

    private static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = WebUtility.HtmlDecode(value.Trim().Replace("\\/", "/", StringComparison.Ordinal));
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
            return "https:" + trimmed;

        return Uri.TryCreate(trimmed, UriKind.Absolute, out _)
            ? trimmed
            : "https://" + trimmed.TrimStart('/');
    }

    private static string? FindKnownUrl(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (FindKnownUrl(property.Value) is { Length: > 0 } nested)
                        return nested;
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindKnownUrl(item) is { Length: > 0 } nested)
                        return nested;
                }

                break;
            case JsonValueKind.String:
                var url = NormalizeUrl(element.GetString());
                if (IsKnownContentUrl(url))
                    return url;
                break;
        }

        return null;
    }

    private static string? FindStringProperty(JsonElement element, string propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                        return GetJsonStringValue(property.Value);

                    if (FindStringProperty(property.Value, propertyName) is { } nested)
                        return nested;
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindStringProperty(item, propertyName) is { } nested)
                        return nested;
                }

                break;
        }

        return null;
    }

    private static string? GetJsonStringValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null,
        };
    }

    private static bool IsKnownContentUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains("b23.tv/", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("bilibili.com/", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("zhihu.com/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property))
            return null;

        return GetJsonStringValue(property);
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
}
