using System;
using System.Text.Json;

namespace QQDatabaseExplorer.Models;

public sealed record SharedContactCard(
    SharedContactCardKind Kind,
    string Title,
    string Subtitle,
    string Tag,
    string? AvatarUrl,
    string? JumpUrl,
    string RawPayload)
{
    public string CopyText => Kind switch
    {
        SharedContactCardKind.Group => $"群名片: {Title}",
        SharedContactCardKind.Friend => $"推荐联系人：{Title}",
        _ => string.IsNullOrWhiteSpace(Tag) ? Title : $"{Tag}: {Title}",
    };
}

public enum SharedContactCardKind
{
    Friend,
    Group,
}

public static class SharedContactCardParser
{
    public static bool TryParse(string? appJson, out SharedContactCard? card)
    {
        card = null;
        if (string.IsNullOrWhiteSpace(appJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(appJson);
            var root = document.RootElement;
            var app = GetString(root, "app");
            var kind = app switch
            {
                "com.tencent.contact.lua" => SharedContactCardKind.Friend,
                "com.tencent.troopsharecard" => SharedContactCardKind.Group,
                _ => (SharedContactCardKind?)null,
            };
            if (kind is null)
                return false;

            if (!TryGetProperty(root, "meta", out var meta) ||
                !TryGetProperty(meta, "contact", out var contact))
            {
                return false;
            }

            var title = FirstNonEmpty(
                GetString(contact, "nickname"),
                TrimPromptPrefix(GetString(root, "prompt")),
                kind == SharedContactCardKind.Group ? "群名片" : "推荐联系人");
            var subtitle = FirstNonEmpty(GetString(contact, "contact"), GetString(root, "prompt"));
            var tag = FirstNonEmpty(
                GetString(contact, "tag"),
                kind == SharedContactCardKind.Group ? "群名片" : "推荐好友");

            card = new SharedContactCard(
                kind.Value,
                title,
                subtitle,
                tag,
                GetString(contact, "avatar"),
                GetString(contact, "jumpUrl"),
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
