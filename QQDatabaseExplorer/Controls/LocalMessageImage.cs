using System;
using System.Collections.Concurrent;
using System.IO;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Labs.Gif;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace QQDatabaseExplorer.Controls;

public class LocalMessageImage : ContentControl
{
    public static readonly StyledProperty<string?> SourcePathProperty =
        AvaloniaProperty.Register<LocalMessageImage, string?>(nameof(SourcePath));

    public static readonly StyledProperty<Stretch> StretchProperty =
        Image.StretchProperty.AddOwner<LocalMessageImage>();

    /// <summary>
    /// 缓存图片控件可直接解码的字节，避免重复磁盘读取。
    /// 每个控件从缓存字节流独立创建 Bitmap，避免 Avalonia 同实例冲突。
    /// </summary>
    private static readonly ConcurrentDictionary<string, Lazy<byte[]?>> ImageBytesCache = new();
    private static readonly ConcurrentDictionary<string, Lazy<bool>> ImageDisplayabilityCache = new();
    private GifStreamSource? _gifSource;
    private IDisposable? _ownedStream;
    private bool _isAttached;

    public LocalMessageImage()
    {
        UseLayoutRounding = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    public string? SourcePath
    {
        get => GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
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
        {
            RenderSource();
        }
        else if (change.Property == StretchProperty)
        {
            ApplyStretch();
        }
    }

    private void RenderSource()
    {
        ClearSource();

        var sourcePath = SourcePath;
        if (!_isAttached || !CanDisplayImage(sourcePath))
            return;

        try
        {
            Content = LocalImageFile.IsGifImage(sourcePath!) || LocalImageFile.IsMarketFaceEncodedGifImage(sourcePath!)
                ? CreateGifImage(sourcePath!)
                : CreateStaticImage(sourcePath!);
        }
        catch
        {
            Content = null;
        }
    }

    private GifImage CreateGifImage(string sourcePath)
    {
        var stream = CreateImageStream(sourcePath);
        _ownedStream = stream;
        try
        {
            _gifSource = GifStreamSource.FromStream(stream);
        }
        catch
        {
            stream.Dispose();
            _ownedStream = null;
            throw;
        }

        var image = new GifImage
        {
            Source = _gifSource,
            IterationCount = IterationCount.Infinite,
            Stretch = Stretch,
            StretchDirection = StretchDirection.Both,
            ClipToBounds = false,
            UseLayoutRounding = true,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        return image;
    }

    private Image CreateStaticImage(string sourcePath)
    {
        using var ms = CreateImageStream(sourcePath);
        var image = new Image
        {
            Source = new Bitmap(ms),
            Stretch = Stretch,
            ClipToBounds = false,
            UseLayoutRounding = true,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        return image;
    }

    private void ApplyStretch()
    {
        switch (Content)
        {
            case Image image:
                image.Stretch = Stretch;
                break;
            case GifImage gifImage:
                gifImage.Stretch = Stretch;
                break;
        }
    }

    private void ClearSource()
    {
        Content = null;
        _gifSource?.Dispose();
        _gifSource = null;
        _ownedStream?.Dispose();
        _ownedStream = null;
    }

    private static byte[] GetOrReadImageBytes(string sourcePath)
    {
        var bytes = ImageBytesCache
            .GetOrAdd(sourcePath,
                static path => new Lazy<byte[]?>(() =>
                {
                    try
                    {
                        return LocalImageFile.ReadDisplayBytes(path);
                    }
                    catch
                    {
                        return null;
                    }
                }))
            .Value!;

        return bytes ?? [];
    }

    public static bool CanDisplayImage(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return false;

        return ImageDisplayabilityCache
            .GetOrAdd(sourcePath,
                static path => new Lazy<bool>(() => CanDisplayImageCore(path)))
            .Value;
    }

    private static bool CanDisplayImageCore(string sourcePath)
    {
        try
        {
            if (LocalImageFile.IsGifImage(sourcePath))
            {
                using var stream = CreateImageStream(sourcePath);
                using var source = GifStreamSource.FromStream(stream);
                return true;
            }

            if (LocalImageFile.IsMarketFaceEncodedGifImage(sourcePath))
            {
                using var stream = CreateImageStream(sourcePath);
                using var source = GifStreamSource.FromStream(stream);
                return true;
            }

            using var imageStream = CreateImageStream(sourcePath);
            using var bitmap = new Bitmap(imageStream);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Stream CreateImageStream(string sourcePath)
    {
        if (!LocalImageFile.IsMarketFaceEncodedGifImage(sourcePath))
            return File.OpenRead(sourcePath);

        return new MemoryStream(GetOrReadImageBytes(sourcePath), writable: false);
    }
}
