using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Google.Protobuf;

namespace QQDatabaseExplorer.Models;

public sealed class MessageBatchCopyPayload
{
    private MessageBatchCopyPayload(string plainText, string htmlFragment, string htmlDocument, byte[]? qqMultiMsgBytes)
    {
        PlainText = plainText;
        HtmlFragment = htmlFragment;
        HtmlDocument = htmlDocument;
        QQMultiMsgBytes = qqMultiMsgBytes;
    }

    public string PlainText { get; }
    public string HtmlFragment { get; }
    public string HtmlDocument { get; }
    public byte[]? QQMultiMsgBytes { get; }
    public bool HasContent => !string.IsNullOrEmpty(PlainText) || !string.IsNullOrEmpty(HtmlFragment);

    public static MessageBatchCopyPayload Empty { get; } = new(string.Empty, string.Empty, string.Empty, null);

    public static MessageBatchCopyPayload FromMessages(IReadOnlyList<AvaQQMessage> messages)
    {
        if (messages.Count == 0)
            return Empty;

        var plainText = new StringBuilder();
        var html = new StringBuilder();

        for (var i = 0; i < messages.Count; i++)
        {
            if (i > 0)
            {
                plainText.Append('\n');
                plainText.Append('\n');
                html.Append('\n');
                html.Append('\n');
            }

            var message = messages[i];
            if (messages.Count > 1)
            {
                var header = CreateHeader(message);
                plainText.Append(header);
                plainText.Append('\n');
                AppendTextHtml(html, header, isWarning: false);
                html.Append('\n');
            }

            AppendMessagePlainText(plainText, message);
            AppendMessageHtml(html, message);
        }

        var fragment = html.ToString();
        return new MessageBatchCopyPayload(
            plainText.ToString(),
            fragment,
            CreateHtmlDocument(fragment),
            CreateQQMultiMsgBytes(messages, plainText.ToString()));
    }

    private static string CreateHeader(AvaQQMessage message)
    {
        return $"{message.Name}: {FormatMessageTime(message.MessageTime)}";
    }

    private static string FormatMessageTime(int messageTime)
    {
        if (messageTime <= 0)
            return string.Empty;

        var localTime = DateTimeOffset.FromUnixTimeSeconds(messageTime).LocalDateTime;
        return localTime.Year == DateTime.Now.Year
            ? localTime.ToString("MM-dd HH:mm:ss")
            : localTime.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private static void AppendMessagePlainText(StringBuilder builder, AvaQQMessage message)
    {
        if (message.Reply is not null && !string.IsNullOrWhiteSpace(message.Reply.PreviewText))
        {
            builder.Append("[回复]");
            builder.Append(message.Reply.PreviewText);
            builder.Append('\n');
        }

        AppendMessagePlainText(builder, message.Segments);
    }

    private static void AppendMessagePlainText(StringBuilder builder, IReadOnlyList<AvaQQMessageSegment> segments)
    {
        foreach (var segment in segments)
        {
            if (TryCreateImageTag(segment, htmlEncode: false) is { } imageTag)
            {
                builder.Append(imageTag);
                continue;
            }

            builder.Append(segment.DisplayText);
        }
    }

    private static void AppendMessageHtml(StringBuilder builder, AvaQQMessage message)
    {
        if (message.Reply is not null && !string.IsNullOrWhiteSpace(message.Reply.PreviewText))
        {
            AppendTextHtml(builder, "[回复]" + message.Reply.PreviewText, isWarning: false);
            builder.Append("<br>");
        }

        AppendMessageHtml(builder, message.Segments);
    }

    private static void AppendMessageHtml(StringBuilder builder, IReadOnlyList<AvaQQMessageSegment> segments)
    {
        var needsLineBreak = false;

        foreach (var segment in segments)
        {
            if (TryCreateImageTag(segment, htmlEncode: true) is { } imageTag)
            {
                if (builder.Length > 0)
                    builder.Append("<br>");

                builder.Append(imageTag);
                needsLineBreak = true;
                continue;
            }

            if (needsLineBreak)
            {
                builder.Append("<br>");
                needsLineBreak = false;
            }

            AppendTextHtml(builder, segment.DisplayText, segment.Tone == AvaQQMessageSegmentTone.Warning, segment.LinkUrl);
        }
    }

    private static bool IsCopyableImage(AvaQQMessageSegment segment)
    {
        return segment.Type == AvaQQMessageSegmentType.Image &&
               segment.IsImageAvailable &&
               !string.IsNullOrWhiteSpace(segment.ImageLocalPath) &&
               File.Exists(segment.ImageLocalPath);
    }

    private static string? TryCreateImageTag(AvaQQMessageSegment segment, bool htmlEncode)
    {
        if (!IsCopyableImage(segment))
        {
            return null;
        }

        var source = "file://" + segment.ImageLocalPath;
        if (htmlEncode)
        {
            source = WebUtility.HtmlEncode(source);
        }

        return $"<img src=\"{source}\" />";
    }

    private static void AppendTextHtml(
        StringBuilder builder,
        string text,
        bool isWarning,
        string? linkUrl = null)
    {
        var encoded = WebUtility.HtmlEncode(text)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);

        if (!string.IsNullOrWhiteSpace(linkUrl))
        {
            builder.Append("<a href=\"")
                .Append(WebUtility.HtmlEncode(linkUrl))
                .Append("\" style=\"color:#1677ff;text-decoration:underline;\">")
                .Append(encoded)
                .Append("</a>");
            return;
        }

        if (isWarning)
        {
            builder.Append("<span style=\"color:#dc2626;\">")
                .Append(encoded)
                .Append("</span>");
            return;
        }

        builder.Append(encoded);
    }

    private static string CreateHtmlDocument(string fragment)
    {
        return "<html>\r\n<body>\r\n<!--StartFragment-->" +
               fragment +
               "<!--EndFragment-->\r\n</body>\r\n</html>";
    }

    private static byte[]? CreateQQMultiMsgBytes(IReadOnlyList<AvaQQMessage> messages, string plainText)
    {
        if (messages.Count <= 1 || messages.Any(message => message.GroupId == 0))
            return null;

        var groupId = messages[0].GroupId;
        if (messages.Any(message => message.GroupId != groupId))
            return null;

        using var stream = new MemoryStream();
        var output = new CodedOutputStream(stream);

        foreach (var message in messages)
        {
            if (message.MessageId <= 0)
                return null;

            output.WriteTag(1, WireFormat.WireType.LengthDelimited);
            output.WriteLength(CodedOutputStream.ComputeTagSize(1) + CodedOutputStream.ComputeInt64Size(message.MessageId));
            output.WriteTag(1, WireFormat.WireType.Varint);
            output.WriteInt64(message.MessageId);
        }

        output.WriteTag(2, WireFormat.WireType.Varint);
        output.WriteInt32(2);

        output.WriteTag(3, WireFormat.WireType.LengthDelimited);
        output.WriteString(groupId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        output.WriteTag(4, WireFormat.WireType.LengthDelimited);
        output.WriteLength(0);

        output.WriteTag(5, WireFormat.WireType.LengthDelimited);
        output.WriteString(plainText);

        output.Flush();
        return stream.ToArray();
    }
}
