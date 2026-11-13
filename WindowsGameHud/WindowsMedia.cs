// SPDX-License-Identifier: AGPL-3.0-only
// Added 2026-11-12 for Windows media controls in the island.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;
using WinSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using WinSessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace SapphireGameHud;

internal sealed record MediaReading(string Title, string Artist, BitmapSource? Artwork, bool Playing,
    bool CanPrevious, bool CanToggle, bool CanNext, bool CanSeek,
    double Position, double Duration)
{
    internal static MediaReading Empty { get; } = new("", "", null, false, false, false, false, false, 0, 0);
    internal bool HasTrack => !string.IsNullOrWhiteSpace(Title);
    internal bool Stopped { get; init; }
    internal string? Source { get; init; }
    internal double PlaybackRate { get; init; } = 1;
}

internal sealed class WindowsMedia : IDisposable
{
    private readonly Action<MediaReading> publish;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim reads = new(1);
    private WinSessionManager? manager;
    private WinSession? session;
    private string title = "";
    private string artist = "";
    private BitmapSource? artwork;
    private volatile bool disposed;

    internal WindowsMedia(Action<MediaReading> publish)
    {
        this.publish = publish;
        _ = Initialize();
    }

    private async Task Initialize()
    {
        try
        {
            var source = await WinSessionManager.RequestAsync();
            if (disposed) return;
            manager = source;
            source.CurrentSessionChanged += SessionChanged;
            await Refresh(true);
        }
        catch (Exception error) when (IsProviderError(error))
        {
            if (!disposed) publish(MediaReading.Empty);
        }
    }

    private void SessionChanged(WinSessionManager sender, CurrentSessionChangedEventArgs args) => _ = Refresh(true, rebind: true);
    private void MediaChanged(WinSession sender, MediaPropertiesChangedEventArgs args) => _ = Refresh(true);
    private void PlaybackChanged(WinSession sender, PlaybackInfoChangedEventArgs args) => _ = Refresh(false);
    private void TimelineChanged(WinSession sender, TimelinePropertiesChangedEventArgs args) => _ = Refresh(false);

    private async Task Refresh(bool metadata, bool rebind = false)
    {
        var token = lifetime.Token;
        try
        {
            await reads.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                var next = manager?.GetCurrentSession();
                if (rebind || next is null || !StringComparer.Ordinal.Equals(session?.SourceAppUserModelId, next.SourceAppUserModelId))
                {
                    UnsubscribeSession();
                    session = next;
                    title = artist = "";
                    artwork = null;
                    if (session is { } current)
                    {
                        current.MediaPropertiesChanged += MediaChanged;
                        current.PlaybackInfoChanged += PlaybackChanged;
                        current.TimelinePropertiesChanged += TimelineChanged;
                    }
                    metadata = true;
                }
                if (session is not { } active)
                {
                    if (!disposed) publish(MediaReading.Empty);
                    return;
                }

                if (metadata)
                {
                    var properties = await active.TryGetMediaPropertiesAsync();
                    token.ThrowIfCancellationRequested();
                    title = properties?.Title?.Trim() ?? "";
                    artist = properties?.Artist?.Trim() ?? "";
                    artwork = await LoadArtwork(properties?.Thumbnail, token);
                }

                var playback = active.GetPlaybackInfo();
                var timeline = active.GetTimelineProperties();
                var controls = playback?.Controls;
                var status = playback?.PlaybackStatus;
                var playing = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var rate = playback?.PlaybackRate ?? 1;
                var duration = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds);
                var position = (timeline.Position - timeline.StartTime).TotalSeconds;
                var age = (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalSeconds;
                if (playing && age is >= 0 and < 86_400)
                    position += age * rate;
                if (!disposed)
                    publish(new MediaReading(title, artist, artwork, playing,
                        controls?.IsPreviousEnabled == true,
                        controls?.IsPlayPauseToggleEnabled == true || controls?.IsPlayEnabled == true || controls?.IsPauseEnabled == true,
                        controls?.IsNextEnabled == true,
                        controls?.IsPlaybackPositionEnabled == true && duration > 0,
                        Math.Clamp(position, 0, duration), duration)
                    {
                        Stopped = status is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped
                            or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed,
                        Source = active.SourceAppUserModelId,
                        PlaybackRate = rate
                    });
            }
            finally { reads.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (IsProviderError(error))
        {
            if (!disposed && title.Length == 0) publish(MediaReading.Empty);
        }
    }

    internal async Task<bool> Control(string action, double seconds = 0)
    {
        var active = session;
        if (active is null || disposed) return false;
        try
        {
            var playback = active.GetPlaybackInfo();
            var result = action switch
            {
                "previous" => await active.TrySkipPreviousAsync(),
                "next" => await active.TrySkipNextAsync(),
                "toggle" when playback?.Controls.IsPlayPauseToggleEnabled == true => await active.TryTogglePlayPauseAsync(),
                "toggle" when playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    => await active.TryPauseAsync(),
                "toggle" => await active.TryPlayAsync(),
                "seek" => await active.TryChangePlaybackPositionAsync(
                    active.GetTimelineProperties().StartTime.Ticks + TimeSpan.FromSeconds(seconds).Ticks),
                _ => false
            };
            if (result) await Refresh(action is "next" or "previous");
            return result;
        }
        catch (Exception error) when (IsProviderError(error)) { return false; }
    }

    private static async Task<BitmapSource?> LoadArtwork(IRandomAccessStreamReference? reference, CancellationToken token)
    {
        if (reference is null) return null;
        try
        {
            using var image = await reference.OpenReadAsync();
            if (image.Size is 0 or > 8_000_000) return null;
            using var stream = image.AsStreamForRead();
            using var buffer = new MemoryStream((int)image.Size);
            await stream.CopyToAsync(buffer, token);
            buffer.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = buffer;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 160;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception error) when (IsProviderError(error)) { return null; }
    }

    private static bool IsProviderError(Exception error) =>
        error is COMException or IOException or UnauthorizedAccessException or InvalidOperationException
            or NotSupportedException or ArgumentException or TimeoutException;

    private void UnsubscribeSession()
    {
        if (session is not { } current) return;
        current.MediaPropertiesChanged -= MediaChanged;
        current.PlaybackInfoChanged -= PlaybackChanged;
        current.TimelinePropertiesChanged -= TimelineChanged;
        session = null;
    }

    public void Dispose()
    {
        disposed = true;
        lifetime.Cancel();
        if (manager is { } source) source.CurrentSessionChanged -= SessionChanged;
        UnsubscribeSession();
    }
}
