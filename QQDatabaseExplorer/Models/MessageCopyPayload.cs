using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using QQDatabaseExplorer.Controls;

namespace QQDatabaseExplorer.Models;

public sealed class MessageCopyPayload
{
    private MessageCopyPayload(IReadOnlyList<MessageCopyPart> parts)
    {
        Parts = parts;
        PlainText = BuildPlainText(parts);
        Html = BuildHtml(parts);
        QQRichEditBytes = BuildQQRichEditBytes(parts);
        ImagePaths = parts
            .Where(part => part.Kind == MessageCopyPartKind.Image &&
                           !string.IsNullOrWhiteSpace(part.ImagePath) &&
                           File.Exists(part.ImagePath))
            .Select(part => part.ImagePath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        FilePaths = parts
            .Where(part => part.Kind == MessageCopyPartKind.File &&
                           !string.IsNullOrWhiteSpace(part.FilePath) &&
                           File.Exists(part.FilePath))
            .Select(part => part.FilePath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<MessageCopyPart> Parts { get; }
    public string PlainText { get; }
    public string Html { get; }
    public byte[]? QQRichEditBytes { get; }
    public IReadOnlyList<string> ImagePaths { get; }
    public IReadOnlyList<string> FilePaths { get; }
    public bool HasContent => Parts.Count > 0 && (!string.IsNullOrEmpty(PlainText) || ImagePaths.Count > 0 || FilePaths.Count > 0);
    public bool IsSingleLocalImage =>
        Parts.Count == 1 &&
        Parts[0].Kind == MessageCopyPartKind.Image &&
        !string.IsNullOrWhiteSpace(Parts[0].ImagePath) &&
        File.Exists(Parts[0].ImagePath);
    public string? SingleLocalImagePath => IsSingleLocalImage ? Parts[0].ImagePath : null;
    public bool IsSingleLocalFile =>
        Parts.Count == 1 &&
        Parts[0].Kind == MessageCopyPartKind.File &&
        !string.IsNullOrWhiteSpace(Parts[0].FilePath) &&
        File.Exists(Parts[0].FilePath);
    public string? SingleLocalFilePath => IsSingleLocalFile ? Parts[0].FilePath : null;

    public static MessageCopyPayload Empty { get; } = new([]);

    public static MessageCopyPayload FromParts(IEnumerable<MessageCopyPart> parts)
    {
        return new MessageCopyPayload(parts.Where(part => !part.IsEmpty).ToArray());
    }

    public static MessageCopyPayload FromSegments(IReadOnlyList<AvaQQMessageSegment> segments)
    {
        if (segments.Count == 0)
            return Empty;

        return FromParts(CreateParts(segments));
    }

    public static MessageCopyPayload FromMessage(AvaQQMessage message)
    {
        if (message.Reply is null)
            return FromSegments(message.Segments);

        var parts = new List<MessageCopyPart>();
        if (!string.IsNullOrWhiteSpace(message.Reply.PreviewText))
        {
            parts.Add(MessageCopyPart.CreateText($"[回复]{message.Reply.PreviewText}\n"));
        }

        parts.AddRange(CreateParts(message.Segments));
        return FromParts(parts);
    }

    internal static IReadOnlyList<MessageCopyPart> CreateParts(IReadOnlyList<AvaQQMessageSegment> segments)
    {
        if (segments.Count == 0)
            return [];

        var parts = new List<MessageCopyPart>();
        var hasContent = false;
        var needsLineBreak = false;

        foreach (var segment in segments)
        {
            if (segment.Type == AvaQQMessageSegmentType.QQFace &&
                !string.IsNullOrWhiteSpace(segment.FaceAssetPath))
            {
                if (needsLineBreak)
                {
                    parts.Add(MessageCopyPart.CreateText("\n"));
                    needsLineBreak = false;
                }

                parts.Add(MessageCopyPart.CreateAssetImages(
                    segment.DisplayText,
                    [segment.FaceAssetPath],
                    qqFaceId: segment.FaceId,
                    isBlockImage: false));
                hasContent = true;
                continue;
            }

            if (segment.Type == AvaQQMessageSegmentType.Image)
            {
                if (hasContent)
                {
                    parts.Add(MessageCopyPart.CreateText("\n"));
                }

                parts.Add(MessageCopyPart.CreateImage(
                    segment.DisplayText,
                    segment.IsImageAvailable ? segment.ImageLocalPath : null,
                    segment.Tone));
                hasContent = true;
                needsLineBreak = true;
                continue;
            }

            if (segment.Type == AvaQQMessageSegmentType.Voice)
            {
                if (needsLineBreak)
                {
                    parts.Add(MessageCopyPart.CreateText("\n"));
                    needsLineBreak = false;
                }

                parts.Add(MessageCopyPart.CreateText(segment.DisplayText, segment.Tone));
                hasContent = true;
                continue;
            }

            if (segment.Type == AvaQQMessageSegmentType.Video)
            {
                if (hasContent)
                {
                    parts.Add(MessageCopyPart.CreateText("\n"));
                }

                parts.Add(MessageCopyPart.CreateFile(
                    segment.DisplayText,
                    segment.IsVideoAvailable ? segment.VideoLocalPath : null,
                    segment.Tone));
                hasContent = true;
                needsLineBreak = true;
                continue;
            }

            if (segment.Type == AvaQQMessageSegmentType.File)
            {
                if (hasContent)
                {
                    parts.Add(MessageCopyPart.CreateText("\n"));
                }

                parts.Add(MessageCopyPart.CreateFile(
                    segment.DisplayText,
                    segment.IsFileAvailable ? segment.FileLocalPath : null,
                    segment.Tone));
                hasContent = true;
                needsLineBreak = true;
                continue;
            }

            if (segment.Type == AvaQQMessageSegmentType.ForwardedMessage)
            {
                if (hasContent)
                {
                    parts.Add(MessageCopyPart.CreateText("\n"));
                }

                parts.Add(MessageCopyPart.CreateText(segment.DisplayText));
                hasContent = true;
                needsLineBreak = true;
                continue;
            }

            if (segment.Type == AvaQQMessageSegmentType.SharedContact)
            {
                if (hasContent)
                {
                    parts.Add(MessageCopyPart.CreateText("\n"));
                }

                parts.Add(MessageCopyPart.CreateText(segment.DisplayText));
                hasContent = true;
                needsLineBreak = true;
                continue;
            }

            if (segment.Type == AvaQQMessageSegmentType.MiniApp)
            {
                if (hasContent)
                {
                    parts.Add(MessageCopyPart.CreateText("\n"));
                }

                parts.Add(MessageCopyPart.CreateText(
                    segment.DisplayText,
                    linkUrl: segment.MiniApp?.JumpUrl));
                hasContent = true;
                needsLineBreak = true;
                continue;
            }

            var text = segment.DisplayText;
            if (string.IsNullOrEmpty(text))
                continue;

            if (needsLineBreak)
            {
                parts.Add(MessageCopyPart.CreateText("\n"));
                needsLineBreak = false;
            }

            parts.Add(MessageCopyPart.CreateText(text, segment.Tone, segment.LinkUrl));
            hasContent = true;
        }

        return parts;
    }

    private static string BuildPlainText(IEnumerable<MessageCopyPart> parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            builder.Append(part.Text);
        }

        return builder.ToString();
    }

    private static string BuildHtml(IEnumerable<MessageCopyPart> parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            switch (part.Kind)
            {
                case MessageCopyPartKind.Image when part.QQFaceId is not null:
                    AppendTextHtml(builder, part);
                    break;
                case MessageCopyPartKind.Image when !string.IsNullOrWhiteSpace(part.ImagePath) && File.Exists(part.ImagePath):
                    AppendImageHtml(builder, CreateFileImageDataUri(part.ImagePath), part.Text, part.IsBlockImage);
                    break;
                case MessageCopyPartKind.Image when part.AssetPaths.Count > 0:
                    foreach (var assetPath in part.AssetPaths)
                    {
                        if (File.Exists(assetPath))
                            AppendImageHtml(builder, CreateFileImageDataUri(assetPath), part.Text, part.IsBlockImage);
                    }
                    break;
                case MessageCopyPartKind.File when !string.IsNullOrWhiteSpace(part.FilePath) && File.Exists(part.FilePath):
                    AppendFileHtml(builder, part.FilePath, part.Text);
                    break;
                default:
                    AppendTextHtml(builder, part);
                    break;
            }
        }

        return builder.ToString();
    }

    private static byte[]? BuildQQRichEditBytes(IReadOnlyList<MessageCopyPart> parts)
    {
        var hasRichElement = false;
        var builder = new StringBuilder("<QQRichEditFormat><Info version=\"1001\"></Info>");

        foreach (var part in parts)
        {
            if (part.QQFaceId is { } qqFaceId)
            {
                hasRichElement = true;
                builder.Append("<EditElement type=\"2\" sysfaceindex=\"")
                    .Append(qqFaceId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append("\" filepath=\"\" shortcut=\"\"></EditElement>");
                continue;
            }

            if (part.Kind == MessageCopyPartKind.Text)
            {
                AppendQQRichEditText(builder, part.Text);
                continue;
            }

            if (part.Kind == MessageCopyPartKind.Image &&
                !string.IsNullOrWhiteSpace(part.ImagePath) &&
                File.Exists(part.ImagePath))
            {
                hasRichElement = true;
                builder.Append("<EditElement type=\"1\" filepath=\"")
                    .Append(WebUtility.HtmlEncode(part.ImagePath))
                    .Append("\" shortcut=\"\"></EditElement>");
                continue;
            }

            if (part.Kind == MessageCopyPartKind.File)
            {
                return null;
            }

            return null;
        }

        if (!hasRichElement)
            return null;

        builder.Append("</QQRichEditFormat>");
        return Encoding.UTF8.GetBytes(builder.Append('\0').ToString());
    }

    private static void AppendQQRichEditText(StringBuilder builder, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        builder.Append("<EditElement type=\"0\"><![CDATA[")
            .Append(text.Replace("]]>", "]]]]><![CDATA[>", StringComparison.Ordinal))
            .Append("]]></EditElement>");
    }

    private static void AppendTextHtml(StringBuilder builder, MessageCopyPart part)
    {
        var encoded = WebUtility.HtmlEncode(part.Text)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", "<br />", StringComparison.Ordinal);

        if (!string.IsNullOrWhiteSpace(part.LinkUrl))
        {
            builder.Append("<a href=\"")
                .Append(WebUtility.HtmlEncode(part.LinkUrl))
                .Append("\" style=\"color:#1677ff;text-decoration:underline;\">")
                .Append(encoded)
                .Append("</a>");
            return;
        }

        if (part.Tone == AvaQQMessageSegmentTone.Warning)
        {
            builder.Append("<span style=\"color:#dc2626;\">")
                .Append(encoded)
                .Append("</span>");
            return;
        }

        builder.Append(encoded);
    }

    private static void AppendImageHtml(StringBuilder builder, string dataUri, string altText, bool isBlockImage)
    {
        var style = isBlockImage
            ? "max-width:480px;height:auto;display:block;margin:4px 0;"
            : "width:20px;height:20px;vertical-align:-4px;";

        builder.Append("<img src=\"")
            .Append(WebUtility.HtmlEncode(dataUri))
            .Append("\" alt=\"")
            .Append(WebUtility.HtmlEncode(altText))
            .Append("\" style=\"")
            .Append(style)
            .Append("\" />");
    }

    private static void AppendFileHtml(StringBuilder builder, string filePath, string text)
    {
        builder.Append("<a href=\"")
            .Append(WebUtility.HtmlEncode(new Uri(filePath).AbsoluteUri))
            .Append("\">")
            .Append(WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(text) ? Path.GetFileName(filePath) : text))
            .Append("</a>");
    }

    private static string CreateFileImageDataUri(string imagePath)
    {
        var bytes = LocalImageFile.ReadDisplayBytes(imagePath);
        var mimeType = GetImageMimeType(bytes);
        return $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";
    }

    private static string GetImageMimeType(string imagePath)
    {
        Span<byte> header = stackalloc byte[12];
        try
        {
            using var stream = File.OpenRead(imagePath);
            var read = stream.Read(header);
            return GetImageMimeType(header[..read]);
        }
        catch
        {
        }

        return "application/octet-stream";
    }

    private static string GetImageMimeType(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 6 &&
            header[0] == 'G' &&
            header[1] == 'I' &&
            header[2] == 'F')
        {
            return "image/gif";
        }

        if (header.Length >= 8 &&
            header[0] == 0x89 &&
            header[1] == 0x50 &&
            header[2] == 0x4E &&
            header[3] == 0x47)
        {
            return "image/png";
        }

        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (header.Length >= 12 &&
            header[0] == 'R' &&
            header[1] == 'I' &&
            header[2] == 'F' &&
            header[3] == 'F' &&
            header[8] == 'W' &&
            header[9] == 'E' &&
            header[10] == 'B' &&
            header[11] == 'P')
        {
            return "image/webp";
        }

        return "application/octet-stream";
    }
}

public sealed record MessageCopyPart(
    MessageCopyPartKind Kind,
    string Text,
    AvaQQMessageSegmentTone Tone = AvaQQMessageSegmentTone.Normal,
    string? LinkUrl = null,
    string? ImagePath = null,
    string? FilePath = null,
    IReadOnlyList<string>? AssetPaths = null,
    int? QQFaceId = null,
    bool IsBlockImage = false,
    AvaQQMessageSegment? Segment = null)
{
    public IReadOnlyList<string> AssetPaths { get; init; } = AssetPaths ?? [];
    public bool IsEmpty => Kind is not (MessageCopyPartKind.Image or MessageCopyPartKind.File) && string.IsNullOrEmpty(Text);

    public static MessageCopyPart CreateText(
        string text,
        AvaQQMessageSegmentTone tone = AvaQQMessageSegmentTone.Normal,
        string? linkUrl = null,
        AvaQQMessageSegment? segment = null)
    {
        return new MessageCopyPart(MessageCopyPartKind.Text, text, tone, linkUrl, Segment: segment);
    }

    public static MessageCopyPart CreateImage(
        string text,
        string? imagePath,
        AvaQQMessageSegmentTone tone = AvaQQMessageSegmentTone.Normal)
    {
        return new MessageCopyPart(MessageCopyPartKind.Image, text, tone, ImagePath: imagePath, IsBlockImage: true);
    }

    public static MessageCopyPart CreateFile(
        string text,
        string? filePath,
        AvaQQMessageSegmentTone tone = AvaQQMessageSegmentTone.Normal)
    {
        return new MessageCopyPart(MessageCopyPartKind.File, text, tone, FilePath: filePath);
    }

    public static MessageCopyPart CreateAssetImages(
        string text,
        IReadOnlyList<string> assetPaths,
        AvaQQMessageSegmentTone tone = AvaQQMessageSegmentTone.Normal,
        int? qqFaceId = null,
        bool isBlockImage = false)
    {
        return new MessageCopyPart(
            MessageCopyPartKind.Image,
            text,
            tone,
            AssetPaths: assetPaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray(),
            QQFaceId: qqFaceId,
            IsBlockImage: isBlockImage);
    }

    public MessageCopyPart WithText(string text)
    {
        return this with { Text = text };
    }
}

public enum MessageCopyPartKind
{
    Text,
    Image,
    File,
}
