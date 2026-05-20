using QQDatabaseReader.Database;

namespace QQDatabaseReader;

public static class QQMessageDisplayText
{
    public static string CreateText(QQMessageContent content, MessageType messageType, SubMessageType subMessageType)
    {
        if (TryGetPriorityText(messageType, subMessageType, out var priorityText))
            return priorityText;

        var text = string.Concat(content.Segments.Select(segment => CreateSegmentText(segment, messageType, subMessageType)));
        if (!string.IsNullOrWhiteSpace(text))
            return text;

        return TryGetFallbackText(messageType, subMessageType, out var fallbackText)
            ? fallbackText
            : string.Empty;
    }

    public static string CreateSegmentText(QQMessageSegment segment, MessageType messageType, SubMessageType subMessageType)
    {
        if (messageType == MessageType.Text &&
            subMessageType == SubMessageType.Sticker &&
            segment.Type == MessageSegmentType.Image &&
            string.IsNullOrEmpty(segment.Text) &&
            string.IsNullOrEmpty(segment.AltText))
        {
            return "[动画表情]";
        }

        return segment.GetDisplayText();
    }

    public static bool TryGetPriorityText(MessageType messageType, SubMessageType subMessageType, out string text)
    {
        text = messageType switch
        {
            MessageType.GroupFile => subMessageType switch
            {
                SubMessageType.GroupFileImage => "[群文件图片]",
                SubMessageType.GroupFileVideo => "[群文件视频]",
                SubMessageType.GroupFileAudio => "[群文件音频]",
                SubMessageType.GroupFileDocx => "[群文件 DOCX]",
                SubMessageType.GroupFilePptx => "[群文件 PPTX]",
                SubMessageType.GroupFileXlsx => "[群文件 XLSX]",
                SubMessageType.GroupFileZip => "[群文件 ZIP]",
                SubMessageType.GroupFileExe => "[群文件 EXE]",
                _ => "[群文件]",
            },
            MessageType.Video => "[视频消息]",
            MessageType.Voice => "[语音消息]",
            MessageType.Text when subMessageType == SubMessageType.Sticker => "[动画表情]",
            _ => string.Empty,
        };

        return !string.IsNullOrWhiteSpace(text);
    }

    public static bool TryGetFallbackText(MessageType messageType, SubMessageType subMessageType, out string text)
    {
        if (TryGetPriorityText(messageType, subMessageType, out text))
            return true;

        text = messageType switch
        {
            MessageType.System => subMessageType switch
            {
                SubMessageType.MessageRecalled => "[撤回消息]",
                SubMessageType.Nudge => "[互动消息]",
                SubMessageType.Pat => "[拍一拍]",
                _ => "[系统消息]",
            },
            MessageType.None => "[空消息/损坏消息]",
            MessageType.Forwarded => "[合并转发消息]",
            MessageType.Reply => "[回复消息]",
            MessageType.RedPacket => "[红包消息]",
            MessageType.App => "[应用消息]",
            _ => subMessageType switch
            {
                SubMessageType.GroupAnnouncement => "[群公告]",
                SubMessageType.PlatformText => "[平台文本消息]",
                SubMessageType.ContainsLink => "[链接消息]",
                _ => string.Empty,
            },
        };

        return !string.IsNullOrWhiteSpace(text);
    }
}
