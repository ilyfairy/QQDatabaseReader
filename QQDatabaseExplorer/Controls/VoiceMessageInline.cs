using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.Controls;

public sealed class VoiceMessageInline : Control
{
    public static readonly StyledProperty<AvaQQMessageSegment?> SegmentProperty =
        AvaloniaProperty.Register<VoiceMessageInline, AvaQQMessageSegment?>(nameof(Segment));

    private static readonly IBrush AvailableBrush = Brushes.Black;
    private static readonly IBrush MissingBrush = MessageInlineRenderer.UnsupportedTextBrush;
    private static readonly Typeface TextTypeface = new(MessageInlineRenderer.TextFontFamily);

    private static readonly double[] WaveHeights = [8, 12, 16, 10, 14, 9];

    public AvaQQMessageSegment? Segment
    {
        get => GetValue(SegmentProperty);
        set => SetValue(SegmentProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == SegmentProperty)
        {
            if (change.OldValue is AvaQQMessageSegment oldSegment)
                oldSegment.PropertyChanged -= OnSegmentPropertyChanged;

            if (change.NewValue is AvaQQMessageSegment newSegment)
                newSegment.PropertyChanged += OnSegmentPropertyChanged;

            InvalidateVisual();
        }

        base.OnPropertyChanged(change);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (Segment is { } segment)
            segment.PropertyChanged -= OnSegmentPropertyChanged;

        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var segment = Segment;
        var isAvailable = segment?.IsVoiceAvailable == true;
        var isPlaying = isAvailable && segment?.IsVoicePlaying == true;
        var brush = isAvailable ? AvailableBrush : MissingBrush;
        var size = Bounds.Size;

        var iconCenter = new Point(10, size.Height / 2);
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(28, 0, 0, 0)), null, iconCenter, 10, 10);

        if (isPlaying)
            DrawPauseIcon(context, brush, iconCenter);
        else
            DrawPlayIcon(context, brush, iconCenter);

        if (isAvailable)
        {
            var barX = 24d;
            foreach (var height in WaveHeights)
            {
                var top = (size.Height - height) / 2;
                context.DrawRectangle(brush, null, new RoundedRect(new Rect(barX, top, 2, height), 1));
                barX += 4;
            }
        }

        var text = isAvailable
            ? segment?.VoiceDurationMilliseconds is > 0
                ? AvaQQMessageSegment.FormatVoiceDuration(segment.VoiceDurationMilliseconds.Value)
                : "语音"
            : "语音未找到";
        var textLayout = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            TextTypeface,
            12,
            brush)
        {
            MaxTextWidth = Math.Max(0, size.Width - 54),
            TextAlignment = TextAlignment.Left,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        context.DrawText(textLayout, new Point(54, (size.Height - textLayout.Height) / 2));
    }

    private static void DrawPlayIcon(DrawingContext context, IBrush brush, Point center)
    {
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            stream.BeginFigure(new Point(center.X - 3, center.Y - 4.5), isFilled: true);
            stream.LineTo(new Point(center.X - 3, center.Y + 4.5));
            stream.LineTo(new Point(center.X + 4.5, center.Y));
            stream.EndFigure(isClosed: true);
        }

        context.DrawGeometry(brush, null, geometry);
    }

    private static void DrawPauseIcon(DrawingContext context, IBrush brush, Point center)
    {
        context.DrawRectangle(brush, null, new RoundedRect(new Rect(center.X - 3.5, center.Y - 4.5, 2.6, 9), 1));
        context.DrawRectangle(brush, null, new RoundedRect(new Rect(center.X + 0.9, center.Y - 4.5, 2.6, 9), 1));
    }

    private void OnSegmentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AvaQQMessageSegment.IsVoicePlaying))
            InvalidateVisual();
    }
}
