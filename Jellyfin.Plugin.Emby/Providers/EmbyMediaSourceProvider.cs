using System.Collections.Concurrent;
using Jellyfin.Plugin.Emby.Api;
using Jellyfin.Plugin.Emby.Configuration;
using Jellyfin.Plugin.Emby.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Emby.Providers;

/// <summary>
/// Dynamic MediaSource provider for federated Emby .strm and library items.
/// Supplies pre-populated Video, Audio, and Subtitle MediaStreams directly from the peer Emby API
/// so clients (Web, Android TV, Fire TV, Mobile) start playback immediately without FFmpeg probing delays.
/// </summary>
public class EmbyMediaSourceProvider : IMediaSourceProvider, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EmbyMediaSourceProvider> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public int Order => -100;

    private static readonly ConcurrentDictionary<string, (MediaSourceInfo Source, DateTime CachedAt)> _sourceCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(20);

    public EmbyMediaSourceProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<EmbyMediaSourceProvider> logger,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is not Movie && item is not Episode)
        {
            return Enumerable.Empty<MediaSourceInfo>();
        }

        var embyId = EmbyIdKeys.GetItemId(item.ProviderIds) ?? EmbyIdKeys.ExtractItemIdFromPath(item.Path);
        if (string.IsNullOrWhiteSpace(embyId))
        {
            return Enumerable.Empty<MediaSourceInfo>();
        }

        var cacheKey = $"emby_{embyId}";
        if (_sourceCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.CachedAt < CacheTtl)
        {
            return new[] { CloneMediaSource(cached.Source, item.Id.ToString("N")) };
        }

        try
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            using var client = new EmbyApiClient(
                _httpClientFactory.CreateClient("Emby"),
                _loggerFactory.CreateLogger<EmbyApiClient>());

            var (baseUrl, token, _, _) = await client.EnsureAuthenticatedAsync(null, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
            {
                return Enumerable.Empty<MediaSourceInfo>();
            }

            var embyItem = await client.GetItemAsync(embyId, null, cancellationToken).ConfigureAwait(false);
            var embySource = embyItem?.MediaSources?.FirstOrDefault();
            var container = !string.IsNullOrWhiteSpace(embySource?.Container) ? embySource!.Container! : "mkv";

            var streamUrl = config.UseDirectEmbyStreamUrls
                ? client.GetDirectStreamUrl(baseUrl, embyId, token)
                : $"http://127.0.0.1:8096/api/embyPlugin/stream/{embyId}/stream.{container}";

            var streams = new List<MediaStream>();
            int? defaultAudioIdx = null;
            if (embySource?.MediaStreams != null && embySource.MediaStreams.Count > 0)
            {
                var idx = 0;
                foreach (var es in embySource.MediaStreams)
                {
                    if (string.Equals(es.Type, "Video", StringComparison.OrdinalIgnoreCase))
                    {
                        streams.Add(new MediaStream
                        {
                            Type = MediaStreamType.Video,
                            Index = idx++,
                            Codec = !string.IsNullOrWhiteSpace(es.Codec) ? es.Codec : "h264",
                            Width = es.Width ?? 1920,
                            Height = es.Height ?? 1080,
                            BitRate = es.BitRate ?? 8000000,
                            Language = es.Language,
                            Title = es.DisplayTitle ?? es.Title ?? "Emby Video",
                            IsDefault = true
                        });
                    }
                    else if (string.Equals(es.Type, "Audio", StringComparison.OrdinalIgnoreCase))
                    {
                        var curIdx = idx++;
                        if (defaultAudioIdx == null || es.IsDefault)
                        {
                            defaultAudioIdx = curIdx;
                        }

                        var channelDesc = es.Channels switch
                        {
                            1 => "Mono",
                            2 => "Stereo 2.0",
                            6 => "5.1 Surround",
                            8 => "7.1 Surround",
                            _ => es.Channels.HasValue ? $"{es.Channels}ch" : ""
                        };
                        var audioTitle = es.DisplayTitle ?? es.Title;
                        if (string.IsNullOrWhiteSpace(audioTitle) && !string.IsNullOrWhiteSpace(es.Language))
                        {
                            audioTitle = $"{es.Language.ToUpperInvariant()} ({es.Codec?.ToUpperInvariant() ?? "Audio"} {channelDesc})".Trim();
                        }

                        streams.Add(new MediaStream
                        {
                            Type = MediaStreamType.Audio,
                            Index = curIdx,
                            Codec = !string.IsNullOrWhiteSpace(es.Codec) ? es.Codec : "aac",
                            Channels = es.Channels ?? 2,
                            SampleRate = es.SampleRate ?? 48000,
                            BitRate = es.BitRate ?? 192000,
                            Language = es.Language ?? "und",
                            Title = audioTitle ?? "Audio",
                            IsDefault = es.IsDefault
                        });
                    }
                    else if (string.Equals(es.Type, "Subtitle", StringComparison.OrdinalIgnoreCase))
                    {
                        var isExt = es.IsExternal;
                        var subUrl = isExt && embySource != null
                            ? $"{baseUrl.TrimEnd('/')}/emby/Videos/{embyId}/{embySource.Id}/Subtitles/{es.Index}/Stream.{es.Codec ?? "srt"}?api_key={token}"
                            : null;

                        streams.Add(new MediaStream
                        {
                            Type = MediaStreamType.Subtitle,
                            Index = idx++,
                            Codec = !string.IsNullOrWhiteSpace(es.Codec) ? es.Codec : "subrip",
                            Language = es.Language ?? "und",
                            Title = es.DisplayTitle ?? es.Title ?? (es.Language != null ? $"{es.Language.ToUpperInvariant()} (Subtitle)" : "Subtitle"),
                            IsDefault = es.IsDefault,
                            IsForced = es.IsForced,
                            IsExternal = isExt,
                            DeliveryMethod = isExt ? MediaBrowser.Model.Dlna.SubtitleDeliveryMethod.External : MediaBrowser.Model.Dlna.SubtitleDeliveryMethod.Embed,
                            DeliveryUrl = subUrl
                        });
                    }
                }
            }

            if (streams.Count == 0)
            {
                streams.Add(new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    Codec = "h264",
                    Width = 1920,
                    Height = 1080,
                    IsDefault = true
                });
                streams.Add(new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = 1,
                    Codec = "aac",
                    Channels = 2,
                    IsDefault = true
                });
                defaultAudioIdx = 1;
            }

            var source = new MediaSourceInfo
            {
                Id = item.Id.ToString("N"),
                Name = $"{item.Name} (Emby Direct Play)",
                Path = streamUrl,
                Protocol = MediaProtocol.Http,
                Container = container,
                Size = embySource?.Size,
                Bitrate = embySource?.Bitrate,
                IsRemote = true,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                SupportsProbing = false,
                DefaultAudioStreamIndex = defaultAudioIdx,
                RunTimeTicks = embySource?.RunTimeTicks ?? embyItem?.RunTimeTicks ?? item.RunTimeTicks,
                MediaStreams = streams
            };

            _sourceCache[cacheKey] = (source, DateTime.UtcNow);
            return new[] { CloneMediaSource(source, item.Id.ToString("N")) };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EmbyMediaSourceProvider failed for {EmbyId}", embyId);
            return Enumerable.Empty<MediaSourceInfo>();
        }
    }

    private static MediaSourceInfo CloneMediaSource(MediaSourceInfo src, string id)
    {
        return new MediaSourceInfo
        {
            Id = id,
            Name = src.Name,
            Path = src.Path,
            Protocol = src.Protocol,
            Container = src.Container,
            Size = src.Size,
            Bitrate = src.Bitrate,
            IsRemote = src.IsRemote,
            SupportsDirectPlay = src.SupportsDirectPlay,
            SupportsDirectStream = src.SupportsDirectStream,
            SupportsTranscoding = src.SupportsTranscoding,
            SupportsProbing = src.SupportsProbing,
            DefaultAudioStreamIndex = src.DefaultAudioStreamIndex,
            DefaultSubtitleStreamIndex = src.DefaultSubtitleStreamIndex,
            RunTimeTicks = src.RunTimeTicks,
            MediaStreams = src.MediaStreams?.Select(s => new MediaStream
            {
                Type = s.Type,
                Index = s.Index,
                Codec = s.Codec,
                Width = s.Width,
                Height = s.Height,
                BitRate = s.BitRate,
                Channels = s.Channels,
                SampleRate = s.SampleRate,
                Language = s.Language,
                Title = s.Title,
                IsDefault = s.IsDefault,
                IsForced = s.IsForced
            }).ToList()
        };
    }

    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken) =>
        Task.FromResult<ILiveStream>(null!);
}
