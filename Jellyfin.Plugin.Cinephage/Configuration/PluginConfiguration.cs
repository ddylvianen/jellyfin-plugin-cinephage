using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Cinephage.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the Cinephage base URL, e.g. http://cinephage:3000.
    /// </summary>
    public string CinephageUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Cinephage main API key (Settings → System → API keys).
    /// Requests made with it belong to the admin account and are approved automatically.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether movies that already have a file are hidden from the results.
    /// </summary>
    public bool HideAvailable { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of search results shown.
    /// </summary>
    public int MaxResults { get; set; } = 12;
}
