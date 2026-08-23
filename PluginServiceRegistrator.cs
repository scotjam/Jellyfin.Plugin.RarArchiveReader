using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.RarArchiveReader
{
    /// <summary>
    /// Registers plugin services with the Jellyfin DI container.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            // Makes /RarStream URLs in PlaybackInfo responses client-agnostic (see RarStreamUrlRewriter.cs).
            serviceCollection.AddSingleton<IStartupFilter, RarStreamUrlStartupFilter>();
        }
    }
}
