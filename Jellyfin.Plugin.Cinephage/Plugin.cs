using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Cinephage.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Cinephage;

/// <summary>
/// Lets Jellyfin users request movies in Cinephage and records who requested what.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Cinephage Requests";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("4f7c2a8e-6b1d-4e3a-9c5f-2d8b7a1e0c93");

    /// <inheritdoc />
    public override string Description => "Request movies in Cinephage from Jellyfin search.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            },
            new PluginPageInfo
            {
                Name = "CinephageRequestLog",
                DisplayName = "Cinephage-verzoeken",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.requestsPage.html", GetType().Namespace),
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "movie"
            }
        ];
    }
}
