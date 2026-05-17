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
    public static readonly StyledProperty<string?> SourceUrlProperty =
        AvaloniaProperty.Register<AvatarImage, string?>(nameof(SourceUrl));

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> BitmapCache = new();

    private string? _loadingSourceUrl;
    private string? _currentSourceUrl;

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

        if (change.Property == SourceUrlProperty)
        {
            _ = LoadSourceAsync(change.GetNewValue<string?>());
        }
    }

    private async Task LoadSourceAsync(string? sourceUrl)
    {
        if (string.Equals(_currentSourceUrl, sourceUrl, StringComparison.Ordinal))
            return;

        _loadingSourceUrl = sourceUrl;

        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            _currentSourceUrl = null;
            Source = null;
            return;
        }

        var bitmap = await GetBitmapAsync(sourceUrl);
        if (_loadingSourceUrl == sourceUrl)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _currentSourceUrl = sourceUrl;
                Source = bitmap;
            });
        }
    }

    private static async Task<Bitmap?> GetBitmapAsync(string sourceUrl)
    {
        var task = BitmapCache.GetOrAdd(sourceUrl, LoadBitmapAsync);
        var bitmap = await task;
        if (bitmap is null)
        {
            BitmapCache.TryRemove(sourceUrl, out _);
        }

        return bitmap;
    }

    private static async Task<Bitmap?> LoadBitmapAsync(string sourceUrl)
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
}
