using System;
using System.IO;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Labs.Gif;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace QQDatabaseExplorer.Controls;

public sealed class ZoomableLocalImageViewer : ContentControl
{
    public static readonly StyledProperty<string?> SourcePathProperty =
        AvaloniaProperty.Register<ZoomableLocalImageViewer, string?>(nameof(SourcePath));

    private const double MinimumScaleFloor = 0.02;
    private const double MaximumScale = 32;

    private readonly Canvas _canvas = new()
    {
        Background = Brushes.Transparent,
        ClipToBounds = true,
    };

    private Bitmap? _bitmap;
    private GifStreamSource? _gifSource;
    private Stream? _ownedStream;
    private Control? _imageControl;
    private Size _imageSize;
    private Size _lastViewportSize;
    private Point? _lastPointerPosition;
    private Point _imageTopLeft;
    private double _scale = 1;
    private bool _isAttached;
    private bool _isPanning;
    private bool _needsFit = true;

    public ZoomableLocalImageViewer()
    {
        Content = _canvas;
        ClipToBounds = true;
        Focusable = true;
    }

    public string? SourcePath
    {
        get => GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        RenderSource();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        ClearSource();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourcePathProperty)
            RenderSource();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arrangedSize = base.ArrangeOverride(finalSize);

        if (_imageControl is not null)
        {
            if (_needsFit || finalSize != _lastViewportSize)
                FitToViewport(finalSize);
            else
                UpdateImageBounds(finalSize);
        }

        _lastViewportSize = finalSize;
        return arrangedSize;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (_imageControl is null || !IsUsableViewport(Bounds.Size))
            return;

        var pointer = e.GetPosition(this);
        var oldScale = _scale;
        var factor = e.Delta.Y > 0 ? 1.1 : 1 / 1.1;
        var newScale = ClampScale(oldScale * factor, Bounds.Size);

        if (Math.Abs(newScale - oldScale) < double.Epsilon)
        {
            e.Handled = true;
            return;
        }

        var imagePoint = new Point(
            (pointer.X - _imageTopLeft.X) / oldScale,
            (pointer.Y - _imageTopLeft.Y) / oldScale);

        _scale = newScale;
        _imageTopLeft = new Point(
            pointer.X - imagePoint.X * newScale,
            pointer.Y - imagePoint.Y * newScale);
        UpdateImageBounds(Bounds.Size);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (_imageControl is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        Focus();
        _isPanning = true;
        _lastPointerPosition = e.GetPosition(this);
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_isPanning || _lastPointerPosition is not { } lastPosition || !Equals(e.Pointer.Captured, this))
            return;

        var currentPosition = e.GetPosition(this);
        _imageTopLeft += currentPosition - lastPosition;
        _lastPointerPosition = currentPosition;
        UpdateImageBounds(Bounds.Size);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        StopPanning(e.Pointer);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        StopPanning(null);
    }

    public void ResetView()
    {
        _needsFit = true;
        InvalidateArrange();
    }

    private void RenderSource()
    {
        ClearSource();

        if (!_isAttached || string.IsNullOrWhiteSpace(SourcePath) || !File.Exists(SourcePath))
            return;

        try
        {
            var isGif = IsGifImage(SourcePath);
            _imageSize = isGif ? ReadGifSize(SourcePath) : ReadBitmapSize(SourcePath);
            _imageControl = isGif ? CreateGifImage(SourcePath) : CreateStaticImage(SourcePath);
            _imageControl.Width = _imageSize.Width;
            _imageControl.Height = _imageSize.Height;
            _canvas.Children.Add(_imageControl);
            ResetView();
        }
        catch
        {
            ClearSource();
        }
    }

    private Image CreateStaticImage(string sourcePath)
    {
        using var stream = LocalImageFile.OpenDisplayStream(sourcePath);
        _bitmap = new Bitmap(stream);
        return new Image
        {
            Source = _bitmap,
            Stretch = Stretch.Fill,
            ClipToBounds = false,
        };
    }

    private GifImage CreateGifImage(string sourcePath)
    {
        var stream = LocalImageFile.OpenDisplayStream(sourcePath);
        try
        {
            _gifSource = GifStreamSource.FromStream(stream);
            _ownedStream = stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        return new GifImage
        {
            Source = _gifSource,
            IterationCount = IterationCount.Infinite,
            Stretch = Stretch.Fill,
            StretchDirection = StretchDirection.Both,
            ClipToBounds = false,
        };
    }

    private void ClearSource()
    {
        _canvas.Children.Clear();
        _imageControl = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _gifSource?.Dispose();
        _gifSource = null;
        _ownedStream?.Dispose();
        _ownedStream = null;
        _imageSize = default;
        _imageTopLeft = default;
        _scale = 1;
        _needsFit = true;
    }

    private void FitToViewport(Size viewport)
    {
        if (!IsUsableViewport(viewport) || !IsUsableImage(_imageSize))
            return;

        var fitScale = GetFitScale(viewport);
        _scale = Math.Min(1, fitScale);
        _imageTopLeft = new Point(
            (viewport.Width - _imageSize.Width * _scale) / 2,
            (viewport.Height - _imageSize.Height * _scale) / 2);
        _needsFit = false;
        UpdateImageBounds(viewport);
    }

    private void UpdateImageBounds(Size viewport)
    {
        if (_imageControl is null || !IsUsableViewport(viewport) || !IsUsableImage(_imageSize))
            return;

        _imageControl.Width = _imageSize.Width * _scale;
        _imageControl.Height = _imageSize.Height * _scale;
        Canvas.SetLeft(_imageControl, _imageTopLeft.X);
        Canvas.SetTop(_imageControl, _imageTopLeft.Y);
    }

    private double ClampScale(double scale, Size viewport)
    {
        var fitScale = GetFitScale(viewport);
        var minScale = Math.Max(MinimumScaleFloor, Math.Min(1, fitScale) * 0.1);
        return Math.Clamp(scale, minScale, MaximumScale);
    }

    private double GetFitScale(Size viewport)
    {
        if (!IsUsableViewport(viewport) || !IsUsableImage(_imageSize))
            return 1;

        return Math.Min(viewport.Width / _imageSize.Width, viewport.Height / _imageSize.Height);
    }

    private void StopPanning(IPointer? pointer)
    {
        if (!_isPanning)
            return;

        _isPanning = false;
        _lastPointerPosition = null;
        pointer?.Capture(null);
        Cursor = null;
    }

    private static Size ReadBitmapSize(string sourcePath)
    {
        using var stream = LocalImageFile.OpenDisplayStream(sourcePath);
        using var bitmap = new Bitmap(stream);
        return bitmap.Size;
    }

    private static Size ReadGifSize(string sourcePath)
    {
        Span<byte> header = stackalloc byte[10];
        using var stream = LocalImageFile.OpenDisplayStream(sourcePath);
        if (stream.Read(header) != header.Length || !IsGifHeader(header))
            throw new InvalidDataException("Invalid GIF image.");

        var width = header[6] | (header[7] << 8);
        var height = header[8] | (header[9] << 8);
        return new Size(width, height);
    }

    private static bool IsGifImage(string sourcePath)
    {
        try
        {
            Span<byte> header = stackalloc byte[6];
            using var stream = LocalImageFile.OpenDisplayStream(sourcePath);
            return stream.Read(header) == header.Length && IsGifHeader(header);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsGifHeader(ReadOnlySpan<byte> header)
    {
        return header.Length >= 6 &&
               header[0] == 'G' &&
               header[1] == 'I' &&
               header[2] == 'F' &&
               header[3] == '8' &&
               (header[4] == '7' || header[4] == '9') &&
               header[5] == 'a';
    }

    private static bool IsUsableViewport(Size viewport)
    {
        return viewport.Width > 0 &&
               viewport.Height > 0 &&
               double.IsFinite(viewport.Width) &&
               double.IsFinite(viewport.Height);
    }

    private static bool IsUsableImage(Size imageSize)
    {
        return imageSize.Width > 0 &&
               imageSize.Height > 0 &&
               double.IsFinite(imageSize.Width) &&
               double.IsFinite(imageSize.Height);
    }
}
