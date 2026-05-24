using System;
using System.Threading;
using System.Threading.Tasks;

namespace QQDatabaseExplorer.Services;

public interface IVoicePlaybackService
{
    event EventHandler<VoicePlaybackStateChangedEventArgs>? StateChanged;

    string? CurrentPlayingPath { get; }

    int? GetDurationMilliseconds(string? localPath);

    bool CanPlay(string? localPath);

    Task PlayOrStopAsync(string? localPath, CancellationToken cancellationToken = default);

    void Stop();
}

public sealed class VoicePlaybackStateChangedEventArgs(string? currentPlayingPath) : EventArgs
{
    public string? CurrentPlayingPath { get; } = currentPlayingPath;
}
