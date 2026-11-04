using Jellyfin.Plugin.Emby.Channels;
using Jellyfin.Plugin.Emby.Providers;
using Jellyfin.Plugin.Emby.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Emby;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient("Emby");
        serviceCollection.AddHostedService<EmbyStartupService>();
        serviceCollection.AddSingleton<IChannel, EmbyChannel>();
        serviceCollection.AddSingleton<IMediaSourceProvider, EmbyMediaSourceProvider>();
        serviceCollection.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
        {
            options.Filters.Add<EmbyPlaybackFilter>();
        });
    }
}
