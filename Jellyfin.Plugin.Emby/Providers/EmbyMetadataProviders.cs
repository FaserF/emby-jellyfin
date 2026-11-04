using Jellyfin.Plugin.Emby.Api;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Emby.Providers;

public class EmbyMovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public EmbyMovieProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    public string Name => "Emby";
    public int Order => 2;

    private EmbyApiClient CreateClient() =>
        new(_httpClientFactory.CreateClient("Emby"), _loggerFactory.CreateLogger<EmbyApiClient>());

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
    {
        var results = new List<RemoteSearchResult>();
        var embyId = EmbyIdKeys.GetItemId(searchInfo.ProviderIds) ?? EmbyIdKeys.ExtractItemIdFromPath(searchInfo.Path);
        if (string.IsNullOrWhiteSpace(embyId))
        {
            return results;
        }

        using var client = CreateClient();
        var (baseUrl, token, _, _) = await client.EnsureAuthenticatedAsync(null, cancellationToken).ConfigureAwait(false);
        var item = await client.GetItemAsync(embyId, null, cancellationToken).ConfigureAwait(false);
        if (item != null)
        {
            var r = new RemoteSearchResult
            {
                Name = item.Name,
                Overview = item.Overview,
                ProductionYear = item.ProductionYear,
                PremiereDate = item.PremiereDate,
                ImageUrl = !string.IsNullOrWhiteSpace(baseUrl) ? client.GetPrimaryImageUrl(baseUrl, item.Id, token) : null,
                SearchProviderName = Name
            };
            EmbyIdKeys.SetMovieId(r, item.Id);
            if (item.GetProviderId("Tmdb") is { } tmdb)
            {
                r.SetProviderId(MetadataProvider.Tmdb, tmdb);
            }

            if (item.GetProviderId("Imdb") is { } imdb)
            {
                r.SetProviderId(MetadataProvider.Imdb, imdb);
            }

            results.Add(r);
        }

        return results;
    }

    public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Movie>();
        var embyId = EmbyIdKeys.GetItemId(info.ProviderIds) ?? EmbyIdKeys.ExtractItemIdFromPath(info.Path);
        if (string.IsNullOrWhiteSpace(embyId))
        {
            return result;
        }

        using var client = CreateClient();
        var (baseUrl, _, _, srvId) = await client.EnsureAuthenticatedAsync(null, cancellationToken).ConfigureAwait(false);
        var item = await client.GetItemAsync(embyId, null, cancellationToken).ConfigureAwait(false);
        if (item == null)
        {
            return result;
        }

        var movie = new Movie
        {
            Name = item.Name,
            OriginalTitle = item.OriginalTitle,
            Overview = EmbyApiClient.FormatRichOverview(item, baseUrl, srvId),
            ProductionYear = item.ProductionYear,
            PremiereDate = item.PremiereDate,
            CommunityRating = item.CommunityRating,
            OfficialRating = item.OfficialRating,
            RunTimeTicks = item.RunTimeTicks
        };

        if (item.Genres != null)
        {
            foreach (var g in item.Genres)
            {
                movie.AddGenre(g);
            }
        }

        EmbyIdKeys.SetMovieId(movie, item.Id);
        if (item.GetProviderId("Tmdb") is { } tmdb)
        {
            movie.SetProviderId(MetadataProvider.Tmdb, tmdb);
        }

        if (item.GetProviderId("Imdb") is { } imdb)
        {
            movie.SetProviderId(MetadataProvider.Imdb, imdb);
        }

        if (item.GetProviderId("Tvdb") is { } tvdb)
        {
            movie.SetProviderId(MetadataProvider.Tvdb, tvdb);
        }

        result.Item = movie;
        result.HasMetadata = true;
        return result;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        CreateClient().HttpClient.GetAsync(url, cancellationToken);
}

public class EmbySeriesProvider : IRemoteMetadataProvider<Series, SeriesInfo>, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public EmbySeriesProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    public string Name => "Emby";
    public int Order => 2;

    private EmbyApiClient CreateClient() =>
        new(_httpClientFactory.CreateClient("Emby"), _loggerFactory.CreateLogger<EmbyApiClient>());

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken) =>
        Task.FromResult<IEnumerable<RemoteSearchResult>>(Array.Empty<RemoteSearchResult>());

    public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Series>();
        var embyId = EmbyIdKeys.GetItemId(info.ProviderIds) ?? EmbyIdKeys.ExtractSeriesIdFromPath(info.Path);
        if (string.IsNullOrWhiteSpace(embyId))
        {
            return result;
        }

        using var client = CreateClient();
        var (baseUrl, _, _, srvId) = await client.EnsureAuthenticatedAsync(null, cancellationToken).ConfigureAwait(false);
        var item = await client.GetItemAsync(embyId, null, cancellationToken).ConfigureAwait(false);
        if (item == null)
        {
            return result;
        }

        var series = new Series
        {
            Name = item.Name,
            OriginalTitle = item.OriginalTitle,
            Overview = EmbyApiClient.FormatRichOverview(item, baseUrl, srvId),
            ProductionYear = item.ProductionYear,
            PremiereDate = item.PremiereDate,
            CommunityRating = item.CommunityRating,
            OfficialRating = item.OfficialRating
        };

        if (item.Genres != null)
        {
            foreach (var g in item.Genres)
            {
                series.AddGenre(g);
            }
        }

        EmbyIdKeys.SetSeriesId(series, item.Id);
        if (item.GetProviderId("Tvdb") is { } tvdb)
        {
            series.SetProviderId(MetadataProvider.Tvdb, tvdb);
        }

        if (item.GetProviderId("Tmdb") is { } tmdb)
        {
            series.SetProviderId(MetadataProvider.Tmdb, tmdb);
        }

        if (item.GetProviderId("Imdb") is { } imdb)
        {
            series.SetProviderId(MetadataProvider.Imdb, imdb);
        }

        result.Item = series;
        result.HasMetadata = true;
        return result;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        CreateClient().HttpClient.GetAsync(url, cancellationToken);
}

public class EmbyEpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public EmbyEpisodeProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    public string Name => "Emby";
    public int Order => 2;

    private EmbyApiClient CreateClient() =>
        new(_httpClientFactory.CreateClient("Emby"), _loggerFactory.CreateLogger<EmbyApiClient>());

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken) =>
        Task.FromResult<IEnumerable<RemoteSearchResult>>(Array.Empty<RemoteSearchResult>());

    public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Episode>();
        var embyId = EmbyIdKeys.GetItemId(info.ProviderIds) ?? EmbyIdKeys.ExtractItemIdFromPath(info.Path);
        if (string.IsNullOrWhiteSpace(embyId))
        {
            return result;
        }

        using var client = CreateClient();
        var item = await client.GetItemAsync(embyId, null, cancellationToken).ConfigureAwait(false);
        if (item == null)
        {
            return result;
        }

        var ep = new Episode
        {
            Name = item.Name,
            Overview = item.Overview,
            IndexNumber = item.IndexNumber ?? info.IndexNumber,
            ParentIndexNumber = item.ParentIndexNumber ?? info.ParentIndexNumber,
            PremiereDate = item.PremiereDate,
            ProductionYear = item.ProductionYear,
            CommunityRating = item.CommunityRating,
            RunTimeTicks = item.RunTimeTicks
        };

        EmbyIdKeys.SetEpisodeId(ep, item.Id);
        if (item.GetProviderId("Tvdb") is { } tvdb)
        {
            ep.SetProviderId(MetadataProvider.Tvdb, tvdb);
        }

        if (item.GetProviderId("Tmdb") is { } tmdb)
        {
            ep.SetProviderId(MetadataProvider.Tmdb, tmdb);
        }

        if (item.GetProviderId("Imdb") is { } imdb)
        {
            ep.SetProviderId(MetadataProvider.Imdb, imdb);
        }

        result.Item = ep;
        result.HasMetadata = true;
        return result;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        CreateClient().HttpClient.GetAsync(url, cancellationToken);
}
