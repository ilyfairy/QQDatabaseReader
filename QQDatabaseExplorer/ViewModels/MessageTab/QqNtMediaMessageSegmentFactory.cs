using QQDatabaseExplorer.Models;
using QQDatabaseReader;
using QQDatabaseReader.Database;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal static class QqNtMediaMessageSegmentFactory
{
    public static AvaQQMessageSegment CreateVoice(QQMessageSegment segment)
    {
        return AvaQQMessageSegment.CreateVoice(
            null,
            segment.VoiceFileName,
            null,
            isAvailable: false);
    }

    public static AvaQQMessageSegment CreateVideo(QQMessageSegment segment)
    {
        return AvaQQMessageSegment.CreateVideo(
            null,
            null,
            segment.VideoFileName,
            segment.VideoCoverFileName,
            segment.ImageWidth,
            segment.ImageHeight,
            segment.VideoDurationMilliseconds,
            isVideoAvailable: true,
            isCoverAvailable: false);
    }

    public static AvaQQMessageSegment CreateMarketFace(QQMessageSegment segment)
    {
        var displayText = string.IsNullOrWhiteSpace(segment.MarketFaceName)
            ? "[商城表情]"
            : segment.MarketFaceName;
        return AvaQQMessageSegment.CreateImage(
            null,
            MessageMediaDisplaySizes.LimitFaceDisplaySize(segment.MarketFaceWidth),
            MessageMediaDisplaySizes.LimitFaceDisplaySize(segment.MarketFaceHeight),
            displayText,
            MessageMediaDisplaySizes.FaceMaxDisplaySize,
            MessageMediaDisplaySizes.FaceMaxDisplaySize);
    }

    public static AvaQQMessageSegment CreateImage(
        QQMessageSegment segment,
        MessageType messageType,
        SubMessageType subMessageType)
    {
        return AvaQQMessageSegment.CreateImage(
            null,
            segment.ImageWidth,
            segment.ImageHeight,
            QQMessageDisplayText.CreateSegmentText(segment, messageType, subMessageType));
    }
}
