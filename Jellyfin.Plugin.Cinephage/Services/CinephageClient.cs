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
/// </summary>
public sealed class CinephageClient : IDisposable
{
    private const string PosterBaseUrl = "https://image.tmdb.org/t/p/w342";
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
        var all = (await movieTask.ConfigureAwait(false)).Concat(await seriesTask.ConfigureAwait(false)).ToList();

        await Task.WhenAll(all.Select(m => FillStatusAsync(m, cancellationToken))).ConfigureAwait(false);

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
        var id = tmdbId.ToString(CultureInfo.InvariantCulture);
        using var details = await GetJsonAsync("/api/tmdb/tv/" + id, cancellationToken).ConfigureAwait(false);
        using var state = await GetJsonAsync("/api/requests/media-status?mediaType=series&tmdbId=" + id, cancellationToken).ConfigureAwait(false);

        var available = new Dictionary<int, int>();
        if (state.RootElement.TryGetProperty("availableEpisodes", out var availableEpisodes) && availableEpisodes.ValueKind == JsonValueKind.Array)
        {
            // Entries look like "1x5" (season x episode).
            foreach (var entry in availableEpisodes.EnumerateArray())
            {
                var text = entry.GetString();
                var x = text?.IndexOf('x', StringComparison.Ordinal) ?? -1;
                if (x > 0 && int.TryParse(text.AsSpan(0, x), NumberStyles.Integer, CultureInfo.InvariantCulture, out var season))
                {
                    available[season] = available.GetValueOrDefault(season) + 1;
                }
            }
        }

        var requested = new HashSet<int>();
        if (state.RootElement.TryGetProperty("activeScopes", out var scopes) && scopes.ValueKind == JsonValueKind.Array)
        {
            foreach (var scope in scopes.EnumerateArray())
            {
                if (scope.TryGetProperty("seasons", out var seasons) && seasons.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in seasons.EnumerateArray())
                    {
                        if (s.TryGetInt32(out var n))
                        {
                            requested.Add(n);
                        }
                    }
                }

                if (scope.TryGetProperty("episodes", out var episodes) && episodes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in episodes.EnumerateArray())
                    {
                        if (e.TryGetProperty("seasonNumber", out var sn) && sn.TryGetInt32(out var n))
                        {
                            requested.Add(n);
                        }
                    }
                }
            }
        }

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
            var availableCount = available.GetValueOrDefault(number);
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
    /// Submits a request in Cinephage.
    /// </summary>
    /// <param name="mediaType">"movie" or "series".</param>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="seasons">The seasons to request (series only).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async Task<RequestOutcome> RequestAsync(string mediaType, int tmdbId, IReadOnlyList<int>? seasons, CancellationToken cancellationToken)
    {
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
    /// Gets the current status of all requests in Cinephage, keyed by request id.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Status per Cinephage request id.</returns>
    public async Task<Dictionary<string, string>> GetRequestStatusesAsync(CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync("/api/requests?take=1000", cancellationToken).ConfigureAwait(false);
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("requests", out var requests) && requests.ValueKind == JsonValueKind.Array)
        {
            foreach (var request in requests.EnumerateArray())
            {
                var id = GetString(request, "id");
                var status = GetString(request, "status");
                if (id is not null && status is not null)
                {
                    statuses[id] = status;
                }
            }
        }

        return statuses;
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

    private async Task FillStatusAsync(MediaResult item, CancellationToken cancellationToken)
    {
        try
        {
            var path = $"/api/requests/media-status?mediaType={item.MediaType}&tmdbId=" + item.TmdbId.ToString(CultureInfo.InvariantCulture);
            using var doc = await GetJsonAsync(path, cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            bool Flag(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            int Count(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;

            if (item.MediaType == "series")
            {
                // Whether all seasons are complete is only known per season (see GetSeasonsAsync).
                item.Status = Count("activeScopes") > 0 ? MediaStatus.Requested
                    : Count("availableEpisodes") > 0 ? MediaStatus.Partial
                    : Flag("inLibrary") ? MediaStatus.Wanted
                    : MediaStatus.None;
                return;
            }

            item.Status = Flag("hasFile") ? MediaStatus.Available
                : Flag("active") ? MediaStatus.Requested
                : Flag("inLibrary") ? MediaStatus.Wanted
                : MediaStatus.None;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not get Cinephage status for {Type} {TmdbId}", item.MediaType, item.TmdbId);
        }
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
