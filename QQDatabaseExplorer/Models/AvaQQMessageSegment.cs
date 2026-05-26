using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QQDatabaseExplorer.Models;

public partial class AvaQQMessageSegment : ObservableObject
{
    public AvaQQMessageSegmentType Type { get; init; }
    public AvaQQMessageSegmentTone Tone { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? LinkUrl { get; init; }
    public bool IsMention { get; init; }
    public string? MentionUid { get; init; }
    public int? FaceId { get; init; }
    public string? FaceName { get; init; }
    public string? FaceAssetPath { get; init; }
    public string? ImageLocalPath { get; set; }
    public int? ImageWidth { get; init; }
    public int? ImageHeight { get; init; }
    public int? ImageMaxWidth { get; init; }
    public int? ImageMaxHeight { get; init; }
    public string? ImageDisplayText { get; set; }
    public bool IsImageAvailable { get; set; }
    public string? VoiceLocalPath { get; set; }
    public string? VoiceFileName { get; init; }
    public int? VoiceDurationMilliseconds { get; set; }
    public bool IsVoiceAvailable { get; set; }
    [ObservableProperty]
    public partial bool IsVoicePlaying { get; set; }
    public string? VideoLocalPath { get; set; }
    public string? VideoCoverLocalPath { get; set; }
    public string? VideoFileName { get; init; }
    public string? VideoCoverFileName { get; init; }
    public int? VideoDurationMilliseconds { get; init; }
    public bool IsVideoAvailable { get; set; }
    public bool IsVideoCoverAvailable { get; set; }
    public ForwardedMessageCard? ForwardedMessage { get; init; }
    public SharedContactCard? SharedContact { get; init; }
    public MiniAppCard? MiniApp { get; init; }

    public string DisplayText
    {
        get
        {
            if (Type is AvaQQMessageSegmentType.Text or AvaQQMessageSegmentType.Unsupported)
                return Text;

            if (!string.IsNullOrEmpty(FaceName))
                return $"[{FaceName}]";

            if (Type == AvaQQMessageSegmentType.Image)
                return string.IsNullOrEmpty(ImageDisplayText) ? "[图片]" : ImageDisplayText;

            if (Type == AvaQQMessageSegmentType.Voice)
                return string.IsNullOrEmpty(Text) ? "[语音]" : Text;

            if (Type == AvaQQMessageSegmentType.Video)
                return string.IsNullOrEmpty(Text) ? "[视频]" : Text;

            if (Type == AvaQQMessageSegmentType.ForwardedMessage)
                return ForwardedMessage?.CopyText ?? "[聊天记录]";

            if (Type == AvaQQMessageSegmentType.SharedContact)
                return SharedContact?.CopyText ?? "[名片]";

            if (Type == AvaQQMessageSegmentType.MiniApp)
                return MiniApp?.CopyText ?? "[QQ小程序]";

            return FaceId is null ? "[QQ表情]" : $"[QQ表情:{FaceId}]";
        }
    }

    public static AvaQQMessageSegment CreateText(
        string text,
        AvaQQMessageSegmentTone tone = AvaQQMessageSegmentTone.Normal,
        string? linkUrl = null,
        bool isMention = false,
        string? mentionUid = null)
    {
        return new AvaQQMessageSegment
        {
            Type = AvaQQMessageSegmentType.Text,
            Tone = tone,
            Text = text,
            LinkUrl = linkUrl,
            IsMention = isMention,
            MentionUid = mentionUid,
        };
    }

    public static AvaQQMessageSegment CreateUnsupportedText(string text)
    {
        return new AvaQQMessageSegment
        {
            Type = AvaQQMessageSegmentType.Unsupported,
            Tone = AvaQQMessageSegmentTone.Warning,
            Text = text,
        };
    }

    public static AvaQQMessageSegment CreateQQFace(int faceId)
    {
        var face = QQFaceCatalog.Get(faceId);
        return new AvaQQMessageSegment
        {
            Type = AvaQQMessageSegmentType.QQFace,
            FaceId = faceId,
            FaceName = face?.Name,
            FaceAssetPath = face?.AssetPath,
        };
    }

    public static AvaQQMessageSegment CreateImage(
        string? localPath,
        int? width,
        int? height,
        string displayText = "[图片]",
        int? maxWidth = null,
        int? maxHeight = null)
    {
        return new AvaQQMessageSegment
        {
            Type = AvaQQMessageSegmentType.Image,
            ImageLocalPath = localPath,
            ImageWidth = width,
            ImageHeight = height,
            ImageMaxWidth = maxWidth,
            ImageMaxHeight = maxHeight,
            ImageDisplayText = displayText,
            IsImageAvailable = true,
        };
    }

    public static AvaQQMessageSegment CreateBrokenImage(
        int? width,
        int? height,
        string displayText = "[图片已损坏]",
        int? maxWidth = null,
        int? maxHeight = null)
    {
        return new AvaQQMessageSegment
        {
            Type = AvaQQMessageSegmentType.Image,
            Tone = AvaQQMessageSegmentTone.Warning,
            ImageWidth = width,
            ImageHeight = height,
            ImageMaxWidth = maxWidth,
            ImageMaxHeight = maxHeight,
            ImageDisplayText = displayText,
            IsImageAvailable = false,
        };
    }

    public static AvaQQMessageSegment CreateVoice(
        string? localPath,
        string? fileName,
        int? durationMilliseconds,
        bool isAvailable)
    {
        return new AvaQQMessageSegment
        {
            Type = AvaQQMessageSegmentType.Voice,
            Tone = isAvailable ? AvaQQMessageSegmentTone.Normal : AvaQQMessageSegmentTone.Warning,
            Text = FormatVoiceDisplayText(durationMilliseconds, isAvailable),
            VoiceLocalPath = localPath,
            VoiceFileName = fileName,
            VoiceDurationMilliseconds = durationMilliseconds,
            IsVoiceAvailable = isAvailable,
        };
    }

    public static AvaQQMessageSegment CreateVideo(
        string? videoLocalPath,
        string? coverLocalPath,
        string? fileName,
        string? coverFileName,
        int? width,
        int? height,
        int? durationMilliseconds,
        bool isVideoAvailable,
        bool isCoverAvailable)
    {
        return new AvaQQMessageSegment
        {
            Type = AvaQQMessageSegmentType.Video,
            Tone = isVideoAvailable ? AvaQQMessageSegmentTone.Normal : AvaQQMessageSegmentTone.Warning,
            Text = isVideoAvailable ? "[视频]" : "[视频文件未找到]",
            VideoLocalPath = videoLocalPath,
            VideoCoverLocalPath = coverLocalPath,
            VideoFileName = fileName,
            VideoCoverFileName = coverFileName,
            VideoDurationMilliseconds = durationMilliseconds,
            ImageWidth = width,
            ImageHeight = height,
            IsVideoAvailable = isVideoAvailable,
            IsVideoCoverAvailable = isCoverAvailable,
        };
    }

    public static AvaQQMessageSegment CreateForwardedMessage(ForwardedMessageCard card)
    {
        return new AvaQQMessageSegment
        {
            Type = AvaQQMessageSegmentType.ForwardedMessage,
            ForwardedMessage = card,
        };
    }

    public static AvaQQMessageSegment CreateSharedContact(SharedContactCard card)
    {
        return new AvaQQMessageSegment
        {
            Type = AvaQQMessageSegmentType.SharedContact,
            SharedContact = card,
        };
    }

    public static AvaQQMessageSegment CreateMiniApp(MiniAppCard card)
    {
        return new AvaQQMessageSegment
        {
            Type = AvaQQMessageSegmentType.MiniApp,
            MiniApp = card,
        };
    }

    private static string FormatVoiceDisplayText(int? durationMilliseconds, bool isAvailable)
    {
        if (!isAvailable)
            return "[语音文件未找到]";

        return durationMilliseconds is > 0
            ? $"[语音 {FormatVoiceDuration(durationMilliseconds.Value)}]"
            : "[语音]";
    }

    public static string FormatVoiceDuration(int milliseconds)
    {
        var seconds = Math.Max(1, (int)Math.Round(milliseconds / 1000d, MidpointRounding.AwayFromZero));
        if (seconds < 60)
            return $"{seconds}\"";

        return $"{seconds / 60}:{seconds % 60:00}";
    }
}

public enum AvaQQMessageSegmentType
{
    Text,
    QQFace,
    Image,
    Voice,
    Video,
    ForwardedMessage,
    SharedContact,
    MiniApp,
    Unsupported,
}

public enum AvaQQMessageSegmentTone
{
    Normal,
    Warning,
    Mention,
}
