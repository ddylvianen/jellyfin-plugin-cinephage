using System;

namespace Jellyfin.Plugin.Cinephage.Services;

/// <summary>
/// A Cinephage call failed or the plugin is misconfigured.
/// </summary>
public class CinephageException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CinephageException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public CinephageException(string message)
        : base(message)
    {
    }
}
