using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using PhoenicxTorBox.Filters;
using PhoenicxTorBox.Library;
using PhoenicxTorBox.Tasks;
using PhoenicxTorBox.TorBox;

namespace PhoenicxTorBox;

/// <summary>Registers the plugin's services with Jellyfin.</summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<TorBoxClient>();
        serviceCollection.AddSingleton<LibrarySync>();
        serviceCollection.AddSingleton<DirectPlayFilter>();
        serviceCollection.AddHostedService<SyncScheduleUpdater>();
        serviceCollection.PostConfigure<MvcOptions>(o => o.Filters.AddService<DirectPlayFilter>());
    }
}
