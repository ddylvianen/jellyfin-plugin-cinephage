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

    private readonly HttpClient _http;
    private readonly ILogger<CinephageClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CinephageClient"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{CinephageClient}"/> interface.</param>
    public CinephageClient(ILogger<CinephageClient> logger)
    {
        _logger = logger;
        _http = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? throw new CinephageException("Plugin is not initialized.");

    /// <summary>
    /// Searches TMDB through Cinephage and adds the library/request status for each movie.
    /// </summary>
    /// <param name="query">The search text.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matching movies.</returns>
    public async Task<IReadOnlyList<MovieResult>> SearchMoviesAsync(string query, CancellationToken cancellationToken)
    {
        var config = Config;
        var max = Math.Clamp(config.MaxResults, 1, 40);
        var path = "/api/discover/search?type=movie&query=" + Uri.EscapeDataString(query);

        using var doc = await GetJsonAsync(path, cancellationToken).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var movies = new List<MovieResult>();
        foreach (var item in results.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var tmdbId))
            {
                continue;
            }

            var releaseDate = GetString(item, "release_date");
            var posterPath = GetString(item, "poster_path");
            movies.Add(new MovieResult
            {
                TmdbId = tmdbId,
                Title = GetString(item, "title") ?? GetString(item, "original_title") ?? "?",
                Year = releaseDate is { Length: >= 4 } && int.TryParse(releaseDate.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ? year : null,
                Overview = GetString(item, "overview"),
                PosterUrl = string.IsNullOrEmpty(posterPath) ? null : PosterBaseUrl + posterPath
            });

            // Fetch a few extra so hiding available movies still fills the list.
            if (movies.Count >= max * 2)
            {
                break;
            }
        }

        await Task.WhenAll(movies.Select(m => FillStatusAsync(m, cancellationToken))).ConfigureAwait(false);

        return movies
            .Where(m => !(config.HideAvailable && m.Status == MovieStatus.Available))
            .Take(max)
            .ToList();
    }

    /// <summary>
    /// Submits a movie request in Cinephage.
    /// </summary>
    /// <param name="tmdbId">The TMDB id of the movie.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async Task<RequestOutcome> RequestMovieAsync(int tmdbId, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new { mediaType = "movie", tmdbId });
        using var response = await SendAsync(HttpMethod.Post, "/api/requests", body, cancellationToken).ConfigureAwait(false);
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
                    Message = "Aangevraagd! De film wordt gezocht."
                };
            }

            var code = GetString(root, "code");
            return new RequestOutcome
            {
                Success = false,
                Code = code,
                Message = code switch
                {
                    "duplicate_request" => "Deze film is al aangevraagd.",
                    "already_available" => "Deze film is al beschikbaar.",
                    "already_in_library" => "Deze film staat al in Cinephage en wordt gezocht.",
                    "blocked_media" => "Deze film is geblokkeerd in Cinephage.",
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
    /// Gets the current status of all movie requests in Cinephage, keyed by request id.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Status per Cinephage request id.</returns>
    public async Task<Dictionary<string, string>> GetRequestStatusesAsync(CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync("/api/requests?mediaType=movie&take=1000", cancellationToken).ConfigureAwait(false);
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

    private async Task FillStatusAsync(MovieResult movie, CancellationToken cancellationToken)
    {
        try
        {
            var path = "/api/requests/media-status?mediaType=movie&tmdbId=" + movie.TmdbId.ToString(CultureInfo.InvariantCulture);
            using var doc = await GetJsonAsync(path, cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            bool Flag(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

            movie.Status = Flag("hasFile") ? MovieStatus.Available
                : Flag("active") ? MovieStatus.Requested
                : Flag("inLibrary") ? MovieStatus.Wanted
                : MovieStatus.None;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not get Cinephage status for TMDB {TmdbId}", movie.TmdbId);
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

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, CancellationToken cancellationToken)
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

        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
