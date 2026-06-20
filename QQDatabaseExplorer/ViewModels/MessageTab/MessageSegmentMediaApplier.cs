using System;
using System.IO;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class MessageSegmentMediaApplier
{
    private readonly Func<string?, bool> _canPlayVoice;
    private readonly Func<string?, int?> _getVoiceDurationMilliseconds;

    public MessageSegmentMediaApplier(
        Func<string?, bool> canPlayVoice,
        Func<string?, int?> getVoiceDurationMilliseconds)
    {
        _canPlayVoice = canPlayVoice;
        _getVoiceDurationMilliseconds = getVoiceDurationMilliseconds;
    }

    public void Apply(
        AvaQQMessageSegment displaySegment,
        QQMessageSegment parsedSegment,
        string? localPath,
        bool isMissing,
        string? coverPath,
        bool isCoverAvailable,
        string unavailableMediaText)
    {
        if (displaySegment.Type == AvaQQMessageSegmentType.Voice)
        {
            ApplyVoice(displaySegment, localPath, isMissing);
            return;
        }

        if (displaySegment.Type == AvaQQMessageSegmentType.Video)
        {
            ApplyVideo(displaySegment, localPath, isMissing, coverPath, isCoverAvailable);
            return;
        }

        ApplyImage(displaySegment, parsedSegment, localPath, isMissing, unavailableMediaText);
    }

    private void ApplyVoice(
        AvaQQMessageSegment displaySegment,
        string? localPath,
        bool isMissing)
    {
        displaySegment.VoiceLocalPath = localPath;
        displaySegment.IsVoiceAvailable = !isMissing && _canPlayVoice(localPath);
        displaySegment.VoiceDurationMilliseconds = displaySegment.IsVoiceAvailable
            ? _getVoiceDurationMilliseconds(localPath)
            : null;
        displaySegment.Text = displaySegment.IsVoiceAvailable
            ? CreateVoiceDisplayText(displaySegment.VoiceDurationMilliseconds)
            : "[语音文件未找到]";
        displaySegment.Tone = displaySegment.IsVoiceAvailable
            ? AvaQQMessageSegmentTone.Normal
            : AvaQQMessageSegmentTone.Warning;
    }

    private static void ApplyVideo(
        AvaQQMessageSegment displaySegment,
        string? localPath,
        bool isMissing,
        string? coverPath,
        bool isCoverAvailable)
    {
        displaySegment.VideoLocalPath = localPath;
        displaySegment.VideoCoverLocalPath = coverPath;
        displaySegment.IsVideoAvailable = !isMissing && !string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath);
        displaySegment.IsVideoCoverAvailable = isCoverAvailable;
        displaySegment.Text = displaySegment.IsVideoAvailable ? "[视频]" : "[视频文件未找到]";
        displaySegment.Tone = displaySegment.IsVideoAvailable
            ? AvaQQMessageSegmentTone.Normal
            : AvaQQMessageSegmentTone.Warning;
    }

    private static void ApplyImage(
        AvaQQMessageSegment displaySegment,
        QQMessageSegment parsedSegment,
        string? localPath,
        bool isMissing,
        string unavailableMediaText)
    {
        displaySegment.ImageLocalPath = localPath;
        displaySegment.IsImageAvailable = !isMissing;
        if (isMissing)
        {
            displaySegment.ImageDisplayText = parsedSegment.IsMarketFace
                ? "[商城表情文件未找到]"
                : unavailableMediaText;
        }
    }

    private static string CreateVoiceDisplayText(int? durationMilliseconds)
    {
        return durationMilliseconds is > 0
            ? $"[语音 {AvaQQMessageSegment.FormatVoiceDuration(durationMilliseconds.Value)}]"
            : "[语音]";
    }
}
