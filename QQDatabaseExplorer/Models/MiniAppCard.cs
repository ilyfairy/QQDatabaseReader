using System;
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

            if (!TryGetProperty(root, "meta", out var meta) ||
                !TryGetProperty(meta, "detail_1", out var detail))
            {
                return false;
            }

            var detailAppId = GetString(detail, "appid");
            var rootAppId = GetString(root, "appID");
            var appName = FirstNonEmpty(GetString(detail, "title"), "QQ小程序");
            var isBilibili = string.Equals(detailAppId, BilibiliDetailAppId, StringComparison.Ordinal) ||
                             string.Equals(rootAppId, BilibiliRootAppId, StringComparison.Ordinal) ||
                             appName.Contains("哔哩", StringComparison.Ordinal);
            if (!isBilibili)
                return false;

            card = new MiniAppCard(
                MiniAppCardKind.Bilibili,
                appName,
                FirstNonEmpty(GetString(detail, "desc"), TrimPromptPrefix(GetString(root, "prompt")), appName),
                TryGetProperty(detail, "host", out var host) ? GetString(host, "nick") : null,
                NormalizeUrl(GetString(detail, "icon")),
                NormalizeUrl(GetString(detail, "preview")),
                NormalizeUrl(FirstNonEmpty(
                    GetString(detail, "qqdocurl"),
                    GetString(detail, "qddocurl"),
                    GetString(detail, "url"))),
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

        var trimmed = value.Trim().Replace("\\/", "/", StringComparison.Ordinal);
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
            return "https:" + trimmed;

        return Uri.TryCreate(trimmed, UriKind.Absolute, out _)
            ? trimmed
            : "https://" + trimmed.TrimStart('/');
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
}
