using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using Avalonia.VisualTree;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.Controls;

public static class MessageInlineRenderer
{
    public const char PlaceholderChar = '\uFFFC';

    private const double FaceFontScale = 1.12;
    private const double FaceLineBoxScale = 1.18;
    private const double FaceBaselineScale = 0.88;
    private const double ImageMaxWidth = 240;
    private const double ImageMaxHeight = 180;
    private const double BrokenImageMinWidth = 88;
    private const double BrokenImageMinHeight = 72;
    private const double VoiceCardHeight = 24;
    private const double VoiceCardBaseWidth = 58;
    private const double VoiceCardMinWidth = 66;
    private const double VoiceCardMaxWidth = 104;
    private const double MessageCardWidth = 270;
    private static readonly Thickness ForwardedCardPadding = new(12, 10, 12, 9);
    private static readonly Thickness SharedContactCardPadding = new(12, 9, 12, 8);
    private const double ForwardedCardTitleHeight = 22;
    private const double ForwardedCardPreviewLineHeight = 18;
    private const double ForwardedCardSeparatorTopMargin = 8;
    private const double ForwardedCardSeparatorHeight = 1;
    private const double ForwardedCardFooterTopMargin = 7;
    private const double ForwardedCardFooterHeight = 18;
    private const double SharedContactAvatarSize = 46;
    private const double SharedContactColumnSpacing = 12;
    private const double SharedContactTitleHeight = 18;
    private const double SharedContactSubtitleLineHeight = 16;
    private const double SharedContactBodyBottomMargin = 9;
    private const double SharedContactFooterTopMargin = 7;
    private const double SharedContactFooterHeight = 17;
    private const double ReplyPreviewMaxWidth = 560;
    private const double ReplyPreviewMaxHeight = 70;

    public static readonly FontFamily TextFontFamily = new("Inter, Microsoft YaHei UI, Segoe UI Emoji, fonts:QQDatabaseExplorerEmoji#Noto Color Emoji");
    public static readonly FontFamily EmojiFontFamily = new("fonts:QQDatabaseExplorerEmoji#Noto Color Emoji, Segoe UI Emoji, Inter, Microsoft YaHei UI");

    public static readonly IBrush UnsupportedTextBrush = new SolidColorBrush(Color.FromRgb(220, 38, 38));
    public static readonly IBrush UrlTextBrush = new SolidColorBrush(Color.FromRgb(22, 119, 255));

    public static readonly AttachedProperty<IReadOnlyList<AvaQQMessageSegment>?> SegmentsProperty =
        AvaloniaProperty.RegisterAttached<MessageSelectableTextBlock, IReadOnlyList<AvaQQMessageSegment>?>(
            "Segments",
            typeof(MessageInlineRenderer));

    public static readonly AttachedProperty<AvaQQMessageSegment?> ImageSegmentProperty =
        AvaloniaProperty.RegisterAttached<Control, AvaQQMessageSegment?>(
            "ImageSegment",
            typeof(MessageInlineRenderer));

    public static readonly AttachedProperty<AvaQQMessageSegment?> VoiceSegmentProperty =
        AvaloniaProperty.RegisterAttached<Control, AvaQQMessageSegment?>(
            "VoiceSegment",
            typeof(MessageInlineRenderer));

    public static readonly AttachedProperty<AvaQQMessageSegment?> VideoSegmentProperty =
        AvaloniaProperty.RegisterAttached<Control, AvaQQMessageSegment?>(
            "VideoSegment",
            typeof(MessageInlineRenderer));

    public static readonly AttachedProperty<AvaQQMessageSegment?> ForwardedMessageSegmentProperty =
        AvaloniaProperty.RegisterAttached<Control, AvaQQMessageSegment?>(
            "ForwardedMessageSegment",
            typeof(MessageInlineRenderer));

    static MessageInlineRenderer()
    {
        SegmentsProperty.Changed.AddClassHandler<MessageSelectableTextBlock>((textBlock, e) =>
        {
            textBlock.SetDocument(CreateDocument(textBlock, e.GetNewValue<IReadOnlyList<AvaQQMessageSegment>?>()));
        });
    }

    public static IReadOnlyList<AvaQQMessageSegment>? GetSegments(MessageSelectableTextBlock textBlock)
    {
        return textBlock.GetValue(SegmentsProperty);
    }

    public static void SetSegments(MessageSelectableTextBlock textBlock, IReadOnlyList<AvaQQMessageSegment>? value)
    {
        textBlock.SetValue(SegmentsProperty, value);
    }

    public static AvaQQMessageSegment? GetImageSegment(Control control)
    {
        return control.GetValue(ImageSegmentProperty);
    }

    public static void SetImageSegment(Control control, AvaQQMessageSegment? value)
    {
        control.SetValue(ImageSegmentProperty, value);
    }

    public static AvaQQMessageSegment? GetVoiceSegment(Control control)
    {
        return control.GetValue(VoiceSegmentProperty);
    }

    public static void SetVoiceSegment(Control control, AvaQQMessageSegment? value)
    {
        control.SetValue(VoiceSegmentProperty, value);
    }

    public static AvaQQMessageSegment? GetVideoSegment(Control control)
    {
        return control.GetValue(VideoSegmentProperty);
    }

    public static void SetVideoSegment(Control control, AvaQQMessageSegment? value)
    {
        control.SetValue(VideoSegmentProperty, value);
    }

    public static AvaQQMessageSegment? GetForwardedMessageSegment(Control control)
    {
        return control.GetValue(ForwardedMessageSegmentProperty);
    }

    public static void SetForwardedMessageSegment(Control control, AvaQQMessageSegment? value)
    {
        control.SetValue(ForwardedMessageSegmentProperty, value);
    }

    public static AvaQQMessageSegment? GetSharedContactSegmentAt(MessageSelectableTextBlock textBlock, Point position)
    {
        return textBlock.GetMediaSegmentAt(position, MessageMediaKind.SharedContact);
    }

    public static IReadOnlyList<AvaQQMessageSegment> CreateCompactPreviewSegments(
        IReadOnlyList<AvaQQMessageSegment> segments,
        int maxTextLength = 80)
    {
        if (segments.Count == 0)
            return [];

        var compactSegments = new List<AvaQQMessageSegment>();
        var remainingTextLength = maxTextLength;
        foreach (var segment in segments)
        {
            if (segment.Type == AvaQQMessageSegmentType.Image)
            {
                var imageText = string.IsNullOrWhiteSpace(segment.DisplayText) ? "[图片]" : segment.DisplayText;
                compactSegments.Add(AvaQQMessageSegment.CreateText(imageText, segment.Tone));
                remainingTextLength -= imageText.Length;
                continue;
            }

            if (segment.Type == AvaQQMessageSegmentType.Voice)
            {
                compactSegments.Add(AvaQQMessageSegment.CreateText(segment.DisplayText, segment.Tone));
                remainingTextLength -= segment.DisplayText.Length;
                continue;
            }

            if (segment.Type == AvaQQMessageSegmentType.Video)
            {
                compactSegments.Add(AvaQQMessageSegment.CreateText(segment.DisplayText, segment.Tone));
                remainingTextLength -= segment.DisplayText.Length;
                continue;
            }

            if (segment.Type == AvaQQMessageSegmentType.ForwardedMessage)
            {
                compactSegments.Add(AvaQQMessageSegment.CreateText("[转发消息]"));
                remainingTextLength -= 6;
                continue;
            }

            if (segment.Type == AvaQQMessageSegmentType.SharedContact)
            {
                compactSegments.Add(AvaQQMessageSegment.CreateText(segment.DisplayText));
                remainingTextLength -= segment.DisplayText.Length;
                continue;
            }

            var text = segment.DisplayText;
            if (string.IsNullOrEmpty(text))
                continue;

            if (remainingTextLength <= 0)
                break;

            if (text.Length > remainingTextLength)
                text = text[..remainingTextLength] + "...";

            compactSegments.Add(segment.Type == AvaQQMessageSegmentType.Unsupported
                ? AvaQQMessageSegment.CreateUnsupportedText(text)
                : AvaQQMessageSegment.CreateText(text, segment.Tone, segment.LinkUrl));
            remainingTextLength -= text.Length;
        }

        return compactSegments;
    }

    public static Size MeasurePreview(
        MessageSelectableTextBlock textBlock,
        IReadOnlyList<AvaQQMessageSegment>? segments)
    {
        if (segments is null || segments.Count == 0)
            return default;

        var document = CreateDocument(textBlock, CreateCompactPreviewSegments(segments));
        textBlock.SetDocument(document);
        textBlock.Measure(new Size(ReplyPreviewMaxWidth, ReplyPreviewMaxHeight));
        return textBlock.DesiredSize;
    }

    public static MessageRenderDocument CreateDocument(
        MessageSelectableTextBlock textBlock,
        IReadOnlyList<AvaQQMessageSegment>? segments)
    {
        if (segments is null || segments.Count == 0)
            return MessageRenderDocument.Empty;

        var faceMetrics = GetFaceMetrics(textBlock);
        var runs = new List<MessageRenderRun>();
        var copySpans = new List<MessageCopySpan>();
        var mediaSpans = new List<MessageMediaSpan>();
        var logicalText = new StringBuilder();
        var textPosition = 0;
        var needsLineBreak = false;

        foreach (var segment in segments)
        {
            if (segment.Type == AvaQQMessageSegmentType.QQFace &&
                !string.IsNullOrWhiteSpace(segment.FaceAssetPath))
            {
                if (needsLineBreak)
                {
                    AddLineBreak(runs, copySpans, logicalText, ref textPosition);
                    needsLineBreak = false;
                }

                AddFace(
                    runs,
                    copySpans,
                    mediaSpans,
                    logicalText,
                    segment.FaceAssetPath,
                    MessageCopyPart.CreateAssetImages(segment.DisplayText, [segment.FaceAssetPath], qqFaceId: segment.FaceId, isBlockImage: false),
                    faceMetrics,
                    ref textPosition);
                continue;
            }

            if (segment.Type == AvaQQMessageSegmentType.Image)
            {
                if (textPosition > 0)
                {
                    AddLineBreak(runs, copySpans, logicalText, ref textPosition);
                }

                AddImage(runs, copySpans, mediaSpans, logicalText, segment, ref textPosition);
                needsLineBreak = true;
                continue;
            }

            if (segment.Type == AvaQQMessageSegmentType.Voice)
            {
                if (textPosition > 0)
                {
                    AddLineBreak(runs, copySpans, logicalText, ref textPosition);
                }

                AddVoice(runs, copySpans, mediaSpans, logicalText, segment, ref textPosition);
                needsLineBreak = true;
                continue;
            }

            if (segment.Type == AvaQQMessageSegmentType.Video)
            {
                if (textPosition > 0)
                {
                    AddLineBreak(runs, copySpans, logicalText, ref textPosition);
                }

                AddVideo(runs, copySpans, mediaSpans, logicalText, segment, ref textPosition);
                needsLineBreak = true;
                continue;
            }

            if (segment.Type == AvaQQMessageSegmentType.ForwardedMessage &&
                segment.ForwardedMessage is not null)
            {
                if (textPosition > 0)
                {
                    AddLineBreak(runs, copySpans, logicalText, ref textPosition);
                }

                AddForwardedMessage(runs, copySpans, mediaSpans, logicalText, segment, ref textPosition);
                needsLineBreak = true;
                continue;
            }

            if (segment.Type == AvaQQMessageSegmentType.SharedContact &&
                segment.SharedContact is not null)
            {
                if (textPosition > 0)
                {
                    AddLineBreak(runs, copySpans, logicalText, ref textPosition);
                }

                AddSharedContact(runs, copySpans, mediaSpans, logicalText, segment, ref textPosition);
                needsLineBreak = true;
                continue;
            }

            var text = segment.DisplayText;
            if (string.IsNullOrEmpty(text))
                continue;

            if (needsLineBreak)
            {
                AddLineBreak(runs, copySpans, logicalText, ref textPosition);
                needsLineBreak = false;
            }

            AddTextSegment(runs, copySpans, mediaSpans, logicalText, segment, text, faceMetrics, ref textPosition);
        }

        return new MessageRenderDocument(logicalText.ToString(), runs, copySpans, mediaSpans);
    }

    public static string GetSelectedText(MessageSelectableTextBlock textBlock)
    {
        return GetSelectedPayload(textBlock).PlainText;
    }

    public static MessageCopyPayload GetSelectedPayload(MessageSelectableTextBlock textBlock)
    {
        var start = textBlock.SelectionStart;
        var end = textBlock.SelectionEnd;
        return start == end
            ? MessageCopyPayload.Empty
            : textBlock.Document.GetPayloadRange(start, end);
    }

    public static MessageCopyPayload GetPayload(MessageSelectableTextBlock textBlock)
    {
        return textBlock.Document.GetPayload();
    }

    public static string? GetLinkUrlAt(MessageSelectableTextBlock textBlock, Point position)
    {
        var hit = textBlock.HitTestText(position);
        return textBlock.Document.GetLinkUrlAt(hit.TextPosition);
    }

    public static AvaQQMessageSegment? GetImageSegmentAt(MessageSelectableTextBlock textBlock, Point position)
    {
        return textBlock.GetMediaSegmentAt(position, imageOnly: true);
    }

    public static AvaQQMessageSegment? GetForwardedMessageSegmentAt(MessageSelectableTextBlock textBlock, Point position)
    {
        return textBlock.GetMediaSegmentAt(position, MessageMediaKind.ForwardedMessage);
    }

    public static AvaQQMessageSegment? GetVoiceSegmentAt(MessageSelectableTextBlock textBlock, Point position)
    {
        return textBlock.GetMediaSegmentAt(position, MessageMediaKind.Voice);
    }

    public static AvaQQMessageSegment? GetVideoSegmentAt(MessageSelectableTextBlock textBlock, Point position)
    {
        return textBlock.GetMediaSegmentAt(position, MessageMediaKind.Video);
    }

    public static AvaQQMessageSegment? GetImageSegmentByBounds(Visual root, Point position)
    {
        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            var segment = GetImageSegment(control);
            if (segment is null)
                continue;

            var topLeft = control.TranslatePoint(new Point(0, 0), root);
            if (topLeft is null)
                continue;

            var bounds = new Rect(topLeft.Value, control.Bounds.Size);
            if (bounds.Contains(position))
                return segment;
        }

        return null;
    }

    public static AvaQQMessageSegment? GetForwardedMessageSegmentByBounds(Visual root, Point position)
    {
        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            var segment = GetForwardedMessageSegment(control);
            if (segment is null)
                continue;

            var topLeft = control.TranslatePoint(new Point(0, 0), root);
            if (topLeft is null)
                continue;

            var bounds = new Rect(topLeft.Value, control.Bounds.Size);
            if (bounds.Contains(position))
                return segment;
        }

        return null;
    }

    public static AvaQQMessageSegment? GetSharedContactSegmentByBounds(Visual root, Point position)
    {
        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            var segment = control.DataContext as AvaQQMessageSegment;
            if (segment?.Type != AvaQQMessageSegmentType.SharedContact)
                continue;

            var topLeft = control.TranslatePoint(new Point(0, 0), root);
            if (topLeft is null)
                continue;

            var bounds = new Rect(topLeft.Value, control.Bounds.Size);
            if (bounds.Contains(position))
                return segment;
        }

        return null;
    }

    public static AvaQQMessageSegment? GetVoiceSegmentByBounds(Visual root, Point position)
    {
        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            var segment = control.DataContext as AvaQQMessageSegment;
            if (segment?.Type != AvaQQMessageSegmentType.Voice)
                continue;

            var topLeft = control.TranslatePoint(new Point(0, 0), root);
            if (topLeft is null)
                continue;

            var bounds = new Rect(topLeft.Value, control.Bounds.Size);
            if (bounds.Contains(position))
                return segment;
        }

        return null;
    }

    public static AvaQQMessageSegment? GetVideoSegmentByBounds(Visual root, Point position)
    {
        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            var segment = GetVideoSegment(control);
            if (segment is null)
                continue;

            var topLeft = control.TranslatePoint(new Point(0, 0), root);
            if (topLeft is null)
                continue;

            var bounds = new Rect(topLeft.Value, control.Bounds.Size);
            if (bounds.Contains(position))
                return segment;
        }

        return null;
    }

    private static void AddTextSegment(
        List<MessageRenderRun> runs,
        List<MessageCopySpan> copySpans,
        List<MessageMediaSpan> mediaSpans,
        StringBuilder logicalText,
        AvaQQMessageSegment segment,
        string text,
        FaceMetrics faceMetrics,
        ref int textPosition)
    {
        if (!CanReplaceUnicodeEmoji(segment))
        {
            AddTextRun(runs, copySpans, logicalText, segment, text, ref textPosition);
            return;
        }

        var runStart = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            var elementIndex = enumerator.ElementIndex;
            var emojiFace = QQFaceCatalog.GetUnicodeEmoji(element);
            var emojiAssetPath = emojiFace?.AssetPath;
            if (string.IsNullOrWhiteSpace(emojiAssetPath))
                continue;

            if (elementIndex > runStart)
            {
                AddTextRun(runs, copySpans, logicalText, segment, text[runStart..elementIndex], ref textPosition);
            }

            AddFace(
                runs,
                copySpans,
                mediaSpans,
                logicalText,
                emojiAssetPath,
                MessageCopyPart.CreateText(element, segment.Tone, segment.LinkUrl),
                faceMetrics,
                ref textPosition);
            runStart = elementIndex + element.Length;
        }

        if (runStart < text.Length)
        {
            AddTextRun(runs, copySpans, logicalText, segment, text[runStart..], ref textPosition);
        }
    }

    private static bool CanReplaceUnicodeEmoji(AvaQQMessageSegment segment)
    {
        return segment.Type == AvaQQMessageSegmentType.Text &&
               segment.Tone == AvaQQMessageSegmentTone.Normal &&
               string.IsNullOrWhiteSpace(segment.LinkUrl);
    }

    private static void AddTextRun(
        List<MessageRenderRun> runs,
        List<MessageCopySpan> copySpans,
        StringBuilder logicalText,
        AvaQQMessageSegment segment,
        string text,
        ref int textPosition)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var copyStart = textPosition;
        var part = MessageCopyPart.CreateText(text, segment.Tone, segment.LinkUrl);
        var foreground = GetTextBrush(segment);
        var isLink = segment.LinkUrl is not null;

        if (!EmojiTextRunHelper.MayContainEmojiFontText(text))
        {
            runs.Add(MessageRenderRun.CreateText(textPosition, text, foreground, isLink));
            textPosition += text.Length;
            copySpans.Add(new MessageCopySpan(copyStart, text.Length, part));
            logicalText.Append(text);
            return;
        }

        var runStart = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (!EmojiTextRunHelper.ShouldUseEmojiFont(element))
                continue;

            var elementIndex = enumerator.ElementIndex;
            if (elementIndex > runStart)
            {
                var plainText = text[runStart..elementIndex];
                runs.Add(MessageRenderRun.CreateText(textPosition, plainText, foreground, isLink));
                textPosition += plainText.Length;
            }

            runs.Add(MessageRenderRun.CreateEmojiText(textPosition, element, foreground, isLink));
            textPosition += element.Length;
            runStart = elementIndex + element.Length;
        }

        if (runStart < text.Length)
        {
            var plainText = text[runStart..];
            runs.Add(MessageRenderRun.CreateText(textPosition, plainText, foreground, isLink));
            textPosition += plainText.Length;
        }

        copySpans.Add(new MessageCopySpan(copyStart, text.Length, part));
        logicalText.Append(text);
    }

    private static void AddLineBreak(
        List<MessageRenderRun> runs,
        List<MessageCopySpan> copySpans,
        StringBuilder logicalText,
        ref int textPosition)
    {
        var lineBreakText = Environment.NewLine;
        runs.Add(MessageRenderRun.CreateText(textPosition, lineBreakText, null, false));
        copySpans.Add(new MessageCopySpan(textPosition, lineBreakText.Length, MessageCopyPart.CreateText(lineBreakText)));
        logicalText.Append(lineBreakText);
        textPosition += lineBreakText.Length;
    }

    private static void AddFace(
        List<MessageRenderRun> runs,
        List<MessageCopySpan> copySpans,
        List<MessageMediaSpan> mediaSpans,
        StringBuilder logicalText,
        string assetPath,
        MessageCopyPart copyPart,
        FaceMetrics metrics,
        ref int textPosition)
    {
        var media = MessageMediaRun.Face(textPosition, assetPath, copyPart, metrics.FaceSize, metrics.LineBoxSize, metrics.BaselineOffset);
        runs.Add(MessageRenderRun.CreateMedia(media));
        copySpans.Add(MessageCopySpan.Placeholder(textPosition, copyPart));
        mediaSpans.Add(new MessageMediaSpan(textPosition, 1, media));
        logicalText.Append(PlaceholderChar);
        textPosition++;
    }

    private static void AddImage(
        List<MessageRenderRun> runs,
        List<MessageCopySpan> copySpans,
        List<MessageMediaSpan> mediaSpans,
        StringBuilder logicalText,
        AvaQQMessageSegment segment,
        ref int textPosition)
    {
        var isImageDisplayable = IsImageDisplayable(segment);
        var (width, height) = GetImageDisplaySize(segment.ImageWidth, segment.ImageHeight, segment.ImageMaxWidth, segment.ImageMaxHeight);
        if (!isImageDisplayable && !HasKnownImageSize(segment))
        {
            width = BrokenImageMinWidth;
            height = BrokenImageMinHeight;
        }

        var copyPart = MessageCopyPart.CreateImage(
            segment.DisplayText,
            isImageDisplayable ? segment.ImageLocalPath : null,
            segment.Tone);
        var media = MessageMediaRun.Image(textPosition, segment, copyPart, width, height, isImageDisplayable);

        runs.Add(MessageRenderRun.CreateMedia(media));
        copySpans.Add(MessageCopySpan.Placeholder(textPosition, copyPart));
        mediaSpans.Add(new MessageMediaSpan(textPosition, 1, media));
        logicalText.Append(PlaceholderChar);
        textPosition++;
    }

    private static void AddForwardedMessage(
        List<MessageRenderRun> runs,
        List<MessageCopySpan> copySpans,
        List<MessageMediaSpan> mediaSpans,
        StringBuilder logicalText,
        AvaQQMessageSegment segment,
        ref int textPosition)
    {
        var copyPart = MessageCopyPart.CreateText(segment.DisplayText);
        var height = GetForwardedCardHeight(segment.ForwardedMessage);
        var media = MessageMediaRun.ForwardedMessage(textPosition, segment, copyPart, MessageCardWidth, height);

        runs.Add(MessageRenderRun.CreateMedia(media));
        copySpans.Add(MessageCopySpan.Placeholder(textPosition, copyPart));
        mediaSpans.Add(new MessageMediaSpan(textPosition, 1, media));
        logicalText.Append(PlaceholderChar);
        textPosition++;
    }

    private static void AddVoice(
        List<MessageRenderRun> runs,
        List<MessageCopySpan> copySpans,
        List<MessageMediaSpan> mediaSpans,
        StringBuilder logicalText,
        AvaQQMessageSegment segment,
        ref int textPosition)
    {
        var copyPart = MessageCopyPart.CreateText(segment.DisplayText, segment.Tone);
        var media = MessageMediaRun.Voice(
            textPosition,
            segment,
            copyPart,
            GetVoiceCardWidth(segment),
            VoiceCardHeight,
            segment.IsVoiceAvailable);

        runs.Add(MessageRenderRun.CreateMedia(media));
        copySpans.Add(MessageCopySpan.Placeholder(textPosition, copyPart));
        mediaSpans.Add(new MessageMediaSpan(textPosition, 1, media));
        logicalText.Append(PlaceholderChar);
        textPosition++;
    }

    private static void AddVideo(
        List<MessageRenderRun> runs,
        List<MessageCopySpan> copySpans,
        List<MessageMediaSpan> mediaSpans,
        StringBuilder logicalText,
        AvaQQMessageSegment segment,
        ref int textPosition)
    {
        var isDisplayable = segment.IsVideoCoverAvailable &&
                            LocalMessageImage.CanDisplayImage(segment.VideoCoverLocalPath);
        var (width, height) = GetImageDisplaySize(segment.ImageWidth, segment.ImageHeight);
        if (!isDisplayable && !HasKnownImageSize(segment))
        {
            width = BrokenImageMinWidth;
            height = BrokenImageMinHeight;
        }

        var copyPart = MessageCopyPart.CreateFile(
            segment.DisplayText,
            segment.IsVideoAvailable ? segment.VideoLocalPath : null,
            segment.Tone);
        var media = MessageMediaRun.Video(textPosition, segment, copyPart, width, height, isDisplayable);

        runs.Add(MessageRenderRun.CreateMedia(media));
        copySpans.Add(MessageCopySpan.Placeholder(textPosition, copyPart));
        mediaSpans.Add(new MessageMediaSpan(textPosition, 1, media));
        logicalText.Append(PlaceholderChar);
        textPosition++;
    }

    private static double GetVoiceCardWidth(AvaQQMessageSegment segment)
    {
        var text = segment.IsVoiceAvailable
            ? segment.VoiceDurationMilliseconds is > 0
                ? AvaQQMessageSegment.FormatVoiceDuration(segment.VoiceDurationMilliseconds.Value)
                : "语音"
            : "语音未找到";

        var textWidth = text.Sum(ch => ch < 128 ? 7d : 13d);
        return Math.Clamp(VoiceCardBaseWidth + textWidth, VoiceCardMinWidth, VoiceCardMaxWidth);
    }

    private static void AddSharedContact(
        List<MessageRenderRun> runs,
        List<MessageCopySpan> copySpans,
        List<MessageMediaSpan> mediaSpans,
        StringBuilder logicalText,
        AvaQQMessageSegment segment,
        ref int textPosition)
    {
        var copyPart = MessageCopyPart.CreateText(segment.DisplayText);
        var media = MessageMediaRun.SharedContact(textPosition, segment, copyPart, MessageCardWidth, GetSharedContactCardHeight(segment.SharedContact));

        runs.Add(MessageRenderRun.CreateMedia(media));
        copySpans.Add(MessageCopySpan.Placeholder(textPosition, copyPart));
        mediaSpans.Add(new MessageMediaSpan(textPosition, 1, media));
        logicalText.Append(PlaceholderChar);
        textPosition++;
    }

    private static IBrush? GetTextBrush(AvaQQMessageSegment segment)
    {
        if (!string.IsNullOrWhiteSpace(segment.LinkUrl))
            return UrlTextBrush;

        return segment.Tone == AvaQQMessageSegmentTone.Warning ||
               segment.Type == AvaQQMessageSegmentType.Unsupported
            ? UnsupportedTextBrush
            : null;
    }

    private static FaceMetrics GetFaceMetrics(TextBlock textBlock)
    {
        var fontSize = double.IsFinite(textBlock.FontSize) && textBlock.FontSize > 0
            ? textBlock.FontSize
            : 14;

        var faceSize = double.Ceiling(fontSize * FaceFontScale);
        var lineBoxSize = double.Ceiling(fontSize * FaceLineBoxScale);
        if (lineBoxSize < faceSize)
        {
            lineBoxSize = faceSize;
        }

        var baselineOffset = double.Round(lineBoxSize * FaceBaselineScale, 2);
        return new FaceMetrics(faceSize, lineBoxSize, baselineOffset);
    }

    public static Control CreateMediaControl(MessageMediaRun media)
    {
        return media.Kind switch
        {
            MessageMediaKind.Image => CreateImageControl(media),
            MessageMediaKind.Voice => CreateVoiceControl(media),
            MessageMediaKind.Video => CreateVideoControl(media),
            MessageMediaKind.ForwardedMessage => CreateForwardedMessageControl(media),
            MessageMediaKind.SharedContact => CreateSharedContactControl(media),
            _ => CreateFaceControl(media),
        };
    }

    private static Control CreateVoiceControl(MessageMediaRun media)
    {
        var segment = media.Segment;
        var control = new VoiceMessageInline
        {
            Segment = segment,
            Margin = new Thickness(0),
            DataContext = segment,
            IsHitTestVisible = false,
            Width = media.Width,
            Height = media.Height,
            VerticalAlignment = VerticalAlignment.Center,
        };
        SetVoiceSegment(control, segment);

        return control;
    }

    private static Control CreateVideoControl(MessageMediaRun media)
    {
        if (media.Segment is null || !media.IsDisplayable)
        {
            return CreateBrokenImageControl(media);
        }

        var overlay = media.Segment.IsVideoAvailable
            ? CreateVideoPlayOverlay()
            : CreateUnavailableVideoOverlay();

        var control = new Border
        {
            Width = media.Width,
            Height = media.Height,
            MaxWidth = media.Width,
            MaxHeight = media.Height,
            Margin = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            UseLayoutRounding = true,
            IsHitTestVisible = false,
            Child = new Grid
            {
                Width = media.Width,
                Height = media.Height,
                Children =
                {
                    new LocalMessageImage
                    {
                        SourcePath = media.SourcePath,
                        Width = media.Width,
                        Height = media.Height,
                        MaxWidth = media.Width,
                        MaxHeight = media.Height,
                        Stretch = Stretch.UniformToFill,
                        IsHitTestVisible = false,
                    },
                    overlay,
                },
            },
        };
        RenderOptions.SetBitmapInterpolationMode(control, BitmapInterpolationMode.HighQuality);
        SetVideoSegment(control, media.Segment);
        return control;
    }

    private static Control CreateVideoPlayOverlay()
    {
        return new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(21),
            Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "▶",
                FontSize = 20,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 1),
                IsHitTestVisible = false,
            },
        };
    }

    private static Control CreateUnavailableVideoOverlay()
    {
        return new Border
        {
            Padding = new Thickness(8, 4),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(190, 127, 29, 29)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "视频文件未找到",
                FontSize = 12,
                Foreground = Brushes.White,
                IsHitTestVisible = false,
            },
        };
    }

    private static Control CreateFaceControl(MessageMediaRun media)
    {
        var grid = new Grid
        {
            Width = media.Width,
            Height = media.Height,
            ClipToBounds = false,
            UseLayoutRounding = true,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Children =
            {
                new AnimatedAssetImage
                {
                    SourcePath = media.SourcePath,
                    Width = media.Width,
                    Height = media.Width,
                    Stretch = Stretch.Uniform,
                    ClipToBounds = false,
                    IsHitTestVisible = false,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            },
        };
        RenderOptions.SetBitmapInterpolationMode(grid, BitmapInterpolationMode.HighQuality);
        return grid;
    }

    private static Control CreateImageControl(MessageMediaRun media)
    {
        if (media.Segment is null || !media.IsDisplayable)
        {
            return CreateBrokenImageControl(media);
        }

        var control = new Border
        {
            Width = media.Width,
            Height = media.Height,
            MaxWidth = media.Width,
            MaxHeight = media.Height,
            Margin = new Thickness(0, 2, 0, 2),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            UseLayoutRounding = true,
            IsHitTestVisible = false,
            Child = new LocalMessageImage
            {
                SourcePath = media.SourcePath,
                Width = media.Width,
                Height = media.Height,
                MaxWidth = media.Width,
                MaxHeight = media.Height,
                Stretch = Stretch.Uniform,
                IsHitTestVisible = false,
            },
        };
        RenderOptions.SetBitmapInterpolationMode(control, BitmapInterpolationMode.HighQuality);
        SetImageSegment(control, media.Segment);
        return control;
    }

    private static Control CreateBrokenImageControl(MessageMediaRun media)
    {
        var width = media.Width;
        var height = media.Height;
        var compact = height < BrokenImageMinHeight || width < BrokenImageMinWidth;

        var content = new TextBlock
        {
            Text = media.Segment?.DisplayText ?? media.CopyPart.Text,
            Foreground = UnsupportedTextBrush,
            FontSize = compact ? 10 : 12,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(compact ? 3 : 8, 0),
        };
        Grid.SetRow(content, 1);

        var control = new Border
        {
            Width = width,
            Height = height,
            MaxWidth = width,
            MaxHeight = height,
            Margin = new Thickness(0, 2, 0, 2),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = UnsupportedTextBrush,
            Background = new SolidColorBrush(Color.FromRgb(254, 242, 242)),
            ClipToBounds = true,
            IsHitTestVisible = false,
            Child = new Grid
            {
                ClipToBounds = true,
                RowDefinitions = RowDefinitions.Parse("*,Auto,*"),
                Children =
                {
                    content,
                },
            },
        };
        return control;
    }

    private static Control CreateForwardedMessageControl(MessageMediaRun media)
    {
        var card = media.Segment?.ForwardedMessage;
        var title = string.IsNullOrWhiteSpace(card?.Title) ? "聊天记录" : card!.Title;
        var footer = string.IsNullOrWhiteSpace(card?.Footer) ? "查看转发消息" : card!.Footer;
        var previewLines = card?.PreviewLines.Take(3).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray() ?? [];
        var contentWidth = Math.Max(1, media.Width - ForwardedCardPadding.Left - ForwardedCardPadding.Right);

        var grid = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,Auto,Auto"),
            Width = contentWidth,
        };

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(25, 25, 25)),
            TextAlignment = TextAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = contentWidth,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Height = ForwardedCardTitleHeight,
        };
        Grid.SetRow(titleBlock, 0);
        grid.Children.Add(titleBlock);

        var previewPanel = new StackPanel
        {
            Spacing = 0,
            Margin = new Thickness(0),
        };
        foreach (var line in previewLines)
        {
            previewPanel.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 12,
                Width = contentWidth,
                Height = ForwardedCardPreviewLineHeight,
                Foreground = new SolidColorBrush(Color.FromRgb(117, 117, 117)),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        if (previewLines.Length == 0)
        {
            previewPanel.Children.Add(new TextBlock
            {
                Text = "暂无本地预览",
                FontSize = 12,
                Width = contentWidth,
                Height = ForwardedCardPreviewLineHeight,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        Grid.SetRow(previewPanel, 1);
        grid.Children.Add(previewPanel);

        var separator = new Border
        {
            Height = ForwardedCardSeparatorHeight,
            Margin = new Thickness(0, ForwardedCardSeparatorTopMargin, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(235, 235, 235)),
        };
        Grid.SetRow(separator, 2);
        grid.Children.Add(separator);

        var footerBlock = new TextBlock
        {
            Text = footer,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(128, 128, 128)),
            Width = contentWidth,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Height = ForwardedCardFooterHeight,
            Margin = new Thickness(0, ForwardedCardFooterTopMargin, 0, 0),
        };
        Grid.SetRow(footerBlock, 3);
        grid.Children.Add(footerBlock);

        var control = new Border
        {
            Width = media.Width,
            Height = media.Height,
            Margin = new Thickness(0),
            Padding = ForwardedCardPadding,
            Background = Brushes.Transparent,
            ClipToBounds = true,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
        };

        if (media.Segment is not null)
            SetForwardedMessageSegment(control, media.Segment);

        return control;
    }

    private static double GetForwardedCardHeight(ForwardedMessageCard? card)
    {
        var lineCount = Math.Max(1, Math.Min(3, card?.PreviewLines.Count ?? 0));
        return ForwardedCardPadding.Top +
               ForwardedCardTitleHeight +
               lineCount * ForwardedCardPreviewLineHeight +
               ForwardedCardSeparatorTopMargin +
               ForwardedCardSeparatorHeight +
               ForwardedCardFooterTopMargin +
               ForwardedCardFooterHeight +
               ForwardedCardPadding.Bottom;
    }

    private static Control CreateSharedContactControl(MessageMediaRun media)
    {
        var card = media.Segment?.SharedContact;
        var title = string.IsNullOrWhiteSpace(card?.Title) ? "名片" : card!.Title;
        var subtitle = card?.Subtitle ?? string.Empty;
        var tag = string.IsNullOrWhiteSpace(card?.Tag) ? "推荐" : card!.Tag;
        var contentWidth = Math.Max(1, media.Width - SharedContactCardPadding.Left - SharedContactCardPadding.Right);
        var textWidth = Math.Max(1, contentWidth - SharedContactAvatarSize - SharedContactColumnSpacing);
        var subtitleLineCount = GetSharedContactSubtitleLineCount(card);

        var avatar = new Grid
        {
            Width = SharedContactAvatarSize,
            Height = SharedContactAvatarSize,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(232, 234, 240)),
                    CornerRadius = new CornerRadius(SharedContactAvatarSize / 2),
                },
                new AvatarImage
                {
                    SourceUrl = card?.AvatarUrl,
                    Width = SharedContactAvatarSize,
                    Height = SharedContactAvatarSize,
                    Stretch = Stretch.UniformToFill,
                },
            },
        };

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 14,
            Width = textWidth,
            Height = SharedContactTitleHeight,
            Foreground = new SolidColorBrush(Color.FromRgb(25, 25, 25)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var subtitleBlock = new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Width = textWidth,
            MaxHeight = subtitleLineCount * SharedContactSubtitleLineHeight,
            LineHeight = SharedContactSubtitleLineHeight,
            Foreground = new SolidColorBrush(Color.FromRgb(117, 117, 117)),
            TextWrapping = subtitleLineCount > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = subtitleLineCount,
        };

        var textPanel = new StackPanel
        {
            Spacing = 3,
            Width = textWidth,
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                titleBlock,
                subtitleBlock,
            },
        };

        var body = new Grid
        {
            Width = contentWidth,
            ColumnDefinitions = new ColumnDefinitions
            {
                new(SharedContactAvatarSize, GridUnitType.Pixel),
                new(SharedContactColumnSpacing, GridUnitType.Pixel),
                new(1, GridUnitType.Star),
            },
            Margin = new Thickness(0, 0, 0, SharedContactBodyBottomMargin),
            ClipToBounds = true,
            Children =
            {
                avatar,
                textPanel,
            },
        };
        Grid.SetColumn(textPanel, 2);

        var separator = new Border
        {
            Height = 1,
            Width = contentWidth,
            Background = new SolidColorBrush(Color.FromRgb(238, 238, 238)),
        };
        Grid.SetRow(separator, 1);

        var footer = new TextBlock
        {
            Text = tag,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(117, 117, 117)),
            Width = contentWidth,
            Height = SharedContactFooterHeight,
            Margin = new Thickness(0, SharedContactFooterTopMargin, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetRow(footer, 2);

        return new Border
        {
            Width = media.Width,
            Height = media.Height,
            Margin = new Thickness(0),
            Padding = SharedContactCardPadding,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Cursor = string.IsNullOrWhiteSpace(card?.JumpUrl) ? null : new Cursor(StandardCursorType.Hand),
            DataContext = media.Segment,
            Child = new Grid
            {
                Width = contentWidth,
                ClipToBounds = true,
                RowDefinitions = RowDefinitions.Parse("Auto,Auto,Auto"),
                Children =
                {
                    body,
                    separator,
                    footer,
                },
            },
        };
    }

    private static double GetSharedContactCardHeight(SharedContactCard? card)
    {
        return SharedContactCardPadding.Top +
               Math.Max(
                   SharedContactAvatarSize,
                   SharedContactTitleHeight + 3 + GetSharedContactSubtitleLineCount(card) * SharedContactSubtitleLineHeight) +
               SharedContactBodyBottomMargin +
               ForwardedCardSeparatorHeight +
               SharedContactFooterTopMargin +
               SharedContactFooterHeight +
               SharedContactCardPadding.Bottom;
    }

    private static int GetSharedContactSubtitleLineCount(SharedContactCard? card)
    {
        return card?.Kind == SharedContactCardKind.Group ? 2 : 1;
    }

    private static bool IsImageDisplayable(AvaQQMessageSegment segment)
    {
        return segment.IsImageAvailable && LocalMessageImage.CanDisplayImage(segment.ImageLocalPath);
    }

    private static bool HasKnownImageSize(AvaQQMessageSegment segment)
    {
        return segment.ImageWidth is > 0 && segment.ImageHeight is > 0;
    }

    private static (double Width, double Height) GetImageDisplaySize(
        int? sourceWidth,
        int? sourceHeight,
        int? maxWidth = null,
        int? maxHeight = null)
    {
        var displayMaxWidth = maxWidth is > 0 ? maxWidth.Value : ImageMaxWidth;
        var displayMaxHeight = maxHeight is > 0 ? maxHeight.Value : ImageMaxHeight;
        var width = sourceWidth is > 0 ? sourceWidth.Value : displayMaxWidth;
        var height = sourceHeight is > 0 ? sourceHeight.Value : displayMaxHeight;
        var scale = Math.Min(displayMaxWidth / width, displayMaxHeight / height);
        if (!double.IsFinite(scale) || scale <= 0)
            scale = 1;

        scale = Math.Min(1, scale);
        return (RoundDisplaySize(width * scale), RoundDisplaySize(height * scale));
    }

    private static double RoundDisplaySize(double value)
    {
        return Math.Max(1, Math.Round(value, MidpointRounding.AwayFromZero));
    }

    private readonly record struct FaceMetrics(double FaceSize, double LineBoxSize, double BaselineOffset);
}

public sealed class MessageRenderDocument
{
    public static MessageRenderDocument Empty { get; } = new(string.Empty, [], [], []);

    public MessageRenderDocument(
        string text,
        IReadOnlyList<MessageRenderRun> runs,
        IReadOnlyList<MessageCopySpan> copySpans,
        IReadOnlyList<MessageMediaSpan> mediaSpans)
    {
        Text = text;
        Runs = runs;
        CopySpans = copySpans;
        MediaSpans = mediaSpans;
    }

    public string Text { get; }
    public IReadOnlyList<MessageRenderRun> Runs { get; }
    public IReadOnlyList<MessageCopySpan> CopySpans { get; }
    public IReadOnlyList<MessageMediaSpan> MediaSpans { get; }

    public MessageCopyPayload GetPayload()
    {
        return CopySpans.Count == 0
            ? MessageCopyPayload.Empty
            : MessageCopyPayload.FromParts(CopySpans.Select(span => span.Part));
    }

    public MessageCopyPayload GetPayloadRange(int selectionStart, int selectionEnd)
    {
        if (CopySpans.Count == 0)
            return MessageCopyPayload.Empty;

        var start = Math.Min(selectionStart, selectionEnd);
        var end = Math.Max(selectionStart, selectionEnd);
        if (start == end)
            return MessageCopyPayload.Empty;

        var parts = new List<MessageCopyPart>();
        foreach (var span in CopySpans)
        {
            var spanEnd = span.Start + span.Length;
            var overlapStart = Math.Max(start, span.Start);
            var overlapEnd = Math.Min(end, spanEnd);
            if (overlapStart >= overlapEnd)
                continue;

            var overlapLength = overlapEnd - overlapStart;
            if (span.Part.Kind == MessageCopyPartKind.Text && span.Part.Text.Length == span.Length)
            {
                parts.Add(span.Part.WithText(span.Part.Text.Substring(overlapStart - span.Start, overlapLength)));
            }
            else if (overlapLength == span.Length)
            {
                parts.Add(span.Part);
            }
        }

        return MessageCopyPayload.FromParts(parts);
    }

    public string? GetLinkUrlAt(int textPosition)
    {
        foreach (var span in CopySpans)
        {
            if (string.IsNullOrWhiteSpace(span.Part.LinkUrl))
                continue;

            if (textPosition >= span.Start && textPosition < span.Start + span.Length)
                return span.Part.LinkUrl;
        }

        return null;
    }

    public MessageMediaRun? GetMediaAt(int textPosition, bool imageOnly)
    {
        foreach (var span in MediaSpans)
        {
            if (textPosition < span.Start || textPosition >= span.Start + span.Length)
                continue;

            if (imageOnly && span.Media.Kind != MessageMediaKind.Image)
                return null;

            return span.Media;
        }

        return null;
    }

    public MessageMediaRun? GetMediaAt(int textPosition, MessageMediaKind kind)
    {
        foreach (var span in MediaSpans)
        {
            if (textPosition < span.Start || textPosition >= span.Start + span.Length)
                continue;

            return span.Media.Kind == kind ? span.Media : null;
        }

        return null;
    }
}

public sealed record MessageRenderRun(
    MessageRenderRunKind Kind,
    int Start,
    string Text,
    IBrush? Foreground,
    bool IsLink,
    bool UsesEmojiFont,
    MessageMediaRun? Media)
{
    public int Length => Kind == MessageRenderRunKind.Text ? Text.Length : 1;

    public static MessageRenderRun CreateText(int start, string text, IBrush? foreground, bool isLink)
    {
        return new MessageRenderRun(MessageRenderRunKind.Text, start, text, foreground, isLink, false, null);
    }

    public static MessageRenderRun CreateEmojiText(int start, string text, IBrush? foreground, bool isLink)
    {
        return new MessageRenderRun(MessageRenderRunKind.Text, start, text, foreground, isLink, true, null);
    }

    public static MessageRenderRun CreateMedia(MessageMediaRun media)
    {
        return new MessageRenderRun(MessageRenderRunKind.Media, media.Start, MessageInlineRenderer.PlaceholderChar.ToString(), null, false, false, media);
    }
}

public enum MessageRenderRunKind
{
    Text,
    Media,
}

public sealed record MessageMediaRun(
    MessageMediaKind Kind,
    int Start,
    string? SourcePath,
    AvaQQMessageSegment? Segment,
    MessageCopyPart CopyPart,
    double Width,
    double Height,
    double Baseline,
    bool IsDisplayable)
{
    public static MessageMediaRun Face(
        int start,
        string sourcePath,
        MessageCopyPart copyPart,
        double width,
        double height,
        double baseline)
    {
        return new MessageMediaRun(MessageMediaKind.Face, start, sourcePath, null, copyPart, width, height, baseline, true);
    }

    public static MessageMediaRun Image(
        int start,
        AvaQQMessageSegment segment,
        MessageCopyPart copyPart,
        double width,
        double height,
        bool isDisplayable)
    {
        return new MessageMediaRun(MessageMediaKind.Image, start, segment.ImageLocalPath, segment, copyPart, width, height, Math.Round(height / 2, 2), isDisplayable);
    }

    public static MessageMediaRun Video(
        int start,
        AvaQQMessageSegment segment,
        MessageCopyPart copyPart,
        double width,
        double height,
        bool isDisplayable)
    {
        return new MessageMediaRun(MessageMediaKind.Video, start, segment.VideoCoverLocalPath, segment, copyPart, width, height, Math.Round(height / 2, 2), isDisplayable);
    }

    public static MessageMediaRun ForwardedMessage(
        int start,
        AvaQQMessageSegment segment,
        MessageCopyPart copyPart,
        double width,
        double height)
    {
        return new MessageMediaRun(MessageMediaKind.ForwardedMessage, start, null, segment, copyPart, width, height, height, true);
    }

    public static MessageMediaRun SharedContact(
        int start,
        AvaQQMessageSegment segment,
        MessageCopyPart copyPart,
        double width,
        double height)
    {
        return new MessageMediaRun(MessageMediaKind.SharedContact, start, null, segment, copyPart, width, height, height, true);
    }

    public static MessageMediaRun Voice(
        int start,
        AvaQQMessageSegment segment,
        MessageCopyPart copyPart,
        double width,
        double height,
        bool isDisplayable)
    {
        return new MessageMediaRun(MessageMediaKind.Voice, start, segment.VoiceLocalPath, segment, copyPart, width, height, Math.Round(height * 0.78, 2), isDisplayable);
    }
}

public enum MessageMediaKind
{
    Face,
    Image,
    Voice,
    Video,
    ForwardedMessage,
    SharedContact,
}

public readonly record struct MessageCopySpan(int Start, int Length, MessageCopyPart Part)
{
    public static MessageCopySpan Placeholder(int start, MessageCopyPart part) => new(start, 1, part);
}

public readonly record struct MessageMediaSpan(int Start, int Length, MessageMediaRun Media);
