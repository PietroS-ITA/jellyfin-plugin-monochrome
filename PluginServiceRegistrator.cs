using Jellyfin.Plugin.Monochrome.Api;
using Jellyfin.Plugin.Monochrome.Channels;
using Jellyfin.Plugin.Monochrome.Providers;
using Jellyfin.Plugin.Monochrome.Search;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Monochrome;

/// <summary>
/// Registers plugin services into Jellyfin dependency injection.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient();
        serviceCollection.AddSingleton<MonochromeApiClient>();
        serviceCollection.AddSingleton<IChannel, MonochromeChannel>();
        serviceCollection.AddSingleton<MonochromeSearchProvider>();
        serviceCollection.AddSingleton<ISearchProvider>(sp => sp.GetRequiredService<MonochromeSearchProvider>());
        serviceCollection.AddSingleton<IExternalSearchProvider>(sp => sp.GetRequiredService<MonochromeSearchProvider>());
        serviceCollection.AddSingleton<IMediaSourceProvider, MonochromeMediaSourceProvider>();
    }
}

