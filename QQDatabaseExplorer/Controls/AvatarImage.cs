using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace QQDatabaseExplorer.Controls;

public class AvatarImage : Image
{
    public static readonly StyledProperty<string?> SourcePathProperty =
        AvaloniaProperty.Register<AvatarImage, string?>(nameof(SourcePath));

    public static readonly StyledProperty<string?> SourceUrlProperty =
        AvaloniaProperty.Register<AvatarImage, string?>(nameof(SourceUrl));

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> BitmapCache = new();

    private AvatarSourceKey? _loadingSource;
    private AvatarSourceKey? _currentSource;

    public string? SourcePath
    {
        get => GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    public string? SourceUrl
    {
        get => GetValue(SourceUrlProperty);
        set => SetValue(SourceUrlProperty, value);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Clip = new EllipseGeometry(new Rect(finalSize));
        return base.ArrangeOverride(finalSize);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourcePathProperty || change.Property == SourceUrlProperty)
        {
            _ = LoadSourceAsync(SourcePath, SourceUrl);
        }
    }

    private async Task LoadSourceAsync(string? sourcePath, string? sourceUrl)
    {
        var source = AvatarSourceKey.Create(sourcePath, sourceUrl);
        if (Equals(_currentSource, source))
            return;

        _loadingSource = source;

        if (source is null)
        {
            _currentSource = null;
            Source = null;
            return;
        }

        var bitmap = await GetBitmapAsync(source.Value);
        if (bitmap is null &&
            source.Value.Kind == AvatarSourceKind.File &&
            !string.IsNullOrWhiteSpace(sourceUrl))
        {
            bitmap = await GetBitmapAsync(new AvatarSourceKey(AvatarSourceKind.Url, sourceUrl));
        }

        if (Equals(_loadingSource, source))
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _currentSource = source;
                Source = bitmap;
            });
        }
    }

    private static async Task<Bitmap?> GetBitmapAsync(AvatarSourceKey source)
    {
        var task = BitmapCache.GetOrAdd(source.CacheKey, _ => LoadBitmapAsync(source));
        var bitmap = await task;
        if (bitmap is null)
        {
            BitmapCache.TryRemove(source.CacheKey, out _);
        }

        return bitmap;
    }

    private static async Task<Bitmap?> LoadBitmapAsync(AvatarSourceKey source)
    {
        return source.Kind == AvatarSourceKind.File
            ? await LoadFileBitmapAsync(source.Value)
            : await LoadUrlBitmapAsync(source.Value);
    }

    private static async Task<Bitmap?> LoadFileBitmapAsync(string sourcePath)
    {
        try
        {
            await using var stream = File.OpenRead(sourcePath);
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream);
            memoryStream.Position = 0;
            return new Bitmap(memoryStream);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<Bitmap?> LoadUrlBitmapAsync(string sourceUrl)
    {
        try
        {
            var bytes = await HttpClient.GetByteArrayAsync(sourceUrl);
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("QQDatabaseExplorer/1.0");
        return httpClient;
    }

    private readonly record struct AvatarSourceKey(AvatarSourceKind Kind, string Value)
    {
        public string CacheKey => $"{Kind}:{Value}";

        public static AvatarSourceKey? Create(string? sourcePath, string? sourceUrl)
        {
            if (!string.IsNullOrWhiteSpace(sourcePath))
                return new AvatarSourceKey(AvatarSourceKind.File, sourcePath);

            return string.IsNullOrWhiteSpace(sourceUrl)
                ? null
                : new AvatarSourceKey(AvatarSourceKind.Url, sourceUrl);
        }
    }

    private enum AvatarSourceKind
    {
        File,
        Url,
    }
}
