using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Rendering;
using Avalonia.Utilities;
using Avalonia.VisualTree;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.Controls;

public class MessageSelectableTextBlock : SelectableTextBlock, ICustomHitTest
{
    private const double DrawableRunWrapSlack = 1d / 1024d;
    private static readonly IBrush DefaultSelectionBrush = new SolidColorBrush(Color.FromArgb(120, 0, 120, 215));
    private static readonly Cursor IBeamCursor = new(StandardCursorType.Ibeam);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);

    private readonly Dictionary<MessageMediaRun, Control> _mediaControls = new();
    private readonly Dictionary<MessageMediaRun, Rect> _mediaBounds = new();

    private TopLevel? _keyboardHost;
    private bool _isOpeningLink;
    private bool _isPointerOver;
    private Point _lastPointerPosition;

    static MessageSelectableTextBlock()
    {
        BackgroundProperty.OverrideDefaultValue<MessageSelectableTextBlock>(Brushes.Transparent);
        CursorProperty.OverrideDefaultValue<MessageSelectableTextBlock>(IBeamCursor);
        SelectionBrushProperty.OverrideDefaultValue<MessageSelectableTextBlock>(DefaultSelectionBrush);
        TextAlignmentProperty.OverrideDefaultValue<MessageSelectableTextBlock>(TextAlignment.Left);
    }

    public MessageRenderDocument Document { get; private set; } = MessageRenderDocument.Empty;

    public bool HitTest(Point point)
    {
        // Avalonia normally hit-tests TextBlock by rendered glyphs. Messages need
        // browser-like selection where line whitespace and bubble padding can start a drag.
        return new Rect(Bounds.Size).Contains(point);
    }

    internal void SetDocument(MessageRenderDocument document)
    {
        Document = document;
        Text = document.Text;

        var selectionStart = Math.Clamp(SelectionStart, 0, document.Text.Length);
        var selectionEnd = Math.Clamp(SelectionEnd, 0, document.Text.Length);
        SetCurrentValue(SelectionStartProperty, selectionStart);
        SetCurrentValue(SelectionEndProperty, selectionEnd);

        SynchronizeMediaControls();
        InvalidateTextLayout();
    }

    public TextHitTestResult HitTestText(Point position)
    {
        var textPoint = position - GetTextOrigin();
        return TextLayout.HitTestPoint(textPoint);
    }

    public AvaQQMessageSegment? GetMediaSegmentAt(Point position, bool imageOnly)
    {
        var media = GetMediaAt(position, imageOnly);
        return media?.Segment;
    }

    public AvaQQMessageSegment? GetMediaSegmentAt(Point position, MessageMediaKind kind)
    {
        var media = GetMediaAt(position, kind);
        return media?.Segment;
    }

    protected override TextLayout CreateTextLayout(string? text)
    {
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);

        var defaultProperties = new GenericTextRunProperties(
            typeface,
            FontSize,
            TextDecorations,
            Foreground,
            fontFeatures: FontFeatures);

        var paragraphProperties = new GenericTextParagraphProperties(
            FlowDirection,
            TextAlignment,
            true,
            false,
            defaultProperties,
            TextWrapping,
            LineHeight,
            0,
            LetterSpacing);

        var textRuns = BuildTextRuns(defaultProperties);
        var textStyleOverrides = BuildSelectionStyleOverrides(typeface);
        var textSource = new MessageTextSource(textRuns, textStyleOverrides);
        var maxSize = GetFormatterMaxSize();

        return new TextLayout(
            textSource,
            paragraphProperties,
            TextTrimming,
            maxSize.Width,
            maxSize.Height,
            MaxLines);
    }

    private Size GetFormatterMaxSize()
    {
        var maxSize = GetMaxSizeFromConstraint();

        if (Document.MediaSpans.Count == 0 ||
            TextWrapping == TextWrapping.NoWrap ||
            !double.IsFinite(maxSize.Width) ||
            maxSize.Width <= 0)
        {
            return maxSize;
        }

        // Avalonia 12.0.2 wraps DrawableTextRun when its right edge is exactly
        // equal to paragraphWidth. Message media runs are drawable placeholders,
        // so a naturally measured line like "emoji emoji emoji" can reflow the
        // last item into a clipped extra line during arrange. A sub-pixel slack
        // preserves the measured layout without changing visible width.
        return maxSize.WithWidth(maxSize.Width + DrawableRunWrapSlack);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);

        foreach (var (media, control) in _mediaControls)
        {
            control.Measure(new Size(media.Width, media.Height));
        }

        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        ArrangeMediaControls();
        return arranged;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed &&
            MessageInlineRenderer.GetLinkUrlAt(this, e.GetPosition(this)) is { Length: > 0 } url)
        {
            e.Handled = true;
            e.PreventGestureRecognition();
            _isOpeningLink = true;
            e.Pointer.Capture(this);
            _ = OpenLinkAsync(url);
            return;
        }

        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_isOpeningLink)
        {
            e.Handled = true;
            e.PreventGestureRecognition();
            return;
        }

        base.OnPointerMoved(e);
        _lastPointerPosition = e.GetPosition(this);
        UpdateCursor(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_isOpeningLink)
        {
            _isOpeningLink = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            e.PreventGestureRecognition();
            return;
        }

        base.OnPointerReleased(e);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _isOpeningLink = false;
        base.OnPointerCaptureLost(e);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _isPointerOver = true;
        _lastPointerPosition = e.GetPosition(this);
        AttachKeyboardHost();
        UpdateCursor(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _isPointerOver = false;
        DetachKeyboardHost();
        Cursor = IBeamCursor;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachKeyboardHost();
        _isPointerOver = false;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsCopyGesture(e) && TryCopySelectedPayload())
        {
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
        UpdateCursorForKeyboard(e.KeyModifiers);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        UpdateCursorForKeyboard(e.KeyModifiers);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == FontSizeProperty ||
            change.Property == FontFamilyProperty ||
            change.Property == FontWeightProperty ||
            change.Property == FontStyleProperty ||
            change.Property == FontStretchProperty ||
            change.Property == ForegroundProperty ||
            change.Property == TextWrappingProperty ||
            change.Property == PaddingProperty ||
            change.Property == LineHeightProperty ||
            change.Property == LineSpacingProperty ||
            change.Property == TextAlignmentProperty ||
            change.Property == TextTrimmingProperty ||
            change.Property == MaxLinesProperty)
        {
            var segments = MessageInlineRenderer.GetSegments(this);
            if (segments is not null)
            {
                SetDocument(MessageInlineRenderer.CreateDocument(this, segments));
            }
        }
    }

    private IReadOnlyList<TextRun> BuildTextRuns(TextRunProperties defaultProperties)
    {
        if (Document.Runs.Count == 0)
            return [];

        var textRuns = new List<TextRun>(Document.Runs.Count);
        foreach (var run in Document.Runs)
        {
            if (run.Kind == MessageRenderRunKind.Text)
            {
                textRuns.Add(new TextCharacters(run.Text, CreateRunProperties(defaultProperties, run)));
                continue;
            }

            if (run.Media is { } media)
            {
                textRuns.Add(new MessageMediaTextRun(media, CreateRunProperties(defaultProperties, run)));
            }
        }

        return textRuns;
    }

    private TextRunProperties CreateRunProperties(TextRunProperties defaultProperties, MessageRenderRun run)
    {
        if (run.Foreground is null && !run.IsLink)
            return defaultProperties;

        return new GenericTextRunProperties(
            defaultProperties.Typeface,
            defaultProperties.FontRenderingEmSize,
            run.IsLink ? Avalonia.Media.TextDecorations.Underline : TextDecorations,
            run.Foreground ?? defaultProperties.ForegroundBrush,
            defaultProperties.BackgroundBrush,
            defaultProperties.BaselineAlignment,
            defaultProperties.CultureInfo,
            defaultProperties.FontFeatures);
    }

    private IReadOnlyList<ValueSpan<TextRunProperties>>? BuildSelectionStyleOverrides(Typeface typeface)
    {
        var selectionStart = SelectionStart;
        var selectionEnd = SelectionEnd;
        var start = Math.Min(selectionStart, selectionEnd);
        var length = Math.Max(selectionStart, selectionEnd) - start;

        if (length <= 0 || SelectionForegroundBrush is null)
            return null;

        return
        [
            new ValueSpan<TextRunProperties>(
                start,
                length,
                new GenericTextRunProperties(
                    typeface,
                    FontSize,
                    foregroundBrush: SelectionForegroundBrush,
                    fontFeatures: FontFeatures))
        ];
    }

    private void SynchronizeMediaControls()
    {
        var currentMedia = Document.MediaSpans.Select(span => span.Media).ToHashSet();

        foreach (var pair in _mediaControls.ToArray())
        {
            if (currentMedia.Contains(pair.Key))
                continue;

            VisualChildren.Remove(pair.Value);
            LogicalChildren.Remove(pair.Value);
            _mediaControls.Remove(pair.Key);
            _mediaBounds.Remove(pair.Key);
        }

        foreach (var media in currentMedia)
        {
            if (_mediaControls.ContainsKey(media))
                continue;

            var control = MessageInlineRenderer.CreateMediaControl(media);
            _mediaControls.Add(media, control);
            VisualChildren.Add(control);
            LogicalChildren.Add(control);
        }
    }

    private void ArrangeMediaControls()
    {
        _mediaBounds.Clear();

        var textOrigin = GetTextOrigin();
        foreach (var line in TextLayout.TextLines)
        {
            var currentX = textOrigin.X + line.Start;
            foreach (var run in line.TextRuns)
            {
                if (run is MessageMediaTextRun mediaRun &&
                    _mediaControls.TryGetValue(mediaRun.Media, out var control))
                {
                    var offsetY = GetBaselineOffset(line, mediaRun);
                    var point = new Point(currentX, textOrigin.Y + GetLineTop(line) + offsetY);
                    var rect = new Rect(point, mediaRun.Size);
                    control.Arrange(rect);
                    _mediaBounds[mediaRun.Media] = rect;
                    currentX += mediaRun.Size.Width;
                    continue;
                }

                if (run is DrawableTextRun drawable)
                {
                    currentX += drawable.Size.Width;
                }
            }
        }
    }

    private Point GetTextOrigin()
    {
        var padding = Padding;
        if (UseLayoutRounding)
        {
            var scale = LayoutHelper.GetLayoutScale(this);
            padding = LayoutHelper.RoundLayoutThickness(padding, scale);
        }

        var top = padding.Top;
        var textHeight = TextLayout.Height;

        if (Bounds.Height < textHeight)
        {
            switch (VerticalAlignment)
            {
                case VerticalAlignment.Center:
                    top += (Bounds.Height - textHeight) / 2;
                    break;
                case VerticalAlignment.Bottom:
                    top += Bounds.Height - textHeight;
                    break;
            }
        }

        return new Point(padding.Left, top);
    }

    private double GetLineTop(TextLine targetLine)
    {
        var y = 0d;
        foreach (var line in TextLayout.TextLines)
        {
            if (ReferenceEquals(line, targetLine))
                return y;

            y += line.Height;
        }

        return y;
    }

    private static double GetBaselineOffset(TextLine textLine, DrawableTextRun textRun)
    {
        var baseline = textRun.Baseline;
        var baselineAlignment = textRun.Properties?.BaselineAlignment;

        var baselineOffset = -baseline;

        switch (baselineAlignment)
        {
            case BaselineAlignment.Baseline:
                baselineOffset += textLine.Baseline;
                break;
            case BaselineAlignment.Top:
            case BaselineAlignment.TextTop:
                baselineOffset += textLine.Height - textLine.Extent + textRun.Size.Height / 2;
                break;
            case BaselineAlignment.Center:
                baselineOffset += textLine.Height / 2 + baseline - textRun.Size.Height / 2;
                break;
            case BaselineAlignment.Subscript:
            case BaselineAlignment.Bottom:
            case BaselineAlignment.TextBottom:
                baselineOffset += textLine.Height - textRun.Size.Height + baseline;
                break;
            case BaselineAlignment.Superscript:
                baselineOffset += baseline;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(baselineAlignment), baselineAlignment, null);
        }

        return baselineOffset;
    }

    private MessageMediaRun? GetMediaAt(Point position, bool imageOnly)
    {
        foreach (var pair in _mediaBounds)
        {
            if (imageOnly && pair.Key.Kind != MessageMediaKind.Image)
                continue;

            if (pair.Value.Contains(position))
                return pair.Key;
        }

        if (imageOnly)
            return null;

        var hit = HitTestText(position);
        if (!hit.IsInside)
            return null;

        return Document.GetMediaAt(hit.TextPosition, imageOnly);
    }

    private MessageMediaRun? GetMediaAt(Point position, MessageMediaKind kind)
    {
        foreach (var pair in _mediaBounds)
        {
            if (pair.Key.Kind != kind)
                continue;

            if (pair.Value.Contains(position))
                return pair.Key;
        }

        var hit = HitTestText(position);
        if (!hit.IsInside)
            return null;

        return Document.GetMediaAt(hit.TextPosition, kind);
    }

    private void UpdateCursor(PointerEventArgs e)
    {
        UpdateCursorForPosition(e.KeyModifiers, e.GetPosition(this));
    }

    private void UpdateCursorForKeyboard(KeyModifiers keyModifiers)
    {
        if (_isPointerOver)
            UpdateCursorForPosition(keyModifiers, _lastPointerPosition);
    }

    private void UpdateCursorForPosition(KeyModifiers keyModifiers, Point position)
    {
        if (GetMediaAt(position, imageOnly: true) is not null)
        {
            Cursor = ArrowCursor;
            return;
        }

        if (GetMediaAt(position, MessageMediaKind.ForwardedMessage) is not null)
        {
            Cursor = HandCursor;
            return;
        }

        if (GetMediaAt(position, MessageMediaKind.SharedContact) is not null)
        {
            Cursor = HandCursor;
            return;
        }

        var isLinkHover = keyModifiers.HasFlag(KeyModifiers.Control) &&
                          MessageInlineRenderer.GetLinkUrlAt(this, position) is { Length: > 0 };
        Cursor = isLinkHover ? HandCursor : IBeamCursor;
    }

    private void AttachKeyboardHost()
    {
        var host = TopLevel.GetTopLevel(this);
        if (ReferenceEquals(host, _keyboardHost))
            return;

        DetachKeyboardHost();
        if (host is null)
            return;

        _keyboardHost = host;
        host.AddHandler(KeyDownEvent, OnHostKeyChanged, RoutingStrategies.Tunnel, handledEventsToo: true);
        host.AddHandler(KeyUpEvent, OnHostKeyChanged, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void DetachKeyboardHost()
    {
        if (_keyboardHost is null)
            return;

        _keyboardHost.RemoveHandler(KeyDownEvent, OnHostKeyChanged);
        _keyboardHost.RemoveHandler(KeyUpEvent, OnHostKeyChanged);
        _keyboardHost = null;
    }

    private void OnHostKeyChanged(object? sender, KeyEventArgs e)
    {
        UpdateCursorForKeyboard(e.KeyModifiers);
    }

    private bool TryCopySelectedPayload()
    {
        var payload = MessageInlineRenderer.GetSelectedPayload(this);
        if (!payload.HasContent)
            return false;

        var eventArgs = new RoutedEventArgs(CopyingToClipboardEvent);
        RaiseEvent(eventArgs);
        return eventArgs.Handled;
    }

    private static bool IsCopyGesture(KeyEventArgs e)
    {
        if (Application.Current?.PlatformSettings?.HotkeyConfiguration.Copy is { } copyGestures)
        {
            foreach (var gesture in copyGestures)
            {
                if (gesture.Matches(e))
                    return true;
            }
        }

        return e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control);
    }

    private async Task OpenLinkAsync(string url)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is not { } launcher ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        try
        {
            await launcher.LaunchUriAsync(uri);
        }
        catch
        {
            // Opening a link is best-effort; the message selection state has
            // already been isolated from this pointer sequence.
        }
    }

    private readonly struct MessageTextSource : ITextSource
    {
        private readonly IReadOnlyList<TextRun> _textRuns;
        private readonly IReadOnlyList<ValueSpan<TextRunProperties>>? _textModifier;

        public MessageTextSource(
            IReadOnlyList<TextRun> textRuns,
            IReadOnlyList<ValueSpan<TextRunProperties>>? textModifier)
        {
            _textRuns = textRuns;
            _textModifier = textModifier;
        }

        public TextRun? GetTextRun(int textSourceIndex)
        {
            var currentPosition = 0;

            foreach (var textRun in _textRuns)
            {
                if (textRun.Length == 0)
                    continue;

                if (textSourceIndex >= currentPosition + textRun.Length)
                {
                    currentPosition += textRun.Length;
                    continue;
                }

                if (textRun is TextCharacters textCharacters)
                {
                    var skip = Math.Max(0, textSourceIndex - currentPosition);
                    var properties = ApplyTextStyle(
                        textRun.Text.Slice(skip).Span,
                        textSourceIndex,
                        textCharacters.Properties,
                        _textModifier);

                    return new TextCharacters(textRun.Text.Slice(skip, properties.Length), properties.Value);
                }

                return textRun;
            }

            return new TextEndOfParagraph();
        }

        private static ValueSpan<TextRunProperties> ApplyTextStyle(
            ReadOnlySpan<char> text,
            int firstTextSourceIndex,
            TextRunProperties defaultProperties,
            IReadOnlyList<ValueSpan<TextRunProperties>>? textModifier)
        {
            if (textModifier is null || textModifier.Count == 0)
                return new ValueSpan<TextRunProperties>(firstTextSourceIndex, text.Length, defaultProperties);

            foreach (var modifier in textModifier)
            {
                var modifierEnd = modifier.Start + modifier.Length;
                if (modifierEnd <= firstTextSourceIndex)
                    continue;

                if (modifier.Start > firstTextSourceIndex)
                    return new ValueSpan<TextRunProperties>(
                        firstTextSourceIndex,
                        Math.Min(modifier.Start - firstTextSourceIndex, text.Length),
                        defaultProperties);

                return new ValueSpan<TextRunProperties>(
                    firstTextSourceIndex,
                    Math.Min(modifierEnd - firstTextSourceIndex, text.Length),
                    modifier.Value);
            }

            return new ValueSpan<TextRunProperties>(firstTextSourceIndex, text.Length, defaultProperties);
        }
    }

    private sealed class MessageMediaTextRun : DrawableTextRun
    {
        private static readonly ReadOnlyMemory<char> PlaceholderText = MessageInlineRenderer.PlaceholderChar.ToString().AsMemory();

        // Do not use InlineUIContainer here. Avalonia gives embedded controls
        // Length=1 but Text.Length=0, while SelectableTextBlock selection code
        // is text-length based. A real one-character run keeps layout, hit
        // testing, selection, and copy indexes on the same coordinate system.
        public MessageMediaTextRun(MessageMediaRun media, TextRunProperties properties)
        {
            Media = media;
            Properties = properties;
        }

        public MessageMediaRun Media { get; }
        public override int Length => 1;
        public override ReadOnlyMemory<char> Text => PlaceholderText;
        public override TextRunProperties Properties { get; }
        public override Size Size => new(Media.Width, Media.Height);
        public override double Baseline => Media.Baseline;

        public override void Draw(DrawingContext drawingContext, Point origin)
        {
        }
    }
}
