using System;
using System.IO;
using QQDatabaseExplorer.Controls;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class IcalinguaMessageFileSegmentFactory
{
    private readonly Func<IcalinguaMessageFile, AvaQQGroup, string?> _resolveLocalFilePath;
    private readonly Func<IcalinguaMessageFile, string?, AvaQQGroup, string?> _resolveVideoCoverPath;
    private readonly Func<string?, bool> _canPlayVoice;
    private readonly Func<string?, int?> _getVoiceDurationMilliseconds;

    public IcalinguaMessageFileSegmentFactory(
        Func<IcalinguaMessageFile, AvaQQGroup, string?> resolveLocalFilePath,
        Func<IcalinguaMessageFile, string?, AvaQQGroup, string?> resolveVideoCoverPath,
        Func<string?, bool> canPlayVoice,
        Func<string?, int?> getVoiceDurationMilliseconds)
    {
        _resolveLocalFilePath = resolveLocalFilePath;
        _resolveVideoCoverPath = resolveVideoCoverPath;
        _canPlayVoice = canPlayVoice;
        _getVoiceDurationMilliseconds = getVoiceDurationMilliseconds;
    }

    public AvaQQMessageSegment Create(IcalinguaMessageFile file, AvaQQGroup conversation, bool compactImage = false)
    {
        var type = file.Type ?? string.Empty;
        var localPath = _resolveLocalFilePath(file, conversation);
        var isLocalFile = !string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath);
        var displayName = FirstNonEmpty(file.Name, file.Fid, file.Url);

        if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return CreateImageSegment(file, localPath, isLocalFile, compactImage);

        if (type.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return CreateVideoSegment(file, conversation, localPath, isLocalFile, displayName);

        if (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return CreateVoiceSegment(localPath, isLocalFile, displayName);

        return AvaQQMessageSegment.CreateFile(
            isLocalFile ? localPath : null,
            displayName,
            file.Size,
            isLocalFile);
    }

    public static string CreatePreviewText(IcalinguaMessageFile file)
    {
        var type = file.Type ?? string.Empty;
        if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return file.IsFace ? "[动画表情]" : "[图片]";

        if (type.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return "[视频]";

        if (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return "[语音]";

        return "[文件]";
    }

    private static AvaQQMessageSegment CreateImageSegment(
        IcalinguaMessageFile file,
        string? localPath,
        bool isLocalFile,
        bool compactImage)
    {
        var displayText = file.IsFace ? "[动画表情]" : "[图片]";
        var maxSize = compactImage
            ? 18
            : file.IsFace
                ? MessageMediaDisplaySizes.FaceMaxDisplaySize
                : (int?)null;
        if (isLocalFile)
        {
            var imageSize = LocalImageFile.TryGetImageSize(localPath);
            return AvaQQMessageSegment.CreateImage(
                localPath,
                imageSize?.Width ?? file.Width,
                imageSize?.Height ?? file.Height,
                displayText,
                maxSize,
                maxSize);
        }

        var hasRemoteImage = Uri.TryCreate(file.Url, UriKind.Absolute, out var imageUri) &&
                             (imageUri.Scheme == Uri.UriSchemeHttp || imageUri.Scheme == Uri.UriSchemeHttps);
        return hasRemoteImage
            ? AvaQQMessageSegment.CreateText(displayText, linkUrl: file.Url)
            : AvaQQMessageSegment.CreateBrokenImage(
                file.Width,
                file.Height,
                file.IsFace ? "[动画表情文件未找到]" : "[图片文件未找到]",
                maxSize,
                maxSize);
    }

    private AvaQQMessageSegment CreateVideoSegment(
        IcalinguaMessageFile file,
        AvaQQGroup conversation,
        string? localPath,
        bool isLocalFile,
        string displayName)
    {
        var coverPath = _resolveVideoCoverPath(file, localPath, conversation);
        var coverSize = LocalImageFile.TryGetImageSize(coverPath);
        return AvaQQMessageSegment.CreateVideo(
            isLocalFile ? localPath : null,
            coverPath,
            displayName,
            null,
            coverSize?.Width ?? file.Width,
            coverSize?.Height ?? file.Height,
            null,
            isLocalFile,
            LocalImageFile.IsDisplayableImageFile(coverPath));
    }

    private AvaQQMessageSegment CreateVoiceSegment(string? localPath, bool isLocalFile, string displayName)
    {
        var isPlayable = isLocalFile && _canPlayVoice(localPath);
        return AvaQQMessageSegment.CreateVoice(
            isPlayable ? localPath : null,
            displayName,
            isPlayable ? _getVoiceDurationMilliseconds(localPath) : null,
            isPlayable);
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
