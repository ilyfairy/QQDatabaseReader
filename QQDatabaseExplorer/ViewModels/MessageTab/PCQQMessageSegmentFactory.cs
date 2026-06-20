using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QQDatabaseExplorer.Controls;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class PCQQMessageSegmentFactory
{
    private readonly Func<string?> _getPcqqDataPath;

    public PCQQMessageSegmentFactory(Func<string?> getPcqqDataPath)
    {
        _getPcqqDataPath = getPcqqDataPath;
    }

    public List<AvaQQMessageSegment> Create(PCQQParsedMessage parsed)
    {
        if (parsed.Segments.Count == 0)
        {
            return string.Equals(parsed.DisplayText, "[PCQQ消息]", StringComparison.Ordinal)
                ? [AvaQQMessageSegment.CreateUnsupportedText(parsed.DisplayText)]
                : MessageTextSegmentBuilder.CreateTextSegments(parsed.DisplayText).ToList();
        }

        var pcqqDataPath = _getPcqqDataPath();
        var segments = new List<AvaQQMessageSegment>();
        foreach (var segment in parsed.Segments)
        {
            if (segment.Type == PCQQMessageSegmentType.Text)
            {
                segments.AddRange(MessageTextSegmentBuilder.CreateTextSegments(segment.Text, isMention: segment.IsMention));
                continue;
            }

            if (segment.Type == PCQQMessageSegmentType.Face && segment.FaceId is { } faceId)
            {
                var faceSegment = AvaQQMessageSegment.CreateQQFace(faceId);
                segments.Add(string.IsNullOrWhiteSpace(faceSegment.FaceAssetPath)
                    ? AvaQQMessageSegment.CreateText(segment.Text)
                    : faceSegment);
                continue;
            }

            if (segment.Type == PCQQMessageSegmentType.Image)
            {
                segments.Add(CreateImageSegment(segment, pcqqDataPath));
            }
        }

        return segments;
    }

    private static AvaQQMessageSegment CreateImageSegment(PCQQMessageSegment segment, string? pcqqDataPath)
    {
        var localPath = ResolveImagePath(pcqqDataPath, segment.ImageRelativePath);
        var imageSize = LocalImageFile.TryGetImageSize(localPath);
        var width = segment.ImageWidth ?? imageSize?.Width;
        var height = segment.ImageHeight ?? imageSize?.Height;
        return string.IsNullOrWhiteSpace(localPath)
            ? AvaQQMessageSegment.CreateBrokenImage(null, null, "[图片文件未找到]")
            : AvaQQMessageSegment.CreateImage(
                localPath,
                width,
                height,
                "[图片]");
    }

    private static string? ResolveImagePath(string? pcqqDataPath, string? imageRelativePath)
    {
        if (string.IsNullOrWhiteSpace(pcqqDataPath) ||
            string.IsNullOrWhiteSpace(imageRelativePath))
        {
            return null;
        }

        var relativePath = NormalizeImageRelativePath(imageRelativePath);
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        var imageRoot = Path.Combine(pcqqDataPath, "Image");
        var localPath = Path.GetFullPath(Path.Combine(imageRoot, relativePath));
        if (File.Exists(localPath))
            return localPath;

        var thumbnailPath = CreateThumbnailPath(localPath);
        return File.Exists(thumbnailPath) ? thumbnailPath : null;
    }

    private static string NormalizeImageRelativePath(string imageRelativePath)
    {
        var path = imageRelativePath.Trim();
        foreach (var prefix in new[] { "UserDataImage:", "DataImage:" })
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[prefix.Length..];
                break;
            }
        }

        path = path.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path;
    }

    private static string CreateThumbnailPath(string localPath)
    {
        var directory = Path.GetDirectoryName(localPath);
        var fileName = Path.GetFileNameWithoutExtension(localPath);
        var extension = Path.GetExtension(localPath);
        var thumbnailFileName = string.IsNullOrEmpty(extension)
            ? fileName + "_tmb"
            : fileName + "_tmb" + extension;

        return string.IsNullOrWhiteSpace(directory)
            ? thumbnailFileName
            : Path.Combine(directory, thumbnailFileName);
    }
}
