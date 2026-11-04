using Jellyfin.Plugin.Emby.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Emby;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Emby Federation & Bridge";

    public override Guid Id => Guid.Parse("b4c8e3a2-9f1d-4a7e-8b2c-6d5e4f3a2b1c");

    public override string Description =>
        "Universal Emby-to-Jellyfin Federation Plugin: Native Emby Channel, Automatic Library Federation (.strm & .nfo with TMDb/TVDb/IMDb IDs), Zero-Buffer Direct Play Stream Proxy, Bi-Directional Watch State Sync, Multi-User Account Mapping & Seerr/Suggestarr Request Guard.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
            }
        };
    }
}
