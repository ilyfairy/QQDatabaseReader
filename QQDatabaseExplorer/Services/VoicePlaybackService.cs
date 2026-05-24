using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SilkSharp;
using SilkSharp.Codec;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;

namespace QQDatabaseExplorer.Services;

public sealed class VoicePlaybackService : IVoicePlaybackService, IDisposable
{
    private const int VoiceSampleRate = 24000;

    private static readonly AudioFormat VoiceFormat = new()
    {
        Format = SampleFormat.S16,
        Channels = 1,
        Layout = ChannelLayout.Mono,
        SampleRate = VoiceSampleRate,
    };

    private readonly ConcurrentDictionary<string, VoiceDecodeCacheEntry> _decodeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _syncRoot = new();
    private MiniAudioEngine? _engine;
    private AudioPlaybackDevice? _playbackDevice;
    private SoundPlayer? _currentPlayer;
    private RawDataProvider? _currentProvider;
    private CancellationTokenSource? _currentPlaybackCancellation;
    private string? _currentPlayingPath;
    private bool _disposed;

    public event EventHandler<VoicePlaybackStateChangedEventArgs>? StateChanged;

    public string? CurrentPlayingPath
    {
        get
        {
            lock (_syncRoot)
            {
                return _currentPlayingPath;
            }
        }
    }

    public int? GetDurationMilliseconds(string? localPath)
    {
        return TryGetCacheEntry(localPath)?.DurationMilliseconds;
    }

    public bool CanPlay(string? localPath)
    {
        return TryGetCacheEntry(localPath) is not null;
    }

    public async Task PlayOrStopAsync(string? localPath, CancellationToken cancellationToken = default)
    {
        var cacheEntry = TryGetCacheEntry(localPath);
        if (cacheEntry is null || cacheEntry.Pcm.Length == 0)
            return;

        var fullPath = Path.GetFullPath(localPath!);
        if (StopIfCurrent(fullPath))
            return;

        CancellationTokenSource playbackCancellation;
        SoundPlayer player;
        RawDataProvider provider;
        string? currentPlayingPathBeforeEvent;

        lock (_syncRoot)
        {
            ThrowIfDisposed();
            StopCurrentLocked();

            var playbackDevice = EnsurePlaybackDeviceLocked();
            provider = new RawDataProvider(cacheEntry.Pcm, SampleFormat.S16, VoiceSampleRate);
            player = new SoundPlayer(_engine!, playbackDevice.Format, provider)
            {
                Name = "QQ voice message",
                Volume = 1,
            };
            playbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            player.PlaybackEnded += (_, _) => StopPlayback(player, provider, playbackCancellation);
            playbackDevice.MasterMixer.AddComponent(player);

            _currentProvider = provider;
            _currentPlayer = player;
            _currentPlaybackCancellation = playbackCancellation;
            _currentPlayingPath = fullPath;
            currentPlayingPathBeforeEvent = _currentPlayingPath;

            player.Play();
        }

        NotifyStateChanged(currentPlayingPathBeforeEvent);

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(cacheEntry.DurationMilliseconds + 250), playbackCancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            StopPlayback(player, provider, playbackCancellation);
        }
    }

    public void Stop()
    {
        string? currentPlayingPath;
        lock (_syncRoot)
        {
            currentPlayingPath = _currentPlayingPath;
            StopCurrentLocked();
        }

        if (currentPlayingPath is not null)
            NotifyStateChanged(null);
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
                return;

            _disposed = true;
            StopCurrentLocked();
            _playbackDevice?.Stop();
            _playbackDevice?.Dispose();
            _playbackDevice = null;
            _engine?.Dispose();
            _engine = null;
        }
    }

    private VoiceDecodeCacheEntry? TryGetCacheEntry(string? localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
            return null;

        var fullPath = Path.GetFullPath(localPath);
        try
        {
            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath);
            var length = new FileInfo(fullPath).Length;
            var cacheEntry = _decodeCache.GetOrAdd(fullPath, static path => DecodeVoice(path));
            if (cacheEntry.Length == length && cacheEntry.LastWriteTimeUtc == lastWriteTimeUtc)
                return cacheEntry.IsPlayable ? cacheEntry : null;

            cacheEntry = DecodeVoice(fullPath);
            _decodeCache[fullPath] = cacheEntry;
            return cacheEntry.IsPlayable ? cacheEntry : null;
        }
        catch
        {
            return null;
        }
    }

    private static VoiceDecodeCacheEntry DecodeVoice(string localPath)
    {
        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(localPath);
        var length = new FileInfo(localPath).Length;

        try
        {
            var decoder = new SilkDecoder
            {
                FS_API = VoiceSampleRate,
            };
            var pcm = decoder.Decode(File.ReadAllBytes(localPath));
            var duration = pcm.GetDuration();
            return new VoiceDecodeCacheEntry(
                pcm.Data,
                (int)Math.Clamp(duration, 1, int.MaxValue),
                length,
                lastWriteTimeUtc,
                IsPlayable: pcm.Data.Length > 0);
        }
        catch
        {
            return new VoiceDecodeCacheEntry([], 0, length, lastWriteTimeUtc, IsPlayable: false);
        }
    }

    private AudioPlaybackDevice EnsurePlaybackDeviceLocked()
    {
        if (_playbackDevice is not null)
            return _playbackDevice;

        _engine ??= new MiniAudioEngine([]);
        var defaultDevice = _engine.PlaybackDevices.FirstOrDefault(device => device.IsDefault);
        _playbackDevice = _engine.InitializePlaybackDevice(defaultDevice, VoiceFormat, new MiniAudioDeviceConfig());
        _playbackDevice.Start();
        return _playbackDevice;
    }

    private void StopPlayback(
        SoundPlayer player,
        RawDataProvider provider,
        CancellationTokenSource playbackCancellation)
    {
        string? currentPlayingPath;
        lock (_syncRoot)
        {
            if (!ReferenceEquals(_currentPlayer, player))
                return;

            currentPlayingPath = _currentPlayingPath;
            StopCurrentLocked();
        }

        if (currentPlayingPath is not null)
            NotifyStateChanged(null);
    }

    private void StopCurrentLocked()
    {
        _currentPlaybackCancellation?.Cancel();

        if (_currentPlayer is not null)
        {
            try
            {
                _currentPlayer.Stop();
                _playbackDevice?.MasterMixer.RemoveComponent(_currentPlayer);
            }
            catch
            {
            }

            _currentPlayer.Dispose();
            _currentPlayer = null;
        }

        _currentProvider?.Dispose();
        _currentProvider = null;

        _currentPlaybackCancellation?.Dispose();
        _currentPlaybackCancellation = null;
        _currentPlayingPath = null;
    }

    private bool StopIfCurrent(string fullPath)
    {
        string? currentPlayingPath;
        lock (_syncRoot)
        {
            if (!string.Equals(_currentPlayingPath, fullPath, StringComparison.OrdinalIgnoreCase))
                return false;

            currentPlayingPath = _currentPlayingPath;
            StopCurrentLocked();
        }

        if (currentPlayingPath is not null)
            NotifyStateChanged(null);

        return true;
    }

    private void NotifyStateChanged(string? currentPlayingPath)
    {
        StateChanged?.Invoke(this, new VoicePlaybackStateChangedEventArgs(currentPlayingPath));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record VoiceDecodeCacheEntry(
        byte[] Pcm,
        int DurationMilliseconds,
        long Length,
        DateTime LastWriteTimeUtc,
        bool IsPlayable);
}
