using System;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.CinemaModeDisabler;

/// <summary>
/// Installs <see cref="IndexRewriteMiddleware"/> ahead of Jellyfin's pipeline.
/// </summary>
/// <param name="configurationManager">The server configuration, read for the base URL on each request.</param>
public sealed class IndexRewriteStartupFilter(IServerConfigurationManager configurationManager) : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return application =>
        {
            Func<string> getBaseUrl = () => configurationManager.GetNetworkConfiguration().BaseUrl;
            application.UseMiddleware<IndexRewriteMiddleware>(getBaseUrl);
            next(application);
        };
    }
}
