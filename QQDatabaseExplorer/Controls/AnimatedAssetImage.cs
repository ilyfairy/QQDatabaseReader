using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Labs.Gif;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace QQDatabaseExplorer.Controls;

public class AnimatedAssetImage : ContentControl
{
    public static readonly StyledProperty<string?> SourcePathProperty =
        AvaloniaProperty.Register<AnimatedAssetImage, string?>(nameof(SourcePath));

    public static readonly StyledProperty<Stretch> StretchProperty =
        Image.StretchProperty.AddOwner<AnimatedAssetImage>();

    private static readonly ConcurrentDictionary<string, Lazy<Bitmap?>> StaticBitmapCache = new();
    private static readonly ConcurrentDictionary<string, Lazy<ApngAnimation?>> ApngCache = new();
    private GifStreamSource? _gifSource;
    private Stream? _gifStream;
    private CancellationTokenSource? _animationCancellationTokenSource;
    private bool _isAttached;

    public AnimatedAssetImage()
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

        if (!_isAttached || string.IsNullOrWhiteSpace(SourcePath))
            return;

        try
        {
            if (SourcePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                Content = CreateGifImage(SourcePath);
                return;
            }

            if (IsApngAsset(SourcePath) && GetApngAnimation(SourcePath) is { } animation)
            {
                Content = CreateApngImage(animation);
                return;
            }

            Content = CreateStaticImage(SourcePath);
        }
        catch
        {
            Content = null;
        }
    }

    private GifImage CreateGifImage(string sourcePath)
    {
        var stream = File.OpenRead(sourcePath);
        try
        {
            _gifSource = GifStreamSource.FromStream(stream);
            _gifStream = stream;
        }
        catch
        {
            stream.Dispose();
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

    private Image CreateApngImage(ApngAnimation animation)
    {
        var image = new Image
        {
            Source = animation.Frames[0].Bitmap,
            Stretch = Stretch,
            ClipToBounds = false,
            UseLayoutRounding = true,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);

        _animationCancellationTokenSource = new CancellationTokenSource();
        _ = PlayApngAsync(image, animation, _animationCancellationTokenSource.Token);
        return image;
    }

    private Image CreateStaticImage(string sourcePath)
    {
        var image = new Image
        {
            Source = GetStaticBitmap(sourcePath),
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
        _gifStream?.Dispose();
        _gifStream = null;
        _animationCancellationTokenSource?.Cancel();
        _animationCancellationTokenSource?.Dispose();
        _animationCancellationTokenSource = null;
    }

    private static Bitmap? GetStaticBitmap(string sourcePath)
    {
        return StaticBitmapCache
            .GetOrAdd(sourcePath, static path => new Lazy<Bitmap?>(() => LoadStaticBitmap(path)))
            .Value;
    }

    private static Bitmap? LoadStaticBitmap(string sourcePath)
    {
        try
        {
            using var stream = File.OpenRead(sourcePath);
            return new Bitmap(stream);
        }
        catch
        {
            StaticBitmapCache.TryRemove(sourcePath, out _);
            return null;
        }
    }

    private static ApngAnimation? GetApngAnimation(string sourcePath)
    {
        return ApngCache
            .GetOrAdd(sourcePath, static path => new Lazy<ApngAnimation?>(() => LoadApngAnimation(path)))
            .Value;
    }

    private static ApngAnimation? LoadApngAnimation(string sourcePath)
    {
        try
        {
            return ApngAnimation.Load(sourcePath);
        }
        catch
        {
            ApngCache.TryRemove(sourcePath, out _);
            return null;
        }
    }

    private static bool IsApngAsset(string sourcePath)
    {
        return sourcePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
               sourcePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   .Contains("apng", StringComparer.OrdinalIgnoreCase);
    }

    private static async Task PlayApngAsync(Image image, ApngAnimation animation, CancellationToken cancellationToken)
    {
        var frameIndex = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = animation.Frames[frameIndex];
            try
            {
                await Task.Delay(frame.Delay, cancellationToken);
                frameIndex = (frameIndex + 1) % animation.Frames.Count;
                var nextFrame = animation.Frames[frameIndex];
                await Dispatcher.UIThread.InvokeAsync(
                    () => image.Source = nextFrame.Bitmap,
                    DispatcherPriority.Render,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
