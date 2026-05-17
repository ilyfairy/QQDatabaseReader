using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace QQDatabaseExplorer.Models;

public static class QQFaceCatalog
{
    private const string QQNtAssetPathPrefix = "assets/qq_emoji/";
    private static readonly string QQNtEmojiRoot = Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "QFace",
        "qq_emoji");
    private static readonly Lazy<IReadOnlyDictionary<int, QQFaceInfo>> LazyFaces = new(LoadFaces);
    private static readonly Lazy<IReadOnlyDictionary<string, QQFaceInfo>> LazyUnicodeEmojiFaces = new(LoadUnicodeEmojiFaces);

    public static QQFaceInfo? Get(int id)
    {
        try
        {
            return LazyFaces.Value.TryGetValue(id, out var face) ? face : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static QQFaceInfo? GetUnicodeEmoji(string textElement)
    {
        if (string.IsNullOrEmpty(textElement))
            return null;

        IReadOnlyDictionary<string, QQFaceInfo> faces;
        try
        {
            faces = LazyUnicodeEmojiFaces.Value;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (faces.TryGetValue(textElement, out var face))
            return face;

        var normalized = NormalizeUnicodeEmojiKey(textElement);
        return normalized.Length == textElement.Length
            ? null
            : faces.TryGetValue(normalized, out face)
                ? face
                : null;
    }

    private static IReadOnlyDictionary<int, QQFaceInfo> LoadFaces()
    {
        var faces = new Dictionary<int, QQFaceInfo>();
        var facesByName = new Dictionary<string, QQFaceInfo>(StringComparer.Ordinal);
        var knownFaceMetadata = LoadKnownFaceMetadata();

        foreach (var entry in LoadQQNtEmojiMetadata())
        {
            faces[entry.EmojiId] = entry.Face;

            if (!string.IsNullOrWhiteSpace(entry.Face.Name))
                facesByName.TryAdd(entry.Face.Name, entry.Face);
        }

        foreach (var (faceId, knownFace) in knownFaceMetadata)
        {
            if (!faces.ContainsKey(faceId) &&
                facesByName.TryGetValue(knownFace.Name, out var face))
            {
                faces.Add(faceId, face);
                continue;
            }

            if (!faces.ContainsKey(faceId))
            {
                faces.Add(faceId, new QQFaceInfo(faceId, knownFace.Name, null, false));
            }
        }

        return faces;
    }

    private static IReadOnlyDictionary<int, KnownFaceInfo> LoadKnownFaceMetadata()
    {
        var faces = new Dictionary<int, KnownFaceInfo>();
        LoadQQNtFaceConfig(faces);
        return faces;
    }

    private static void LoadQQNtFaceConfig(Dictionary<int, KnownFaceInfo> faces)
    {
        var configPath = Path.Combine(QQNtEmojiRoot, "face_config.json");
        if (!File.Exists(configPath))
            return;

        using var stream = File.OpenRead(configPath);
        using var document = JsonDocument.Parse(stream);

        if (!document.RootElement.TryGetProperty("sysface", out var sysfaceProperty) ||
            sysfaceProperty.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in sysfaceProperty.EnumerateArray())
        {
            TryAddQQNtFaceConfigEntry(faces, item);
        }
    }

    private static void TryAddQQNtFaceConfigEntry(Dictionary<int, KnownFaceInfo> faces, JsonElement item)
    {
        if (!TryGetConfigInt(item, "QSid", out var id))
            return;

        var name = item.TryGetProperty("QDes", out var descriptionProperty)
            ? NormalizeFaceName(GetString(descriptionProperty))
            : string.Empty;

        if (string.IsNullOrWhiteSpace(name))
            return;

        faces.TryAdd(id, new KnownFaceInfo(name));
    }

    private static IEnumerable<QQNtFaceEntry> LoadQQNtEmojiMetadata()
    {
        var indexPath = Path.Combine(QQNtEmojiRoot, "_index.json");
        if (!File.Exists(indexPath))
            yield break;

        using var stream = File.OpenRead(indexPath);
        using var document = JsonDocument.Parse(stream);

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("emojiId", out var idProperty))
                continue;

            var emojiIdText = GetString(idProperty);
            if (string.IsNullOrWhiteSpace(emojiIdText))
                continue;

            if (!TryGetInt32(idProperty, out var emojiId))
                continue;

            var asset = ResolveQQNtAsset(item, emojiIdText);
            if (asset is null)
                continue;

            var name = item.TryGetProperty("describe", out var descriptionProperty)
                ? NormalizeFaceName(GetString(descriptionProperty))
                : string.Empty;

            yield return new QQNtFaceEntry(
                new QQFaceInfo(emojiId, name, asset.Path, asset.IsAnimated),
                emojiId);
        }
    }

    private static IReadOnlyDictionary<string, QQFaceInfo> LoadUnicodeEmojiFaces()
    {
        var faces = new Dictionary<string, QQFaceInfo>(StringComparer.Ordinal);
        var indexPath = Path.Combine(QQNtEmojiRoot, "_index.json");
        if (!File.Exists(indexPath))
            return faces;

        using var stream = File.OpenRead(indexPath);
        using var document = JsonDocument.Parse(stream);

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("emojiId", out var idProperty))
                continue;

            var emojiIdText = GetString(idProperty);
            if (string.IsNullOrWhiteSpace(emojiIdText) ||
                TryGetInt32(idProperty, out _))
            {
                continue;
            }

            var asset = ResolveQQNtAsset(item, emojiIdText);
            if (asset is null)
                continue;

            var name = item.TryGetProperty("describe", out var descriptionProperty)
                ? NormalizeFaceName(GetString(descriptionProperty))
                : string.Empty;

            var faceId = TryGetConfigInt(item, "qcid", out var qcid) ? qcid : 0;
            var face = new QQFaceInfo(faceId, name, asset.Path, asset.IsAnimated);
            faces.TryAdd(emojiIdText, face);

            var normalized = NormalizeUnicodeEmojiKey(emojiIdText);
            if (!string.IsNullOrEmpty(normalized))
                faces.TryAdd(normalized, face);
        }

        return faces;
    }

    private static FaceAsset? ResolveQQNtAsset(JsonElement item, string emojiIdText)
    {
        if (!item.TryGetProperty("assets", out var assetsProperty) ||
            assetsProperty.ValueKind != JsonValueKind.Array)
        {
            return ResolveQQNtAssetFromLayout(emojiIdText);
        }

        FaceAsset? preferredStaticAsset = null;
        FaceAsset? fallbackStaticAsset = null;

        foreach (var assetProperty in assetsProperty.EnumerateArray())
        {
            if (!assetProperty.TryGetProperty("type", out var typeProperty) ||
                !TryGetInt32(typeProperty, out var type) ||
                (type != 0 && type != 2))
            {
                continue;
            }

            if (!assetProperty.TryGetProperty("path", out var pathProperty))
                continue;

            var assetPath = ToQQNtAssetPath(GetString(pathProperty));
            if (assetPath is null || !File.Exists(assetPath))
                continue;

            if (type == 2)
                return new FaceAsset(assetPath, true);

            if (IsPreferredStaticAsset(assetProperty, emojiIdText))
                preferredStaticAsset ??= new FaceAsset(assetPath, false);
            else
                fallbackStaticAsset ??= new FaceAsset(assetPath, false);
        }

        return preferredStaticAsset ?? fallbackStaticAsset ?? ResolveQQNtAssetFromLayout(emojiIdText);
    }

    private static FaceAsset? ResolveQQNtAssetFromLayout(string emojiIdText)
    {
        var apng = Path.Combine(QQNtEmojiRoot, emojiIdText, "apng", $"{emojiIdText}.png");
        if (File.Exists(apng))
            return new FaceAsset(apng, true);

        var png = Path.Combine(QQNtEmojiRoot, emojiIdText, "png", $"{emojiIdText}.png");
        if (File.Exists(png))
            return new FaceAsset(png, false);

        return null;
    }

    private static bool IsPreferredStaticAsset(JsonElement assetProperty, string emojiIdText)
    {
        return assetProperty.TryGetProperty("name", out var nameProperty) &&
               string.Equals(GetString(nameProperty), $"{emojiIdText}.png", StringComparison.Ordinal);
    }

    private static string? ToQQNtAssetPath(string? metadataPath)
    {
        if (string.IsNullOrWhiteSpace(metadataPath))
            return null;

        var path = metadataPath.Replace('\\', '/');
        if (path.StartsWith(QQNtAssetPathPrefix, StringComparison.Ordinal))
            path = path[QQNtAssetPathPrefix.Length..];
        else
            path = path.TrimStart('/');

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine([QQNtEmojiRoot, ..parts]);
    }

    private static bool TryGetConfigInt(JsonElement item, string propertyName, out int value)
    {
        value = 0;
        if (!item.TryGetProperty(propertyName, out var property))
            return false;

        return TryGetInt32(property, out value);
    }

    private static bool TryGetInt32(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(
                element.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value),
            _ => false,
        };
    }

    private static string? GetString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null,
        };
    }

    private static string NormalizeFaceName(string? name)
    {
        return string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim().TrimStart('/');
    }

    private static string NormalizeUnicodeEmojiKey(string text)
    {
        return text.Replace("\uFE0E", string.Empty, StringComparison.Ordinal)
            .Replace("\uFE0F", string.Empty, StringComparison.Ordinal);
    }

    private sealed record KnownFaceInfo(string Name);
    private sealed record QQNtFaceEntry(QQFaceInfo Face, int EmojiId);
    private sealed record FaceAsset(string Path, bool IsAnimated);
}

public sealed record QQFaceInfo(int Id, string Name, string? AssetPath, bool IsAnimated);
