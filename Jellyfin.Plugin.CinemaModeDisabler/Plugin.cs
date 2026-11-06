using System;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.CinemaModeDisabler;

/// <summary>
/// The Cinema Mode Disabler plugin.
/// It has no settings: its middleware always turns Cinema Mode off in Jellyfin Web.
/// </summary>
/// <remarks>
/// The empty configuration base is still required: only it records the assembly path and version, which the server dereferences when it creates and lists plugins.
/// </remarks>
/// <param name="applicationPaths">The server's application paths.</param>
/// <param name="xmlSerializer">The server's XML serializer.</param>
public class Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
    : BasePlugin<BasePluginConfiguration>(applicationPaths, xmlSerializer)
{
    /// <inheritdoc />
    public override string Name => "Cinema Mode Disabler";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("47e2e30e-7c54-4321-936f-228ff0de2a6c");

    /// <inheritdoc />
    public override string Description => "Turns off Jellyfin Web's Cinema Mode for every user of every browser.";
}
