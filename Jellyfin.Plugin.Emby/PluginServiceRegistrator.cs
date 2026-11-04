using Jellyfin.Plugin.Emby.Providers;
using Jellyfin.Plugin.Emby.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Emby;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient("Emby");
        serviceCollection.AddHostedService<EmbyStartupService>();
        serviceCollection.AddSingleton<IMediaSourceProvider, EmbyMediaSourceProvider>();
        serviceCollection.AddSingleton<IExternalUrlProvider, EmbyExternalUrlProvider>();
    }
}
