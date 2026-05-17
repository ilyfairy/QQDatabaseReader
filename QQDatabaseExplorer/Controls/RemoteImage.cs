using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace QQDatabaseExplorer.Controls;

public class RemoteImage : Image
{
    public static readonly StyledProperty<string?> SourceUrlProperty =
        AvaloniaProperty.Register<RemoteImage, string?>(nameof(SourceUrl));

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> BitmapCache = new();

    private string? _loadingSourceUrl;

    public string? SourceUrl
    {
        get => GetValue(SourceUrlProperty);
        set => SetValue(SourceUrlProperty, value);
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
        _loadingSourceUrl = sourceUrl;
        Source = null;

        if (string.IsNullOrWhiteSpace(sourceUrl))
            return;

        var bitmap = await GetBitmapAsync(sourceUrl);
        if (_loadingSourceUrl == sourceUrl)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Source = bitmap);
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
