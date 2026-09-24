using Jellyfin.Plugin.Oops.Transfer;
using Jellyfin.Plugin.Oops.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Oops;

/// <summary>
/// Registers OOPS services with Jellyfin's dependency injection.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<TransferService>();
        serviceCollection.AddTransient<IStartupFilter, OopsStartupFilter>();
    }
}
