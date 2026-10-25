using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Cinephage.Models;

/// <summary>
/// Library/request state of a movie in Cinephage.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MovieStatus>))]
public enum MovieStatus
{
    /// <summary>Not in Cinephage and not requested.</summary>
    None,

    /// <summary>An active request exists.</summary>
    Requested,

    /// <summary>In the Cinephage library but no file yet.</summary>
    Wanted,

    /// <summary>A file is available.</summary>
    Available
}

/// <summary>
/// A movie search result.
/// </summary>
public class MovieResult
{
    /// <summary>Gets or sets the TMDB id.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the release year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the overview.</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets the poster URL.</summary>
    public string? PosterUrl { get; set; }

    /// <summary>Gets or sets the status.</summary>
    public MovieStatus Status { get; set; }
}

/// <summary>
/// Result of submitting a request.
/// </summary>
public class RequestOutcome
{
    /// <summary>Gets or sets a value indicating whether the request was created.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the Cinephage error code, if any.</summary>
    public string? Code { get; set; }

    /// <summary>Gets or sets the Cinephage request status (pending, approved, ...).</summary>
    public string? RequestStatus { get; set; }

    /// <summary>Gets or sets the Cinephage request id.</summary>
    public string? CinephageRequestId { get; set; }

    /// <summary>Gets or sets the title as stored by Cinephage.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the year as stored by Cinephage.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets a message for the user.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Body of a request submission.
/// </summary>
public class MovieRequestBody
{
    /// <summary>Gets or sets the TMDB id.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the title shown in the search result (fallback for the log).</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the year shown in the search result (fallback for the log).</summary>
    public int? Year { get; set; }
}

/// <summary>
/// One request made from Jellyfin.
/// </summary>
public class RequestLogEntry
{
    /// <summary>Gets or sets the time of the request (UTC).</summary>
    public DateTime RequestedAt { get; set; }

    /// <summary>Gets or sets the Jellyfin user id.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the Jellyfin username at the time of the request.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the TMDB id.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the movie title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the release year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the Cinephage request id; null when the movie was already requested.</summary>
    public string? CinephageRequestId { get; set; }

    /// <summary>Gets or sets the Cinephage status (refreshed when the log is viewed).</summary>
    public string? Status { get; set; }
}
