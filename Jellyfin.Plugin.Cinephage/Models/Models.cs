using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Cinephage.Models;

/// <summary>
/// Library/request state of a movie, series or season in Cinephage.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MediaStatus>))]
public enum MediaStatus
{
    /// <summary>Not in Cinephage and not requested.</summary>
    None,

    /// <summary>An active request exists.</summary>
    Requested,

    /// <summary>In the Cinephage library but no file yet.</summary>
    Wanted,

    /// <summary>Some episodes are available (series/seasons only).</summary>
    Partial,

    /// <summary>Everything is available.</summary>
    Available
}

/// <summary>
/// A search result.
/// </summary>
public class MediaResult
{
    /// <summary>Gets or sets the media type: "movie" or "series".</summary>
    public string MediaType { get; set; } = "movie";

    /// <summary>Gets or sets the TMDB id.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the release year (first air year for series).</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the overview.</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets the poster URL.</summary>
    public string? PosterUrl { get; set; }

    /// <summary>Gets or sets the status.</summary>
    public MediaStatus Status { get; set; }
}

/// <summary>
/// A season of a series, with its request/library state.
/// </summary>
public class SeasonInfo
{
    /// <summary>Gets or sets the season number.</summary>
    public int Number { get; set; }

    /// <summary>Gets or sets the season name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of episodes according to TMDB.</summary>
    public int EpisodeCount { get; set; }

    /// <summary>Gets or sets the number of episodes that already have a file.</summary>
    public int AvailableCount { get; set; }

    /// <summary>Gets or sets the air date of the season.</summary>
    public string? AirDate { get; set; }

    /// <summary>Gets or sets the status.</summary>
    public MediaStatus Status { get; set; }

    /// <summary>Gets or sets a value indicating whether the season can be requested.</summary>
    public bool Requestable { get; set; }
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
public class MediaRequestBody
{
    /// <summary>Gets or sets the media type: "movie" (default) or "series".</summary>
    public string? MediaType { get; set; }

    /// <summary>Gets or sets the TMDB id.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the requested season numbers (series only).</summary>
    public List<int>? Seasons { get; set; }

    /// <summary>Gets or sets the title shown in the search result (fallback for the log).</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the year shown in the search result (fallback for the log).</summary>
    public int? Year { get; set; }
}

/// <summary>
/// A request as Cinephage currently has it.
/// </summary>
public class CinephageRequest
{
    /// <summary>Gets or sets the Cinephage request id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the media type: "movie" or "series".</summary>
    public string MediaType { get; set; } = "movie";

    /// <summary>Gets or sets the TMDB id.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the status (pending, approved, awaiting_target, failed, fulfilled, ...).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the request still points at a library item.</summary>
    public bool HasLibraryItem { get; set; }

    /// <summary>Gets or sets the seasons the request covers (whole seasons or via episodes).</summary>
    public HashSet<int> Seasons { get; set; } = [];

    /// <summary>Gets or sets the failure reason, if any.</summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Gets a value indicating whether this request still blocks a new one for the same scope.
    /// An approved request whose library item was deleted is an orphan (Cinephage fails it
    /// on its hourly sweep); failed requests are cleaned up by the plugin before re-requesting.
    /// </summary>
    public bool IsBlocking => Status is "pending" or "awaiting_target" || (Status == "approved" && HasLibraryItem);
}

/// <summary>
/// Library state of a title in Cinephage.
/// </summary>
public class MediaState
{
    /// <summary>Gets or sets a value indicating whether the title is in the Cinephage library.</summary>
    public bool InLibrary { get; set; }

    /// <summary>Gets or sets a value indicating whether the movie has a file.</summary>
    public bool HasFile { get; set; }

    /// <summary>Gets or sets the episode files per season (series only).</summary>
    public Dictionary<int, int> AvailableEpisodes { get; set; } = [];
}

/// <summary>
/// A log entry plus its current state in Cinephage, as shown on the dashboard.
/// </summary>
public class RequestLogView
{
    /// <summary>Gets or sets the logged request (unchanged audit data).</summary>
    public RequestLogEntry Entry { get; set; } = new();

    /// <summary>
    /// Gets or sets the current state: pending, searching, awaiting_target, available, in_library,
    /// failed, declined, expired, cancelled, removed or unknown (Cinephage unreachable).
    /// </summary>
    public string CurrentStatus { get; set; } = "unknown";
}

/// <summary>
/// One request made from Jellyfin. Entries are an append-only audit log and never change.
/// </summary>
public class RequestLogEntry
{
    /// <summary>Gets or sets the time of the request (UTC).</summary>
    public DateTime RequestedAt { get; set; }

    /// <summary>Gets or sets the Jellyfin user id.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the Jellyfin username at the time of the request.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the media type: "movie" or "series". Missing in entries from 1.0.0.0, which were all movies.</summary>
    public string MediaType { get; set; } = "movie";

    /// <summary>Gets or sets the TMDB id.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the release year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the requested seasons (series only).</summary>
    public List<int>? Seasons { get; set; }

    /// <summary>Gets or sets the Cinephage request id; null when it was already requested.</summary>
    public string? CinephageRequestId { get; set; }

    /// <summary>Gets or sets the Cinephage status at the time of the request ("duplicate" when it was already requested).</summary>
    public string? Status { get; set; }
}
