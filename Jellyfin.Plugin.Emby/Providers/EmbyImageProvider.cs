using Jellyfin.Plugin.Emby.Api;
using Jellyfin.Plugin.Emby.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Emby.Providers;

public class EmbyImageProvider : IRemoteImageProvider, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public EmbyImageProvider(
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    public string Name => "Emby";
    public int Order => 2;

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private EmbyApiClient CreateClient() =>
        new(_httpClientFactory.CreateClient("Emby"), _loggerFactory.CreateLogger<EmbyApiClient>());

    public bool Supports(BaseItem item) => item is Movie or Series or Season or Episode;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        return item switch
        {
            Movie or Series => new[] { ImageType.Primary, ImageType.Backdrop },
            Season or Episode => new[] { ImageType.Primary },
            _ => Array.Empty<ImageType>()
        };
    }

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var list = new List<RemoteImageInfo>();
        if (!Config.FetchArtworkFromEmby)
        {
            return list;
        }

        var embyId = item is Series
            ? EmbyIdKeys.GetItemId(item.ProviderIds) ?? EmbyIdKeys.ExtractSeriesIdFromPath(item.Path)
            : EmbyIdKeys.GetItemId(item.ProviderIds) ?? EmbyIdKeys.ExtractItemIdFromPath(item.Path);

        if (string.IsNullOrWhiteSpace(embyId))
        {
            return list;
        }

        using var client = CreateClient();
        var (baseUrl, token, _, _) = await client.EnsureAuthenticatedAsync(null, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            return list;
        }

        list.Add(new RemoteImageInfo
        {
            ProviderName = Name,
            Type = ImageType.Primary,
            Url = client.GetPrimaryImageUrl(baseUrl, embyId, token)
        });

        if (item is Movie or Series)
        {
            list.Add(new RemoteImageInfo
            {
                ProviderName = Name,
                Type = ImageType.Backdrop,
                Url = client.GetBackdropImageUrl(baseUrl, embyId, token)
            });
        }

        return list;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        CreateClient().HttpClient.GetAsync(url, cancellationToken);
}
