using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Emby.Api;
using Jellyfin.Plugin.Emby.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.Emby.Providers;

/// <summary>
/// Registers official clickable web links to the federated peer Emby server in the Jellyfin UI.
/// Appears natively under the item's 'Links' footer section.
/// </summary>
public class EmbyExternalUrlProvider : IExternalUrlProvider
{
    public string Name => "Emby Web";

    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item is not Movie && item is not Series && item is not Season && item is not Episode)
        {
            return Enumerable.Empty<string>();
        }

        var embyId = EmbyIdKeys.GetItemId(item.ProviderIds) ?? EmbyIdKeys.ExtractItemIdFromPath(item.Path);
        if (string.IsNullOrWhiteSpace(embyId))
        {
            return Enumerable.Empty<string>();
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var baseUrl = !string.IsNullOrWhiteSpace(config.PublicServerUrl)
            ? config.PublicServerUrl
            : config.EmbyServerUrl;

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return Enumerable.Empty<string>();
        }

        var srvId = !string.IsNullOrWhiteSpace(config.EmbyServerId)
            ? config.EmbyServerId
            : "4cc53aaa34c54807874062c289fd8d47";

        var url = $"{baseUrl.TrimEnd('/')}/web/index.html#!/item?id={Uri.EscapeDataString(embyId)}&serverId={Uri.EscapeDataString(srvId)}";
        return new[] { url };
    }
}
