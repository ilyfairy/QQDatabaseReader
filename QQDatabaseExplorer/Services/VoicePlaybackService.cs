using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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
using QQDatabaseReader.Archives;

namespace QQDatabaseExplorer.Services;

public sealed class VoicePlaybackService : IVoicePlaybackService, IDisposable
{
    private const int VoiceSampleRate = 24000;
    private const long MaxDecodeCacheBytes = 128L * 1024 * 1024;
    private const int MaxDecodeCacheEntries = 512;
    private const long MaxEncodedVoiceBytes = 16L * 1024 * 1024;
    private const long MaxDecodedVoiceBytes = 32L * 1024 * 1024;
    private const int SilkMaxEncodedPacketBytes = 1024 * 5;
    private const int SilkMaxFramesPerPacket = 5;
    private const int SilkFrameDurationMilliseconds = 20;
    private const int MaxPcmBytesPerSilkPacket =
        VoiceSampleRate / 1000 * SilkFrameDurationMilliseconds * sizeof(short) * SilkMaxFramesPerPacket;

    private static readonly AudioFormat VoiceFormat = new()
    {
        Format = SampleFormat.S16,
        Channels = 1,
        Layout = ChannelLayout.Mono,
        SampleRate = VoiceSampleRate,
    };

    private readonly Dictionary<string, VoiceDecodeCacheItem> _decodeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _decodeCacheLru = new();
    private readonly object _decodeCacheSyncRoot = new();
    private readonly object _syncRoot = new();
    private long _decodeCacheBytes;
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

        lock (_decodeCacheSyncRoot)
        {
            _decodeCache.Clear();
            _decodeCacheLru.Clear();
            _decodeCacheBytes = 0;
        }
    }

    private VoiceDecodeCacheEntry? TryGetCacheEntry(string? localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !ArchiveFile.Exists(localPath))
            return null;

        // 解码器只能读真实文件, 归档里的语音先解到临时目录
        localPath = ArchiveFile.GetLocalPath(localPath);

        var fullPath = Path.GetFullPath(localPath);
        try
        {
            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath);
            var length = new FileInfo(fullPath).Length;
            lock (_decodeCacheSyncRoot)
            {
                if (_decodeCache.TryGetValue(fullPath, out var cachedItem) &&
                    cachedItem.Entry.Length == length &&
                    cachedItem.Entry.LastWriteTimeUtc == lastWriteTimeUtc)
                {
                    TouchCacheItem(cachedItem);
                    return cachedItem.Entry.IsPlayable ? cachedItem.Entry : null;
                }
            }

            var decodedEntry = DecodeVoice(fullPath);
            lock (_decodeCacheSyncRoot)
            {
                if (_decodeCache.TryGetValue(fullPath, out var existingItem))
                {
                    if (existingItem.Entry.Length == length &&
                        existingItem.Entry.LastWriteTimeUtc == lastWriteTimeUtc)
                    {
                        TouchCacheItem(existingItem);
                        return existingItem.Entry.IsPlayable ? existingItem.Entry : null;
                    }

                    RemoveCacheItem(fullPath, existingItem);
                }

                if (decodedEntry.Pcm.LongLength > MaxDecodedVoiceBytes)
                {
                    decodedEntry = new VoiceDecodeCacheEntry(
                        [],
                        0,
                        length,
                        lastWriteTimeUtc,
                        IsPlayable: false);
                }

                var node = _decodeCacheLru.AddFirst(fullPath);
                _decodeCache[fullPath] = new VoiceDecodeCacheItem(decodedEntry, node);
                _decodeCacheBytes += decodedEntry.Pcm.LongLength;
                TrimDecodeCache(fullPath);
            }

            return decodedEntry.IsPlayable ? decodedEntry : null;
        }
        catch
        {
            return null;
        }
    }

    private void TouchCacheItem(VoiceDecodeCacheItem item)
    {
        _decodeCacheLru.Remove(item.LruNode);
        _decodeCacheLru.AddFirst(item.LruNode);
    }

    private void TrimDecodeCache(string protectedPath)
    {
        while ((_decodeCacheBytes > MaxDecodeCacheBytes || _decodeCache.Count > MaxDecodeCacheEntries) &&
               _decodeCacheLru.Last is { } lastNode)
        {
            if (string.Equals(lastNode.Value, protectedPath, StringComparison.OrdinalIgnoreCase))
            {
                if (lastNode.Previous is null)
                    break;

                lastNode = lastNode.Previous;
            }

            if (_decodeCache.TryGetValue(lastNode.Value, out var item))
                RemoveCacheItem(lastNode.Value, item);
            else
                _decodeCacheLru.Remove(lastNode);
        }
    }

    private void RemoveCacheItem(string path, VoiceDecodeCacheItem item)
    {
        _decodeCache.Remove(path);
        _decodeCacheLru.Remove(item.LruNode);
        _decodeCacheBytes -= item.Entry.Pcm.LongLength;
    }

    private static VoiceDecodeCacheEntry DecodeVoice(string localPath)
    {
        var temporaryPcmPath = Path.Combine(
            Path.GetTempPath(),
            "qq-database-explorer-voice-" + Guid.NewGuid().ToString("N") + ".pcm");
        try
        {
            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(localPath);
            using var inputStream = new FileStream(
                localPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            var length = inputStream.Length;
            if (length <= 0 ||
                length > MaxEncodedVoiceBytes ||
                !TryValidateSilkDecodeBounds(inputStream, out _))
            {
                return new VoiceDecodeCacheEntry([], 0, length, lastWriteTimeUtc, IsPlayable: false);
            }

            var decoder = new SilkDecoder
            {
                FS_API = VoiceSampleRate,
            };
            decoder.Decode(localPath, temporaryPcmPath);
            var pcmLength = new FileInfo(temporaryPcmPath).Length;
            if (pcmLength <= 0 || pcmLength > MaxDecodedVoiceBytes)
                return new VoiceDecodeCacheEntry([], 0, length, lastWriteTimeUtc, IsPlayable: false);

            var pcm = File.ReadAllBytes(temporaryPcmPath);
            var bytesPerSecond = VoiceSampleRate * sizeof(short);
            var durationMilliseconds = (int)Math.Clamp(
                (pcm.LongLength * 1000 + bytesPerSecond - 1) / bytesPerSecond,
                1,
                int.MaxValue);
            return new VoiceDecodeCacheEntry(
                pcm,
                durationMilliseconds,
                length,
                lastWriteTimeUtc,
                IsPlayable: pcm.Length > 0);
        }
        catch
        {
            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(localPath);
            var length = new FileInfo(localPath).Length;
            return new VoiceDecodeCacheEntry([], 0, length, lastWriteTimeUtc, IsPlayable: false);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPcmPath))
                    File.Delete(temporaryPcmPath);
            }
            catch
            {
            }
        }
    }

    private static bool TryValidateSilkDecodeBounds(Stream stream, out long pcmUpperBoundBytes)
    {
        pcmUpperBoundBytes = 0;
        if (!stream.CanRead || !stream.CanSeek)
            return false;

        stream.Position = 0;
        Span<byte> header = stackalloc byte[10];
        var headerBytesRead = 0;
        while (headerBytesRead < header.Length)
        {
            var read = stream.Read(header[headerBytesRead..]);
            if (read == 0)
                break;
            headerBytesRead += read;
        }

        var silkHeader = "#!SILK_V3"u8;
        int headerLength;
        if (headerBytesRead >= silkHeader.Length + 1 &&
            header[0] == 0x02 &&
            header.Slice(1, silkHeader.Length).SequenceEqual(silkHeader))
        {
            headerLength = silkHeader.Length + 1;
        }
        else if (headerBytesRead >= silkHeader.Length &&
                 header[..silkHeader.Length].SequenceEqual(silkHeader))
        {
            headerLength = silkHeader.Length;
        }
        else
        {
            return false;
        }

        stream.Position = headerLength;
        Span<byte> packetLengthBytes = stackalloc byte[sizeof(short)];
        var packetCount = 0;
        while (stream.Position < stream.Length)
        {
            if (stream.Length - stream.Position < packetLengthBytes.Length)
                return false;

            stream.ReadExactly(packetLengthBytes);
            var packetLength = BinaryPrimitives.ReadInt16LittleEndian(packetLengthBytes);
            if (packetLength == -1)
                // SilkSharp's native file decoder unsafely prefetches two packets before its normal EOF checks.
                return packetCount >= 2 && stream.Position == stream.Length;
            if (packetLength < 0 ||
                packetLength > SilkMaxEncodedPacketBytes ||
                stream.Length - stream.Position < packetLength)
            {
                return false;
            }

            stream.Seek(packetLength, SeekOrigin.Current);
            packetCount++;
            if (pcmUpperBoundBytes > MaxDecodedVoiceBytes - MaxPcmBytesPerSilkPacket)
                return false;
            pcmUpperBoundBytes += MaxPcmBytesPerSilkPacket;
        }

        // Tencent SILK commonly omits the terminator, but still needs two packets for the native prefetch.
        return packetCount >= 2;
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

    private sealed record VoiceDecodeCacheItem(
        VoiceDecodeCacheEntry Entry,
        LinkedListNode<string> LruNode);
}
