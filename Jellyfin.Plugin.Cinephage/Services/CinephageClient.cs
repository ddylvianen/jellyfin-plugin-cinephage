using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Cinephage.Configuration;
using Jellyfin.Plugin.Cinephage.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Cinephage.Services;

/// <summary>
/// Talks to the Cinephage API with the configured API key.
/// State (library, requests) always comes from Cinephage; the plugin's own log is only an audit trail.
/// </summary>
public sealed class CinephageClient : IDisposable
{
    private const string PosterBaseUrl = "https://image.tmdb.org/t/p/w342";
    private const int MaxParallelLookups = 8;
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient _http;
    private readonly ILogger<CinephageClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CinephageClient"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{CinephageClient}"/> interface.</param>
    public CinephageClient(ILogger<CinephageClient> logger)
    {
        _logger = logger;
        // Timeouts are set per call (see SendAsync): creating a request can take much longer than a lookup.
        _http = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? throw new CinephageException("Plugin is not initialized.");

    /// <summary>
    /// Searches movies and series (TMDB) through Cinephage and adds the library/request status.
    /// </summary>
    /// <param name="query">The search text.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Movies first, then series.</returns>
    public async Task<IReadOnlyList<MediaResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var config = Config;
        var max = Math.Clamp(config.MaxResults, 1, 40);

        var movieTask = SearchTypeAsync("movie", query, max, cancellationToken);
        var seriesTask = SearchTypeAsync("series", query, max, cancellationToken);
        var requestsTask = TryGetRequestsAsync(cancellationToken);
        var all = (await movieTask.ConfigureAwait(false)).Concat(await seriesTask.ConfigureAwait(false)).ToList();
        var requests = await requestsTask.ConfigureAwait(false);

        var states = await GetMediaStatesAsync(all.Select(m => (m.MediaType, m.TmdbId)), cancellationToken).ConfigureAwait(false);
        foreach (var item in all)
        {
            if (!states.TryGetValue((item.MediaType, item.TmdbId), out var state))
            {
                continue;
            }

            var blocking = requests.Any(r => r.MediaType == item.MediaType && r.TmdbId == item.TmdbId && r.IsBlocking);
            item.Status = item.MediaType == "series"
                ? blocking ? MediaStatus.Requested
                    : state.AvailableEpisodes.Count > 0 ? MediaStatus.Partial
                    : state.InLibrary ? MediaStatus.Wanted
                    : MediaStatus.None
                : state.HasFile ? MediaStatus.Available
                    : blocking ? MediaStatus.Requested
                    : state.InLibrary ? MediaStatus.Wanted
                    : MediaStatus.None;
        }

        // Series stay visible when (partly) available: more seasons may still be requested.
        var visible = all.Where(m => !(config.HideAvailable && m.MediaType == "movie" && m.Status == MediaStatus.Available));
        return visible.Where(m => m.MediaType == "movie").Take(max)
            .Concat(visible.Where(m => m.MediaType == "series").Take(max))
            .ToList();
    }

    /// <summary>
    /// Gets the seasons of a series with their request/library state.
    /// </summary>
    /// <param name="tmdbId">The TMDB id of the series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The seasons, specials excluded (Cinephage only accepts season 1 and up).</returns>
    public async Task<IReadOnlyList<SeasonInfo>> GetSeasonsAsync(int tmdbId, CancellationToken cancellationToken)
    {
        var detailsTask = GetJsonAsync("/api/tmdb/tv/" + tmdbId.ToString(CultureInfo.InvariantCulture), cancellationToken);
        var stateTask = GetMediaStateAsync("series", tmdbId, cancellationToken);
        var requestsTask = TryGetRequestsAsync(cancellationToken);
        using var details = await detailsTask.ConfigureAwait(false);
        var state = await stateTask.ConfigureAwait(false);

        // Only requests that are still in progress lock a season; failed or orphaned ones may be re-requested.
        var requested = (await requestsTask.ConfigureAwait(false))
            .Where(r => r.MediaType == "series" && r.TmdbId == tmdbId && r.IsBlocking)
            .SelectMany(r => r.Seasons)
            .ToHashSet();

        var result = new List<SeasonInfo>();
        if (!details.RootElement.TryGetProperty("seasons", out var tmdbSeasons) || tmdbSeasons.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var season in tmdbSeasons.EnumerateArray())
        {
            if (!season.TryGetProperty("season_number", out var numberElement) || !numberElement.TryGetInt32(out var number) || number <= 0)
            {
                continue;
            }

            var episodeCount = season.TryGetProperty("episode_count", out var ec) && ec.TryGetInt32(out var count) ? count : 0;
            var availableCount = state.AvailableEpisodes.GetValueOrDefault(number);
            var status = requested.Contains(number) ? MediaStatus.Requested
                : episodeCount > 0 && availableCount >= episodeCount ? MediaStatus.Available
                : availableCount > 0 ? MediaStatus.Partial
                : MediaStatus.None;

            result.Add(new SeasonInfo
            {
                Number = number,
                Name = GetString(season, "name") ?? $"Seizoen {number}",
                EpisodeCount = episodeCount,
                AvailableCount = availableCount,
                AirDate = GetString(season, "air_date"),
                Status = status,
                // Cinephage rejects seasons without episodes and overlaps with active requests.
                Requestable = episodeCount > 0 && status is MediaStatus.None or MediaStatus.Partial
            });
        }

        return result.OrderBy(s => s.Number).ToList();
    }

    /// <summary>
    /// Submits a request in Cinephage. Stale requests for the same title (failed, or approved
    /// while the library item was deleted) are removed first, because Cinephage would otherwise
    /// reject the new request as a duplicate.
    /// </summary>
    /// <param name="mediaType">"movie" or "series".</param>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="seasons">The seasons to request (series only).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async Task<RequestOutcome> RequestAsync(string mediaType, int tmdbId, IReadOnlyList<int>? seasons, CancellationToken cancellationToken)
    {
        await RemoveStaleRequestsAsync(mediaType, tmdbId, seasons, cancellationToken).ConfigureAwait(false);

        var body = mediaType == "series"
            ? JsonSerializer.Serialize(new { mediaType, tmdbId, seasons })
            : JsonSerializer.Serialize(new { mediaType, tmdbId });
        var noun = mediaType == "series" ? "serie" : "film";

        // Cinephage auto-approves admin requests and adds the title to the library in the same
        // call, which can be slow. On a timeout, check whether the request landed anyway.
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(HttpMethod.Post, "/api/requests", body, cancellationToken, RequestTimeout).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Cinephage did not answer the {Type} request for TMDB {TmdbId} within {Seconds}s; checking its status", mediaType, tmdbId, RequestTimeout.TotalSeconds);
            var found = await FindRecentRequestAsync(mediaType, tmdbId, cancellationToken).ConfigureAwait(false);
            return new RequestOutcome
            {
                Success = found is not null,
                Code = found is null ? "timeout" : null,
                CinephageRequestId = found?.Id,
                RequestStatus = found?.Status,
                Message = found is not null
                    ? $"Aangevraagd! De {noun} wordt gezocht."
                    : "Cinephage reageert niet op tijd. Controleer later of het verzoek is aangekomen."
            };
        }

        using var responseScope = response;
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (response.IsSuccessStatusCode && root.TryGetProperty("request", out var request))
            {
                return new RequestOutcome
                {
                    Success = true,
                    CinephageRequestId = GetString(request, "id"),
                    RequestStatus = GetString(request, "status"),
                    Title = GetString(request, "title"),
                    Year = request.TryGetProperty("year", out var y) && y.TryGetInt32(out var year) ? year : null,
                    Message = $"Aangevraagd! De {noun} wordt gezocht."
                };
            }

            var code = GetString(root, "code");
            return new RequestOutcome
            {
                Success = false,
                Code = code,
                Message = code switch
                {
                    "duplicate_request" when mediaType == "series" => "Een of meer van deze seizoenen zijn al aangevraagd.",
                    "duplicate_request" => "Deze film is al aangevraagd.",
                    "already_available" => $"Deze {noun} is al beschikbaar.",
                    "already_in_library" => $"Deze {noun} staat al in Cinephage en wordt gezocht.",
                    "blocked_media" => $"Deze {noun} is geblokkeerd in Cinephage.",
                    "cooldown" => $"Deze {noun} is kort geleden afgewezen en kan nog even niet opnieuw worden aangevraagd.",
                    "invalid_scope" => "Kies minstens één bestaand seizoen.",
                    _ => GetString(root, "error") ?? "Aanvragen mislukt."
                }
            };
        }
        catch (JsonException)
        {
            return new RequestOutcome { Success = false, Message = $"Aanvragen mislukt (HTTP {(int)response.StatusCode})." };
        }
    }

    /// <summary>
    /// Works out the current state in Cinephage of every logged request. The log entries themselves are not changed.
    /// </summary>
    /// <param name="entries">The audit log entries.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The entries with their current state, in the same order.</returns>
    public async Task<IReadOnlyList<RequestLogView>> GetCurrentStatesAsync(IReadOnlyList<RequestLogEntry> entries, CancellationToken cancellationToken)
    {
        var requests = (await GetRequestsAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var states = await GetMediaStatesAsync(entries.Select(e => (e.MediaType, e.TmdbId)), cancellationToken).ConfigureAwait(false);

        return entries.Select(entry =>
        {
            var state = states.GetValueOrDefault((entry.MediaType, entry.TmdbId));
            var request = entry.CinephageRequestId is null ? null : requests.GetValueOrDefault(entry.CinephageRequestId);
            return new RequestLogView { Entry = entry, CurrentStatus = CurrentStatus(entry, request, state) };
        }).ToList();
    }

    /// <summary>
    /// Checks that Cinephage is reachable and the API key works.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A short status text.</returns>
    public async Task<string> TestConnectionAsync(CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync("/api/requests?take=1", cancellationToken).ConfigureAwait(false);
        return "Verbonden met Cinephage, API-key werkt.";
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "…";

    private static string CurrentStatus(RequestLogEntry entry, CinephageRequest? request, MediaState? state)
    {
        if (state is null)
        {
            return "unknown";
        }

        // For series the episode totals are not known here, so "available" only comes from
        // Cinephage marking the request fulfilled, or (without a request) from files in every requested season.
        var available = entry.MediaType == "series"
            ? request is null && entry.Seasons is { Count: > 0 } && entry.Seasons.All(state.AvailableEpisodes.ContainsKey)
            : state.HasFile;

        if (request is not null)
        {
            switch (request.Status)
            {
                case "pending":
                case "awaiting_target":
                    return request.Status;
                case "approved":
                    return !request.HasLibraryItem || !state.InLibrary ? "removed" : available ? "available" : "searching";
                case "fulfilled":
                    return state.InLibrary ? "available" : "removed";
                case "failed":
                    return !state.InLibrary || request.FailureReason == "Library item was deleted" ? "removed" : "failed";
                case "declined":
                    return state.InLibrary ? available ? "available" : "in_library" : "declined";
                default:
                    // expired, cancelled (e.g. replaced by a re-request): what matters is whether the
                    // title is in the library now.
                    return state.InLibrary ? available ? "available" : "in_library" : "removed";
            }
        }

        // No (longer a) Cinephage request: "already requested" entries, or the request was removed in Cinephage.
        return !state.InLibrary ? "removed" : available ? "available" : "in_library";
    }

    private async Task RemoveStaleRequestsAsync(string mediaType, int tmdbId, IReadOnlyList<int>? seasons, CancellationToken cancellationToken)
    {
        var stale = (await TryGetRequestsAsync(cancellationToken).ConfigureAwait(false))
            .Where(r => r.MediaType == mediaType && r.TmdbId == tmdbId && !r.IsBlocking && r.Status is "failed" or "approved")
            // For series only clear what overlaps the new selection; other seasons keep their history.
            .Where(r => mediaType != "series" || seasons is null || r.Seasons.Count == 0 || r.Seasons.Overlaps(seasons))
            .ToList();

        foreach (var request in stale)
        {
            // Cinephage removes a failed request and cancels an approved one.
            using var response = await SendAsync(HttpMethod.Delete, "/api/requests/" + Uri.EscapeDataString(request.Id), null, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Removed stale Cinephage {Type} request {Id} ({Status}) for TMDB {TmdbId} before re-requesting: HTTP {Code}",
                mediaType,
                request.Id,
                request.Status,
                tmdbId,
                (int)response.StatusCode);
        }
    }

    private async Task<List<CinephageRequest>> TryGetRequestsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await GetRequestsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CinephageException or HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not list Cinephage requests");
            return [];
        }
    }

    private async Task<List<CinephageRequest>> GetRequestsAsync(CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync("/api/requests?take=1000", cancellationToken).ConfigureAwait(false);
        var result = new List<CinephageRequest>();
        if (!doc.RootElement.TryGetProperty("requests", out var requests) || requests.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var request in requests.EnumerateArray())
        {
            var id = GetString(request, "id");
            if (id is null || !request.TryGetProperty("tmdbId", out var t) || !t.TryGetInt32(out var tmdbId))
            {
                continue;
            }

            var item = new CinephageRequest
            {
                Id = id,
                MediaType = GetString(request, "mediaType") ?? "movie",
                TmdbId = tmdbId,
                Status = GetString(request, "status") ?? string.Empty,
                HasLibraryItem = GetString(request, "movieId") is not null || GetString(request, "seriesId") is not null,
                FailureReason = GetString(request, "failureReason")
            };

            if (request.TryGetProperty("seasons", out var seasons) && seasons.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in seasons.EnumerateArray())
                {
                    if (s.TryGetInt32(out var n))
                    {
                        item.Seasons.Add(n);
                    }
                }
            }

            if (request.TryGetProperty("episodes", out var episodes) && episodes.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in episodes.EnumerateArray())
                {
                    if (e.TryGetProperty("seasonNumber", out var sn) && sn.TryGetInt32(out var n))
                    {
                        item.Seasons.Add(n);
                    }
                }
            }

            result.Add(item);
        }

        return result;
    }

    private async Task<Dictionary<(string, int), MediaState>> GetMediaStatesAsync(IEnumerable<(string MediaType, int TmdbId)> keys, CancellationToken cancellationToken)
    {
        var result = new Dictionary<(string, int), MediaState>();
        using var throttle = new SemaphoreSlim(MaxParallelLookups);
        await Task.WhenAll(keys.Distinct().Select(async key =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var state = await GetMediaStateAsync(key.MediaType, key.TmdbId, cancellationToken).ConfigureAwait(false);
                lock (result)
                {
                    result[key] = state;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Could not get Cinephage state for {Type} {TmdbId}", key.MediaType, key.TmdbId);
            }
            finally
            {
                throttle.Release();
            }
        })).ConfigureAwait(false);
        return result;
    }

    private async Task<MediaState> GetMediaStateAsync(string mediaType, int tmdbId, CancellationToken cancellationToken)
    {
        var path = $"/api/requests/media-status?mediaType={mediaType}&tmdbId=" + tmdbId.ToString(CultureInfo.InvariantCulture);
        using var doc = await GetJsonAsync(path, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        var state = new MediaState
        {
            InLibrary = root.TryGetProperty("inLibrary", out var inLibrary) && inLibrary.ValueKind == JsonValueKind.True,
            HasFile = root.TryGetProperty("hasFile", out var hasFile) && hasFile.ValueKind == JsonValueKind.True
        };

        if (root.TryGetProperty("availableEpisodes", out var availableEpisodes) && availableEpisodes.ValueKind == JsonValueKind.Array)
        {
            // Entries look like "1x5" (season x episode).
            foreach (var entry in availableEpisodes.EnumerateArray())
            {
                var text = entry.GetString();
                var x = text?.IndexOf('x', StringComparison.Ordinal) ?? -1;
                if (x > 0 && int.TryParse(text.AsSpan(0, x), NumberStyles.Integer, CultureInfo.InvariantCulture, out var season))
                {
                    state.AvailableEpisodes[season] = state.AvailableEpisodes.GetValueOrDefault(season) + 1;
                }
            }
        }

        return state;
    }

    private async Task<List<MediaResult>> SearchTypeAsync(string mediaType, string query, int max, CancellationToken cancellationToken)
    {
        // Cinephage/TMDB call series "tv".
        var type = mediaType == "series" ? "tv" : "movie";
        var path = $"/api/discover/search?type={type}&query=" + Uri.EscapeDataString(query);

        using var doc = await GetJsonAsync(path, cancellationToken).ConfigureAwait(false);
        var items = new List<MediaResult>();
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var item in results.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var tmdbId))
            {
                continue;
            }

            var date = mediaType == "series" ? GetString(item, "first_air_date") : GetString(item, "release_date");
            var posterPath = GetString(item, "poster_path");
            items.Add(new MediaResult
            {
                MediaType = mediaType,
                TmdbId = tmdbId,
                Title = (mediaType == "series"
                    ? GetString(item, "name") ?? GetString(item, "original_name")
                    : GetString(item, "title") ?? GetString(item, "original_title")) ?? "?",
                Year = date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ? year : null,
                Overview = GetString(item, "overview"),
                PosterUrl = string.IsNullOrEmpty(posterPath) ? null : PosterBaseUrl + posterPath
            });

            // Fetch a few extra so hiding available movies still fills the list.
            if (items.Count >= max * 2)
            {
                break;
            }
        }

        return items;
    }

    private async Task<(string Id, string? Status)?> FindRecentRequestAsync(string mediaType, int tmdbId, CancellationToken cancellationToken)
    {
        // Newest first; a request created in the last minutes is near the top.
        using var doc = await GetJsonAsync($"/api/requests?mediaType={mediaType}&take=50", cancellationToken).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("requests", out var requests) || requests.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var since = DateTime.UtcNow - RequestTimeout - TimeSpan.FromMinutes(1);
        foreach (var request in requests.EnumerateArray())
        {
            var matches = request.TryGetProperty("tmdbId", out var t) && t.TryGetInt32(out var id) && id == tmdbId;
            var created = GetString(request, "createdAt");
            var recent = DateTime.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var createdAt) && createdAt >= since;
            var requestId = GetString(request, "id");
            if (matches && recent && requestId is not null)
            {
                return (requestId, GetString(request, "status"));
            }
        }

        return null;
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new CinephageException($"Cinephage GET {path} returned HTTP {(int)response.StatusCode}: {Truncate(text)}");
        }

        return JsonDocument.Parse(text);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var config = Config;
        if (string.IsNullOrWhiteSpace(config.CinephageUrl) || string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new CinephageException("Cinephage URL en API-key zijn nog niet ingesteld in de plugin.");
        }

        var baseUri = new Uri(config.CinephageUrl.TrimEnd('/') + "/");
        using var request = new HttpRequestMessage(method, new Uri(baseUri, path.TrimStart('/')));
        request.Headers.TryAddWithoutValidation("x-api-key", config.ApiKey.Trim());
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout ?? LookupTimeout);
        return await _http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
    }
}
