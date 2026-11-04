using Jellyfin.Plugin.Emby.Api;
using Jellyfin.Plugin.Emby.Configuration;
using Jellyfin.Plugin.Emby.Models;
using Jellyfin.Plugin.Emby.Providers;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Emby.Channels;

/// <summary>
/// Universal Native Jellyfin Channel for browsing and streaming remote Emby servers directly.
/// </summary>
public class EmbyChannel : IChannel, IRequiresMediaInfoCallback, IHasCacheKey
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EmbyChannel> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public EmbyChannel(
        IHttpClientFactory httpClientFactory,
        ILogger<EmbyChannel> logger,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public string Name => "Emby";
    public string Description => "Native Emby Federation Channel: Browse Movies, TV Series, Recently Added, and Continue Watching from your federated Emby server with instant Direct Play.";
    public string DataVersion => "1.0.0";
    public string HomePageUrl => Config.GetNormalizedEmbyServerUrl();
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private EmbyApiClient CreateClient() =>
        new(_httpClientFactory.CreateClient("Emby"), _loggerFactory.CreateLogger<EmbyApiClient>());

    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            ContentTypes = new List<ChannelMediaContentType>
            {
                ChannelMediaContentType.Movie,
                ChannelMediaContentType.Episode
            },
            MediaTypes = new List<ChannelMediaType>
            {
                ChannelMediaType.Video
            },
            SupportsContentDownloading = true,
            SupportsSortOrderToggle = true,
            MaxPageSize = 100
        };
    }

    public bool IsEnabledFor(string userId)
    {
        var config = Config;
        if (!config.EnableStreamingChannel)
        {
            return false;
        }

        if (config.EnablePublicFallbackForUnlinkedUsers)
        {
            return true;
        }

        return config.GetAccountForUser(userId) != null;
    }

    public string GetCacheKey(string? userId)
    {
        return $"{DataVersion}_{PluginConfiguration.NormalizeId(userId)}_{DateTime.UtcNow:yyyyMMddHH}";
    }

    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var items = new List<ChannelItemInfo>();
        var config = Config;
        if (!config.EnableStreamingChannel)
        {
            return new ChannelItemResult { Items = items };
        }

        var rawUserId = query.UserId.Equals(Guid.Empty) ? null : query.UserId.ToString("N");
        using var client = CreateClient();
        var (baseUrl, token, _, _) = await client.EnsureAuthenticatedAsync(rawUserId, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            items.Add(new ChannelItemInfo
            {
                Id = "cat_unconfigured",
                Name = "⚙️ Configure Emby Server in Dashboard -> Plugins -> Emby Federation",
                Overview = "Open the Jellyfin Plugin settings to connect your Emby server URL and credentials.",
                Type = ChannelItemType.Folder
            });
            return new ChannelItemResult { Items = items };
        }

        if (string.IsNullOrWhiteSpace(query.FolderId))
        {
            var rootCategories = new (string Id, string Title, string Overview)[]
            {
                ("cat_recent_movies", "🎬 Recently Added Movies", "Latest movies added to the federated Emby server"),
                ("cat_all_movies", "🍿 All Movies (A–Z)", "Complete movie catalog on the federated Emby server"),
                ("cat_series", "📺 TV Series (A–Z)", "Browse all TV series, seasons, and episodes on Emby"),
                ("cat_resume", "▶️ Continue Watching", "Resume in-progress movies and episodes on Emby"),
                ("cat_favorites", "⭐ Favorites", "Your favorite movies and series on Emby")
            };

            foreach (var (id, title, desc) in rootCategories)
            {
                items.Add(new ChannelItemInfo
                {
                    Id = id,
                    Name = title,
                    Overview = desc,
                    Type = ChannelItemType.Folder
                });
            }

            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }

        if (query.FolderId == "cat_recent_movies")
        {
            var movies = await client.GetItemsAsync("Movie", limit: 60, sortBy: "DateCreated,SortName", sortOrder: "Descending", jellyfinUserOrId: rawUserId, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var m in movies)
            {
                items.Add(MapMediaItem(client, baseUrl, token, m));
            }
        }
        else if (query.FolderId == "cat_all_movies")
        {
            var movies = await client.GetItemsAsync("Movie", sortBy: "SortName", sortOrder: "Ascending", jellyfinUserOrId: rawUserId, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var m in movies)
            {
                items.Add(MapMediaItem(client, baseUrl, token, m));
            }
        }
        else if (query.FolderId == "cat_resume")
        {
            var resume = await client.GetItemsAsync("Movie,Episode", limit: 40, filters: "IsResumable", sortBy: "DatePlayed", sortOrder: "Descending", jellyfinUserOrId: rawUserId, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var r in resume)
            {
                items.Add(MapMediaItem(client, baseUrl, token, r));
            }
        }
        else if (query.FolderId == "cat_favorites")
        {
            var favs = await client.GetItemsAsync("Movie,Series", filters: "IsFavorite", sortBy: "SortName", sortOrder: "Ascending", jellyfinUserOrId: rawUserId, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var f in favs)
            {
                if (string.Equals(f.Type, "Series", StringComparison.OrdinalIgnoreCase))
                {
                    items.Add(MapSeriesFolder(client, baseUrl, token, f));
                }
                else
                {
                    items.Add(MapMediaItem(client, baseUrl, token, f));
                }
            }
        }
        else if (query.FolderId == "cat_series")
        {
            var series = await client.GetItemsAsync("Series", sortBy: "SortName", sortOrder: "Ascending", jellyfinUserOrId: rawUserId, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var s in series)
            {
                items.Add(MapSeriesFolder(client, baseUrl, token, s));
            }
        }
        else if (query.FolderId.StartsWith("series_", StringComparison.OrdinalIgnoreCase))
        {
            var seriesId = query.FolderId["series_".Length..];
            var episodes = await client.GetItemsAsync("Episode", parentId: seriesId, sortBy: "ParentIndexNumber,IndexNumber", sortOrder: "Ascending", jellyfinUserOrId: rawUserId, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var ep in episodes)
            {
                items.Add(MapMediaItem(client, baseUrl, token, ep));
            }
        }

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    private static ChannelItemInfo MapSeriesFolder(EmbyApiClient client, string baseUrl, string token, EmbyItemDto s)
    {
        return new ChannelItemInfo
        {
            Id = $"series_{s.Id}",
            Name = s.ProductionYear.HasValue ? $"{s.Name} ({s.ProductionYear})" : s.Name,
            Overview = s.Overview,
            Type = ChannelItemType.Folder,
            ImageUrl = client.GetPrimaryImageUrl(baseUrl, s.Id, token),
            ProductionYear = s.ProductionYear,
            CommunityRating = s.CommunityRating
        };
    }

    private static ChannelItemInfo MapMediaItem(EmbyApiClient client, string baseUrl, string token, EmbyItemDto item)
    {
        var isEpisode = string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase);
        var info = new ChannelItemInfo
        {
            Id = $"emby_{item.Id}",
            Name = isEpisode && item.ParentIndexNumber.HasValue && item.IndexNumber.HasValue
                ? $"{item.SeriesName} - S{item.ParentIndexNumber:D2}E{item.IndexNumber:D2} - {item.Name}"
                : item.Name,
            SeriesName = item.SeriesName,
            Overview = item.Overview,
            Type = ChannelItemType.Media,
            ContentType = isEpisode ? ChannelMediaContentType.Episode : ChannelMediaContentType.Movie,
            MediaType = ChannelMediaType.Video,
            ImageUrl = client.GetPrimaryImageUrl(baseUrl, item.Id, token),
            ProductionYear = item.ProductionYear,
            PremiereDate = item.PremiereDate,
            CommunityRating = item.CommunityRating,
            OfficialRating = item.OfficialRating,
            RunTimeTicks = item.RunTimeTicks,
            IndexNumber = item.IndexNumber,
            ParentIndexNumber = item.ParentIndexNumber
        };

        EmbyIdKeys.SetItemId(info, item.Id);
        if (item.GetProviderId("Tmdb") is { } tmdb)
        {
            info.SetProviderId(MetadataProvider.Tmdb, tmdb);
        }

        if (item.GetProviderId("Imdb") is { } imdb)
        {
            info.SetProviderId(MetadataProvider.Imdb, imdb);
        }

        if (item.GetProviderId("Tvdb") is { } tvdb)
        {
            info.SetProviderId(MetadataProvider.Tvdb, tvdb);
        }

        return info;
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        var embyId = id.StartsWith("emby_", StringComparison.OrdinalIgnoreCase) ? id["emby_".Length..] : id;
        using var client = CreateClient();
        var (baseUrl, token, _, _) = await client.EnsureAuthenticatedAsync(null, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            return Enumerable.Empty<MediaSourceInfo>();
        }

        var streamUrl = Config.UseDirectEmbyStreamUrls
            ? client.GetDirectStreamUrl(baseUrl, embyId, token)
            : $"http://127.0.0.1:8096/api/embyPlugin/stream/{embyId}/stream.mkv";

        return new[]
        {
            new MediaSourceInfo
            {
                Id = embyId,
                Name = $"Emby Direct Play ({embyId})",
                Path = streamUrl,
                Protocol = MediaProtocol.Http,
                Container = "mkv",
                IsRemote = true,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                SupportsProbing = true
            }
        };
    }

    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken) =>
        Task.FromResult(new DynamicImageResponse { HasImage = false });

    public IEnumerable<ImageType> GetSupportedChannelImages() => new[] { ImageType.Thumb, ImageType.Primary };
}
